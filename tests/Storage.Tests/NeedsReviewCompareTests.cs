using System.Globalization;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Sync;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Compare, listing, and watch findings from the round-3 review (needs-review.md, sections A, B, C).</summary>
public sealed class NeedsReviewCompareTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly StorageCompareOptions ByChecksum = new() { CompareBy = StorageCompareBy.Checksum };

    // ------------------------------------------------------------ A22, A23: listings leave folders out whole

    [Fact] // needs-review A22: what an excluded folder holds is not kept path by path
    public async Task An_excluded_folder_is_kept_as_one_prefix_not_as_every_path_inside_it()
    {
        var storage = new MemoryStorage("m");
        for (var i = 0; i < 50; i++) storage.Put($"app/node_modules/pkg{i}/index.js", "x", Old);
        storage.Put("app/main.js", "m", Old);
        var options = new StorageCompareOptions { Exclude = ["**/node_modules"] };

        var tree = (await StorageCompare.ListTreeAsync(storage, "", options, new PathFilter(options), required: true, CancellationToken.None)).Value!;

        Assert.Equal(["app", "app/main.js"], tree.Items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, tree.Exclusions.Count);
        Assert.True(tree.Exclusions.Covers("app/node_modules/pkg7/index.js"));
        Assert.True(tree.Exclusions.Keeps("app"));
    }

    [Fact] // needs-review A23
    public async Task A_recursive_listing_without_hidden_items_leaves_out_what_hidden_folders_hold()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("root");
        Directory.CreateDirectory(Path.Combine(root, ".git", "refs"));
        File.WriteAllText(Path.Combine(root, ".git", "config"), "secret");
        File.WriteAllText(Path.Combine(root, ".git", "refs", "head"), "ref");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "hi");
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = root });

        var listed = (await storage.ListAsync("", new StorageListOptions { Recursive = true, IncludeHidden = false })).Value!;

        Assert.Equal(["readme.txt"], listed.Items.Select(item => item.Path));
    }

    // ------------------------------------------------------------ B56, B57: the hashing budget and unreadable files

    [Fact] // needs-review B56
    public async Task The_hashing_budget_is_taken_for_both_sides_at_once_or_not_at_all()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "aaa", Old);
        b.Put("f.txt", "bbb", Old);

        var diff = (await a.CompareAsync("", b, "", ByChecksum with { MaxHashedFiles = 1 })).Value!;

        Assert.True(Assert.Single(diff.Entries).Reasons.HasFlag(StorageDiffReason.Undecidable));
        Assert.Empty(a.Downloads);
        Assert.Empty(b.Downloads);
    }

    [Fact] // needs-review B56
    public async Task A_file_of_unknown_size_does_not_pass_a_byte_budget()
    {
        var a = new MemoryStorage("a") { NoSizes = true };
        var b = new MemoryStorage("b") { NoSizes = true };
        a.Put("f.txt", "aaa", Old);
        b.Put("f.txt", "aaa", Old);

        var diff = (await a.CompareAsync("", b, "", ByChecksum with { MaxHashedBytes = 1000 })).Value!;

        Assert.True(Assert.Single(diff.Entries).Reasons.HasFlag(StorageDiffReason.Undecidable));
        Assert.Empty(a.Downloads);
    }

    [Fact] // needs-review B57
    public async Task One_unreadable_file_leaves_its_pair_undecided_and_the_comparison_goes_on()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("locked.pst", "aaa", Old);
        b.Put("locked.pst", "aaa", Old);
        a.Put("ok.txt", "xxx", Old);
        b.Put("ok.txt", "yyy", Old);
        a.FailDownload = path => path == "locked.pst" ? StorageErrors.PermissionDenied("The file is locked by another process.") : null;

        var diff = await a.CompareAsync("", b, "", ByChecksum);

        Assert.True(diff.IsSuccess, diff.Error?.Message);
        Assert.True(diff.Value!.Entries.Single(entry => entry.RelativePath == "locked.pst").Reasons.HasFlag(StorageDiffReason.Undecidable));
        Assert.True(diff.Value.Entries.Single(entry => entry.RelativePath == "ok.txt").Reasons.HasFlag(StorageDiffReason.Checksum));
    }

    [Fact] // needs-review B57: a failure about the connection still fails the comparison
    public async Task A_connection_failure_while_hashing_still_fails_the_comparison()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "aaa", Old);
        b.Put("f.txt", "aaa", Old);
        a.FailDownload = _ => StorageErrors.ConnectionLost("The connection dropped.");

        var diff = await a.CompareAsync("", b, "", ByChecksum);

        Assert.Equal(StorageErrors.ConnectionLostCode, diff.Error?.Code);
    }

    // ------------------------------------------------------------ B58: links

    [Fact] // needs-review B58
    public async Task Follow_compares_what_a_link_points_to_when_the_provider_reports_the_link_itself()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("real.txt", "content", Old);
        a.PutLink("lnk.txt", "real.txt");
        a.PutLink("outside.txt", "../../etc/passwd");

        var diff = (await a.CompareAsync("", b, "", new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow })).Value!;

        var link = diff.Entries.Single(entry => entry.RelativePath == "lnk.txt");
        Assert.Equal(StorageItemType.File, link.Source!.ItemType);
        Assert.Equal(7, link.Source.Size);
        Assert.DoesNotContain(diff.Entries, entry => entry.RelativePath == "outside.txt");
    }

    [Fact] // needs-review B58
    public void A_link_target_resolves_inside_the_connection_only()
    {
        Assert.Equal("docs/b.txt", StorageCompare.ResolveLinkTarget("/memory", "docs/a.txt", "b.txt"));
        Assert.Equal("b.txt", StorageCompare.ResolveLinkTarget("/memory", "docs/a.txt", "../b.txt"));
        Assert.Equal("x/y.txt", StorageCompare.ResolveLinkTarget(@"C:\data\root", "a.txt", @"C:\data\root\x\y.txt"));
        Assert.Null(StorageCompare.ResolveLinkTarget("/memory", "a.txt", "../../outside.txt"));
        Assert.Null(StorageCompare.ResolveLinkTarget(@"C:\data\root", "a.txt", @"D:\elsewhere\y.txt"));
    }

    [Fact] // needs-review B58
    public async Task What_a_provider_lists_below_a_skipped_link_to_a_folder_is_left_out_too()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("real.txt", "r", Old);
        a.PutLink("linked", "/somewhere/else");
        a.Put("linked/inside.txt", "listed through the link", Old);

        var report = (await a.SyncAsync("", b, "")).Value!;

        Assert.Empty(report.Failed);
        Assert.Equal(["real.txt"], b.Paths());
    }

    // ------------------------------------------------------------ B60: checksums per side

    [Fact] // needs-review B60
    public async Task A_side_keeping_only_SHA256_is_not_downloaded()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "same", Old);
        b.Put("f.txt", "same", Old);
        a.ServerChecksum = (path, algorithm) => algorithm == StorageChecksumAlgorithm.Sha256 ? a.Digest(path, algorithm) : null;

        var diff = (await a.CompareAsync("", b, "", ByChecksum)).Value!;

        Assert.Equal(StorageDiffKind.Same, Assert.Single(diff.Entries).Kind);
        Assert.Empty(a.Downloads);
        Assert.Equal(1, b.Downloads["f.txt"]);
    }

    [Fact] // needs-review B60
    public async Task Sides_keeping_different_checksums_download_only_one_side()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "same", Old);
        b.Put("f.txt", "same", Old);
        a.ServerChecksum = (path, algorithm) => algorithm == StorageChecksumAlgorithm.Sha256 ? a.Digest(path, algorithm) : null;
        b.ServerChecksum = (path, algorithm) => algorithm == StorageChecksumAlgorithm.Md5 ? b.Digest(path, algorithm) : null;

        var diff = (await a.CompareAsync("", b, "", ByChecksum)).Value!;

        Assert.Equal(StorageDiffKind.Same, Assert.Single(diff.Entries).Kind);
        Assert.Equal(1, a.Downloads.Values.Sum() + b.Downloads.Values.Sum());
    }

    // ------------------------------------------------------------ B61: the kept modification time

    [Fact] // needs-review B61
    public void A_kept_time_without_an_offset_is_UTC_and_a_kept_time_ahead_of_the_server_is_still_used()
    {
        var written = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        StorageItem Item(string kept) => new()
        {
            Path = "f",
            Name = "f",
            ItemType = StorageItemType.File,
            LastModified = written,
            Metadata = new Dictionary<string, string> { [StorageCompareOptions.ModifiedMetadataKey] = kept }
        };

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), StorageCompare.EffectiveModified(Item("2024-01-01T00:00:00")));
        var ahead = written.AddMinutes(10);
        Assert.Equal(ahead, StorageCompare.EffectiveModified(Item(ahead.ToString("O", CultureInfo.InvariantCulture))));
    }

    // ------------------------------------------------------------ B68: inferred folders

    [Fact] // needs-review B68
    public void A_file_named_like_a_folder_does_not_hide_the_folder()
    {
        var items = new List<StorageItem>();
        static StorageItem Folder(string path) => new() { Path = path, Name = path, ItemType = StorageItemType.Directory };

        ImplicitDirectories.AddParents(items, "a/b/c", "", previous: "a/b", Folder);

        Assert.Contains(items, item => item.Path == "a/b");
        Assert.DoesNotContain(items, item => item.Path == "a");
    }

    // ------------------------------------------------------------ B69: items outside the listed folder

    [Fact] // needs-review B69
    public async Task A_folder_listed_in_the_servers_spelling_is_compared_not_dropped()
    {
        var a = new MemoryStorage("a", caseInsensitive: true);
        var b = new MemoryStorage("b");
        a.Put("Docs/x.txt", "x", Old);

        var diff = (await a.CompareAsync("docs", b, "")).Value!;

        Assert.Equal("x.txt", Assert.Single(diff.Entries).RelativePath);
    }

    [Fact] // needs-review B69
    public async Task An_item_listed_outside_the_folder_fails_the_comparison_instead_of_vanishing()
    {
        var odd = new FakeStorageBackend("odd", list: (_, _, _) => Task.FromResult(Result<StoragePage>.Success(new StoragePage(
            [new StorageItem { Path = "elsewhere/y.txt", Name = "y.txt", ItemType = StorageItemType.File, Size = 1 }], null))));
        var b = new MemoryStorage("b");

        var diff = await odd.CompareAsync("docs", b, "");

        Assert.Equal(StorageErrors.ProviderErrorCode, diff.Error?.Code);
    }

    // ------------------------------------------------------------ C: Unicode forms where case is ignored

    [Fact] // needs-review C
    public async Task Names_in_different_Unicode_forms_are_one_item_where_case_is_ignored()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("caf\u00e9.txt", "x", Old);
        b.Put("cafe\u0301.txt", "x", Old);

        var diff = (await a.CompareAsync("", b, "", new StorageCompareOptions { CaseInsensitive = true })).Value!;

        var entry = Assert.Single(diff.Entries);
        Assert.Equal(StorageDiffKind.Same, entry.Kind);
        Assert.Equal("cafe\u0301.txt", entry.DestinationRelativePath);
    }
}
