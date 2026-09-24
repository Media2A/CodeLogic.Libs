using System.Diagnostics;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Tests;

/// <summary>Sync, compare, and listing-filter findings from the round-4 review (needs-review.md, R4-A2 and R4-B17 to R4-B22).</summary>
public sealed class NeedsReviewRound4SyncTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StorageSyncOptions TwoWay(IStorageSyncStateStore store) =>
        new() { Direction = StorageSyncDirection.TwoWay, StateStore = store, SyncId = "s" };

    private static StorageSyncOptions Mirror => new() { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true };

    // ------------------------------------------------------------ R4-A2: an empty folder has a spelling

    [Fact] // needs-review R4-A2 (acceptance check R4-2)
    public async Task A_new_file_goes_into_an_empty_folder_spelled_differently_at_the_destination()
    {
        var local = new MemoryStorage("local", caseInsensitive: true);
        var s3 = new MemoryStorage("s3");
        local.Put("docs/x.txt", "x", Old);
        s3.PutDirectory("Docs");
        var store = new InMemoryStorageSyncStateStore();
        var options = TwoWay(store) with { Compare = new StorageCompareOptions { CaseInsensitive = true } };

        var report = await local.SyncAsync("", s3, "", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.Equal(["Docs", "Docs/x.txt"], s3.Paths());
        var again = await local.CompareAsync("", s3, "", options.Compare);
        Assert.True(again.IsSuccess, again.Error?.Message);
        Assert.True(again.Value!.Identical);
    }

    [Fact] // needs-review R4-A2
    public async Task A_folder_holding_only_items_left_out_still_gives_its_spelling()
    {
        var source = new MemoryStorage("source", caseInsensitive: true);
        var destination = new MemoryStorage("destination");
        source.Put("docs/x.txt", "x", Old);
        destination.Put("Docs/.hidden", "h", Old);
        var compare = new StorageCompareOptions { CaseInsensitive = true, IncludeHidden = false };

        var report = await source.SyncAsync("", destination, "", new StorageSyncOptions { Compare = compare });

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.Equal(["Docs", "Docs/.hidden", "Docs/x.txt"], destination.Paths());
    }

    [Fact] // needs-review R4-A2
    public async Task A_kept_edit_copied_back_to_the_source_uses_the_sources_folder_spelling_not_the_baselines()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination", caseInsensitive: true);
        source.Put("docs/f.txt", "v1", Old);
        destination.Put("docs/f.txt", "v1", Old);
        var store = new InMemoryStorageSyncStateStore();
        var options = TwoWay(store) with
        {
            ConflictPolicy = StorageSyncConflictPolicy.KeepBoth,
            Compare = new StorageCompareOptions { CaseInsensitive = true }
        };
        Assert.True((await source.SyncAsync("", destination, "", options)).IsSuccess);

        // The source deletes the file and respells its folder; the destination edits the file.
        Assert.True((await source.DeleteAsync("docs", new StorageDeleteOptions { Recursive = true })).IsSuccess);
        source.PutDirectory("Docs");
        destination.Put("docs/f.txt", "edited at the destination", New);
        var report = await source.SyncAsync("", destination, "", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.Equal(["Docs", "Docs/f.txt"], source.Paths());
        Assert.Equal("edited at the destination", source.Text("Docs/f.txt"));
        Assert.True((await source.CompareAsync("", destination, "", options.Compare)).IsSuccess);
    }

    // ------------------------------------------------------------ R4-B17: one-way plans respect what the destination left out

    [Fact] // needs-review R4-B17
    public async Task A_destination_item_hidden_there_is_not_copied_onto_and_does_not_hold_back_deletes()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("desktop.ini", "source", Old);
        source.Put("keep.txt", "k", Old);
        destination.Put("desktop.ini", "destination's own", Old, hidden: true);
        destination.Put("keep.txt", "k", Old);
        destination.Put("extra.txt", "e", Old);
        var options = Mirror with { Compare = new StorageCompareOptions { IncludeHidden = false } };

        var report = await source.SyncAsync("", destination, "", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Stale);
        Assert.Empty(report.Value.Withheld);
        Assert.DoesNotContain(report.Value.Actions, action => action.RelativePath == "desktop.ini");
        Assert.Equal("destination's own", destination.Text("desktop.ini"));
        Assert.False(destination.Has("extra.txt"));
    }

    [Fact] // needs-review R4-B17
    public async Task Nothing_is_written_through_a_skipped_destination_link_to_a_folder()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("linked/a.txt", "a", Old);
        destination.Put("elsewhere/b.txt", "b", Old);
        destination.PutLink("linked", "elsewhere");

        var report = await source.SyncAsync("", destination, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror });

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.Empty(report.Value.Stale);
        Assert.DoesNotContain(report.Value.Actions, action => action.RelativePath.StartsWith("linked", StringComparison.Ordinal));
        Assert.Equal(["elsewhere", "elsewhere/b.txt", "linked"], destination.Paths());
    }

    // ------------------------------------------------------------ R4-B18: following links to folders and files

    [Fact] // needs-review R4-B18
    public async Task A_followed_link_to_a_folder_is_listed_with_its_targets_contents_and_Mirror_keeps_their_copies()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("real/x.txt", "x", Old);
        source.PutLink("lnk", "real");
        destination.Put("real/x.txt", "x", Old);
        destination.Put("lnk/x.txt", "x", Old);
        destination.Put("lnk/stray.txt", "s", Old);
        var options = Mirror with { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow } };

        var diff = await source.CompareAsync("", destination, "", options.Compare);
        Assert.True(diff.IsSuccess, diff.Error?.Message);
        Assert.Equal(StorageDiffKind.Same, diff.Value!.Entries.Single(entry => entry.RelativePath == "lnk/x.txt").Kind);

        source.Put("real/new.txt", "new content", Old);
        var report = await source.SyncAsync("", destination, "", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.Empty(report.Value.Stale);
        Assert.Equal("x", destination.Text("lnk/x.txt"));
        Assert.Equal("new content", destination.Text("lnk/new.txt"));
        Assert.Equal("new content", destination.Text("real/new.txt"));
        Assert.False(destination.Has("lnk/stray.txt"));
    }

    [Fact] // needs-review R4-B18
    public async Task A_followed_link_to_a_file_copies_the_targets_content_and_is_in_sync_afterwards()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("real.txt", "the target's content", Old);
        source.PutLink("lnk.txt", "real.txt");
        destination.Put("extra.txt", "e", Old);
        var options = Mirror with { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow } };

        var first = await source.SyncAsync("", destination, "", options);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Empty(first.Value!.Stale);
        Assert.Empty(first.Value.Withheld);
        Assert.Equal("the target's content", destination.Text("lnk.txt"));
        Assert.False(destination.Has("extra.txt"));

        var second = await source.SyncAsync("", destination, "", options);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Empty(second.Value!.Actions);
    }

    [Fact] // needs-review R4-B18
    public async Task A_link_leading_back_into_a_folder_being_listed_is_left_out_with_nothing_below_it_deleted()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("real/x.txt", "x", Old);
        source.PutLink("real/sub/up", "../../real");
        destination.Put("real/x.txt", "x", Old);
        destination.Put("real/sub/up/x.txt", "a real copy", Old);
        var options = Mirror with { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow } };

        var report = await source.SyncAsync("", destination, "", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.True(destination.Has("real/sub/up/x.txt"));
    }

    [Fact] // needs-review R4-B18
    public async Task Nothing_is_deleted_through_a_followed_link_at_the_destination()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("lnk/x.txt", "x", Old);
        destination.Put("target/x.txt", "x", Old);
        destination.Put("target/only-here.txt", "o", Old);
        destination.PutLink("synced/lnk", "../target");
        var options = new StorageSyncOptions
        {
            Direction = StorageSyncDirection.Mirror,
            DeleteExtraneous = true,
            Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow }
        };

        var report = await source.SyncAsync("", destination, "synced", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.True(destination.Has("target/only-here.txt"));
        Assert.Contains(report.Value!.Plan.Warnings, warning => warning.Contains("lnk/only-here.txt", StringComparison.Ordinal));
    }

    [Fact] // needs-review R4-B18 (a Windows junction, or a symbolic link elsewhere)
    public async Task A_local_link_to_a_folder_followed_lists_its_target_and_Mirror_copies_into_it_without_deleting()
    {
        using var root = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "real"));
        File.WriteAllText(Path.Combine(root.Path, "real", "inside.txt"), "inside");
        if (!TryCreateDirectoryLink(Path.Combine(root.Path, "linked"), Path.Combine(root.Path, "real"))) return;
        await using var source = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = root.Path });
        var destination = new MemoryStorage("destination");
        var options = Mirror with { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow, CompareBy = StorageCompareBy.Size } };

        var first = await source.SyncAsync("", destination, "", options);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Empty(first.Value!.Failed);
        Assert.Empty(first.Value.Stale);
        Assert.Equal("inside", destination.Text("linked/inside.txt"));
        Assert.Equal("inside", destination.Text("real/inside.txt"));

        var second = await source.SyncAsync("", destination, "", options);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Empty(second.Value!.Actions);
        Assert.Equal("inside", destination.Text("linked/inside.txt"));
    }

    [Fact] // needs-review R4-B18 (a real symbolic link to a folder; skipped where the OS does not allow creating one)
    public async Task A_local_symbolic_link_to_a_folder_followed_lists_its_target()
    {
        using var root = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "real"));
        File.WriteAllText(Path.Combine(root.Path, "real", "inside.txt"), "inside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root.Path, "linked"), Path.Combine(root.Path, "real"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return; // Creating symbolic links needs a privilege here.
        }
        await using var source = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = root.Path });
        var destination = new MemoryStorage("destination");
        destination.Put("linked/inside.txt", "inside", Old);
        var options = Mirror with { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow, CompareBy = StorageCompareBy.Size } };

        var report = await source.SyncAsync("", destination, "", options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.DoesNotContain(report.Value.Actions, action => action.Kind == StorageSyncActionKind.DeleteFromDestination);
        Assert.Equal("inside", destination.Text("linked/inside.txt"));
    }

    [Fact] // needs-review R4-B18 (a real symbolic link to a file; skipped where the OS does not allow creating one)
    public async Task A_local_symbolic_link_to_a_file_followed_copies_the_targets_content()
    {
        using var root = new TestDirectory();
        File.WriteAllText(Path.Combine(root.Path, "real.txt"), "the target");
        try
        {
            File.CreateSymbolicLink(Path.Combine(root.Path, "lnk.txt"), Path.Combine(root.Path, "real.txt"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return; // Creating symbolic links needs a privilege here.
        }
        await using var source = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = root.Path });
        var destination = new MemoryStorage("destination");
        var options = Mirror with { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Follow, CompareBy = StorageCompareBy.Size } };

        var first = await source.SyncAsync("", destination, "", options);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Empty(first.Value!.Stale);
        Assert.Equal("the target", destination.Text("lnk.txt"));

        var second = await source.SyncAsync("", destination, "", options);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Empty(second.Value!.Actions);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            // A junction needs no privilege, unlike a symbolic link.
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ R4-B19: hidden folders on later pages

    [Fact] // needs-review R4-B19
    public async Task A_paged_object_store_listing_hides_the_contents_of_a_hidden_folder_on_every_page()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put(".git/a", [1]);
        fake.Put(".git/b", [2]);
        fake.Put(".git/objects/c", [3]);
        fake.Put("visible.txt", [4]);
        await using var s3 = new S3StorageBackend("s3", fake.Client, "bucket");

        var paths = new List<string>();
        await foreach (var item in s3.EnumerateItemsAsync("", new StorageListOptions { Recursive = true, IncludeHidden = false, PageSize = 1 }))
        {
            Assert.True(item.IsSuccess, item.Error?.Message);
            paths.Add(item.Value!.Path);
        }

        Assert.Equal(["visible.txt"], paths);
    }

    [Fact] // needs-review R4-B19
    public void The_hidden_rule_tests_every_folder_between_the_listed_folder_and_an_item()
    {
        StorageItem File(string path) => new() { Path = path, Name = path[(path.LastIndexOf('/') + 1)..], ItemType = StorageItemType.File };
        var options = new StorageListOptions { Recursive = true, IncludeHidden = false };

        var fromRoot = CL.Storage.Providers.StorageListFilter.Apply([File("a/.git/b"), File("a/c")], options, root: "").Select(item => item.Path);
        var insideHidden = CL.Storage.Providers.StorageListFilter.Apply([File(".config/app/x"), File(".config/app/.y/z")], options, root: ".config").Select(item => item.Path);

        Assert.Equal(["a/c"], fromRoot);
        Assert.Equal([".config/app/x"], insideHidden);
    }

    // ------------------------------------------------------------ R4-B20: no time tolerance for what is deleted or replaced

    [Fact] // needs-review R4-B20
    public async Task A_same_size_edit_within_the_time_tolerance_is_not_deleted()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination") { NoETags = true };
        source.Put("keep.txt", "k", Old);
        destination.Put("keep.txt", "k", Old);
        destination.Put("extra.txt", "before", Old);
        var plan = (await source.PlanSyncAsync("", destination, "", Mirror)).Value!;
        Assert.Contains(plan.Actions, action => action.Kind == StorageSyncActionKind.DeleteFromDestination && action.RelativePath == "extra.txt");

        destination.Put("extra.txt", "edited", Old.AddSeconds(1));
        var report = await source.ApplySyncAsync("", destination, "", plan, plan.Digest, Mirror);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Equal("edited", destination.Text("extra.txt"));
        Assert.Equal(StorageSyncActionOutcome.Stale, report.Value!.Results.Single(result => result.Action.RelativePath == "extra.txt").Outcome);
    }

    [Fact] // needs-review R4-B20
    public async Task A_same_size_edit_within_the_time_tolerance_is_not_overwritten()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination") { NoETags = true };
        source.Put("f.txt", "source", New);
        destination.Put("f.txt", "before", Old);
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror };
        var plan = (await source.PlanSyncAsync("", destination, "", options)).Value!;
        Assert.Contains(plan.Actions, action => action.Kind == StorageSyncActionKind.CopyToDestination);

        destination.Put("f.txt", "edited", Old.AddSeconds(1));
        var report = await source.ApplySyncAsync("", destination, "", plan, plan.Digest, options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Equal("edited", destination.Text("f.txt"));
        Assert.Equal(StorageSyncActionOutcome.Stale, report.Value!.Results.Single().Outcome);
    }

    [Fact] // needs-review R4-B20
    public async Task A_same_size_edit_within_the_time_tolerance_is_not_renamed_aside()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination") { NoETags = true };
        source.Put("f.txt", "ours!", New);
        destination.Put("f.txt", "theirs", Old);
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, ConflictPolicy = StorageSyncConflictPolicy.KeepBoth };
        var plan = (await source.PlanSyncAsync("", destination, "", options)).Value!;
        Assert.Contains(plan.Actions, action => action.Kind == StorageSyncActionKind.RenameAtDestination);

        destination.Put("f.txt", "edited", Old.AddSeconds(1));
        var report = await source.ApplySyncAsync("", destination, "", plan, plan.Digest, options);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Equal(StorageSyncActionOutcome.Stale, report.Value!.Results.Single(result => result.Action.Kind == StorageSyncActionKind.RenameAtDestination).Outcome);
        Assert.Equal("edited", destination.Text("f.txt"));
        Assert.Equal(["f.txt"], destination.Paths());
    }

    // ------------------------------------------------------------ R4-B21: what the plan is bound to

    [Fact] // needs-review R4-B21
    public async Task A_plan_shown_with_its_conflicts_can_have_its_other_steps_applied()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("both.txt", "ours!", New);
        destination.Put("both.txt", "theirs", Old);
        source.Put("new.txt", "n", Old);
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay };
        var plan = (await source.PlanSyncAsync("", destination, "", options)).Value!;
        Assert.False(plan.IsApprovable);

        var report = await source.ApplySyncAsync("", destination, "", plan, plan.Digest, options with { ApplyWithConflicts = true });

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Equal("n", destination.Text("new.txt"));
        Assert.Equal("theirs", destination.Text("both.txt"));
    }

    [Fact] // needs-review R4-B21
    public async Task How_many_files_are_hashed_at_once_does_not_bind_the_plan()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("new.txt", "n", Old);
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror };
        var plan = (await source.PlanSyncAsync("", destination, "", options)).Value!;

        var report = await source.ApplySyncAsync("", destination, "", plan, plan.Digest,
            options with { Compare = options.Compare with { HashConcurrency = 16 } });

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Equal("n", destination.Text("new.txt"));
    }

    // ------------------------------------------------------------ R4-B22: smaller

    [Fact] // needs-review R4-B22
    public async Task A_folder_deleted_on_one_side_is_deleted_although_a_crashed_transfer_left_old_staging_in_it()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("d/f.txt", "f", Old);
        destination.Put("d/f.txt", "f", Old);
        source.Put("keep.txt", "k", Old);
        destination.Put("keep.txt", "k", Old);
        var store = new InMemoryStorageSyncStateStore();
        Assert.True((await source.SyncAsync("", destination, "", TwoWay(store))).IsSuccess);

        Assert.True((await source.DeleteAsync("d", new StorageDeleteOptions { Recursive = true })).IsSuccess);
        destination.Put("d/.cl-storage-transfer-0123456789abcdef.tmp", "partial", DateTimeOffset.UtcNow.AddDays(-3));
        var report = await source.SyncAsync("", destination, "", TwoWay(store));

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Stale);
        Assert.False(destination.Has("d"));
    }

    [Fact] // needs-review R4-B22
    public async Task A_folder_is_kept_while_it_holds_fresh_staging_or_a_backup()
    {
        foreach (var (name, age) in new[] { (".cl-storage-transfer-0123456789abcdef.tmp", TimeSpan.FromMinutes(5)), (".cl-storage-transfer-backup-0123456789abcdef", TimeSpan.FromDays(30)) })
        {
            var source = new MemoryStorage("source");
            var destination = new MemoryStorage("destination");
            source.Put("d/f.txt", "f", Old);
            destination.Put("d/f.txt", "f", Old);
            source.Put("keep.txt", "k", Old);
            destination.Put("keep.txt", "k", Old);
            var store = new InMemoryStorageSyncStateStore();
            Assert.True((await source.SyncAsync("", destination, "", TwoWay(store))).IsSuccess);

            Assert.True((await source.DeleteAsync("d", new StorageDeleteOptions { Recursive = true })).IsSuccess);
            destination.Put($"d/{name}", "partial", DateTimeOffset.UtcNow - age);
            var report = await source.SyncAsync("", destination, "", TwoWay(store));

            Assert.True(report.IsSuccess, report.Error?.Message);
            Assert.True(destination.Has($"d/{name}"), name);
            Assert.Equal(StorageSyncActionOutcome.Stale, report.Value!.Results.Single(result => result.Action.RelativePath == "d").Outcome);
        }
    }

    [Fact] // needs-review R4-B22
    public async Task A_provider_whose_listing_throws_fails_the_plan_and_the_comparison()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("a.txt", "a", Old);
        destination.FailList = (_, _) => throw new InvalidOperationException("The provider broke.");

        var plan = await source.PlanSyncAsync("", destination, "", new StorageSyncOptions());
        var diff = await source.CompareAsync("", destination, "");

        Assert.True(plan.IsFailure);
        Assert.True(diff.IsFailure);
    }

    [Fact] // needs-review R4-B22
    public async Task A_filter_pattern_that_runs_too_long_fails_the_plan()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put(new string('a', 400) + ".txt", "a", Old);
        var options = new StorageSyncOptions { Compare = new StorageCompareOptions { NamePattern = string.Concat(Enumerable.Repeat("*a", 40)) + "*b" } };

        var plan = await source.PlanSyncAsync("", destination, "", options);

        Assert.True(plan.IsFailure);
        Assert.Equal(StorageErrors.InvalidContentCode, plan.Error!.Code);
    }

    [Fact] // needs-review R4-B22
    public async Task A_step_that_failed_on_its_own_while_the_run_was_stopping_is_reported_failed()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("a.txt", "a", Old);
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, ItemRetries = 0 };
        var plan = (await source.PlanSyncAsync("", destination, "", options)).Value!;
        using var stop = new CancellationTokenSource();
        source.FailGetInfo = path =>
        {
            stop.Cancel();
            return StorageErrors.PermissionDenied($"'{path}' cannot be read.");
        };

        var report = await source.ApplySyncAsync("", destination, "", plan, plan.Digest, options, stop.Token);

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.True(report.Value!.Cancelled);
        var step = report.Value.Results.Single();
        Assert.Equal(StorageSyncActionOutcome.Failed, step.Outcome);
        Assert.Equal(StorageErrors.PermissionDeniedCode, step.Error!.Code);
    }

    [Fact] // needs-review R4-B22 (B45)
    public async Task Baseline_entries_below_an_excluded_folder_go_once_neither_side_has_it()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination");
        source.Put("cache/a.txt", "a", Old);
        destination.Put("cache/a.txt", "a", Old);
        source.Put("kept/b.txt", "b", Old);
        destination.Put("kept/b.txt", "b", Old);
        var store = new InMemoryStorageSyncStateStore();
        Assert.True((await source.SyncAsync("", destination, "", TwoWay(store))).IsSuccess);
        var excluding = TwoWay(store) with { Compare = new StorageCompareOptions { Exclude = ["cache", "kept"] } };

        // Excluded while still there on one side: the entries are kept, so a deletion is not undone later.
        Assert.True((await source.DeleteAsync("cache", new StorageDeleteOptions { Recursive = true })).IsSuccess);
        Assert.True((await source.DeleteAsync("kept", new StorageDeleteOptions { Recursive = true })).IsSuccess);
        Assert.True((await source.SyncAsync("", destination, "", excluding)).IsSuccess);
        var entries = (await store.LoadAsync("s", default))!.Entries;
        Assert.Contains("cache/a.txt", entries.Keys);
        Assert.Contains("kept/b.txt", entries.Keys);

        // Gone from both sides: nothing is left to protect.
        Assert.True((await destination.DeleteAsync("cache", new StorageDeleteOptions { Recursive = true })).IsSuccess);
        Assert.True((await source.SyncAsync("", destination, "", excluding)).IsSuccess);
        entries = (await store.LoadAsync("s", default))!.Entries;
        Assert.DoesNotContain(entries.Keys, path => path.StartsWith("cache", StringComparison.Ordinal));
        Assert.Contains("kept/b.txt", entries.Keys);
    }

    [Fact] // needs-review R4-B22 (A25)
    public async Task A_copy_whose_read_back_fails_records_no_size_only_identity_so_a_later_same_size_edit_is_not_overwritten()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination", setTimestamps: false);
        source.Put("f.txt", "v1", Old);
        var failing = true;
        destination.FailGetInfo = path => failing && path == "f.txt" && destination.Has("f.txt")
            ? StorageErrors.ConnectionLost("The connection dropped.")
            : null;
        var store = new InMemoryStorageSyncStateStore();
        var first = await source.SyncAsync("", destination, "", TwoWay(store));
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal("v1", destination.Text("f.txt"));
        failing = false;

        // A same-size edit at the destination, and a change at the source.
        destination.Put("f.txt", "d2", DateTimeOffset.UtcNow);
        source.Put("f.txt", "s2", New);
        await source.SyncAsync("", destination, "", TwoWay(store));

        Assert.Equal("d2", destination.Text("f.txt"));
    }
}
