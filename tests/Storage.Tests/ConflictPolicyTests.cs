using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers FileZilla-style conflict policies for uploads, downloads, and transfers.</summary>
public sealed class ConflictPolicyTests
{
    private static readonly DateTimeOffset Old = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("dir/report.csv", 1, "dir/report (1).csv")]
    [InlineData("report", 3, "report (3)")]
    [InlineData(".env", 1, ".env (1)")]
    [InlineData("a/b.tar.gz", 2, "a/b.tar (2).gz")]
    public void Rename_candidates_insert_a_counter_before_the_extension(string path, int attempt, string expected)
    {
        Assert.Equal(expected, StorageConflictResolver.Candidate(path, attempt));
    }

    [Fact]
    public void Newer_uses_a_two_second_tolerance_and_treats_unknown_times_as_newer()
    {
        Assert.True(StorageConflictResolver.IsNewer(New, Old));
        Assert.False(StorageConflictResolver.IsNewer(Old, New));
        Assert.False(StorageConflictResolver.IsNewer(Old.AddSeconds(1), Old));
        Assert.True(StorageConflictResolver.IsNewer(null, Old));
        Assert.True(StorageConflictResolver.SizeDiffers(null, 1));
        Assert.False(StorageConflictResolver.SizeDiffers(5, 5));
    }

    [Theory]
    [InlineData(StorageConflictPolicy.Skip, "old", "report.txt")]
    [InlineData(StorageConflictPolicy.Overwrite, "new!", "report.txt")]
    [InlineData(StorageConflictPolicy.Rename, "old", "report (1).txt")]
    [InlineData(StorageConflictPolicy.OverwriteIfSizeDiffers, "new!", "report.txt")]
    public async Task Upload_policies_decide_what_happens_to_an_existing_file(StorageConflictPolicy policy, string expectedOriginal, string writtenPath)
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("report.txt", Encoding.UTF8.GetBytes("old"));

        var result = await storage.UploadBytesAsync("report.txt", Encoding.UTF8.GetBytes("new!"), new StorageUploadOptions { ConflictPolicy = policy });

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(writtenPath, result.Value!.Path);
        Assert.Equal(expectedOriginal, Encoding.UTF8.GetString((await storage.DownloadBytesAsync("report.txt")).Value!));
        if (policy == StorageConflictPolicy.Rename)
            Assert.Equal("new!", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("report (1).txt")).Value!));
    }

    [Fact]
    public async Task Fail_policy_reports_a_conflict()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("a.txt", [1]);

        var result = await storage.UploadBytesAsync("a.txt", [2], new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Fail });

        Assert.Equal(StorageErrors.ConflictCode, result.Error?.Code);
    }

    [Fact]
    public async Task Newer_only_upload_compares_the_source_time()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.Path);
        await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("current"));
        await storage.SetTimestampsAsync("a.txt", New);

        var older = await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("older"),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewer, SourceLastModified = Old });
        Assert.Equal("current", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("a.txt")).Value!));

        var newer = await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("newer"),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewer, SourceLastModified = New.AddDays(1) });
        Assert.True(older.IsSuccess && newer.IsSuccess);
        Assert.Equal("newer", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("a.txt")).Value!));
    }

    [Fact]
    public async Task Directory_copy_skips_existing_files_and_counts_them()
    {
        using var directory = new TestDirectory();
        var source = Local(directory.CreateDirectory("src"));
        var destination = Local(directory.CreateDirectory("dst"));
        await source.UploadBytesAsync("tree/a.txt", Encoding.UTF8.GetBytes("a-new"));
        await source.UploadBytesAsync("tree/b.txt", Encoding.UTF8.GetBytes("b-new"));
        await destination.UploadBytesAsync("copy/a.txt", Encoding.UTF8.GetBytes("a-old"));

        var copied = await StorageTransferCoordinator.CopyAsync(source, "tree", destination, "copy",
            new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Skip }, CancellationToken.None);

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.Equal(1, copied.Value!.Files);
        Assert.Equal(1, copied.Value.SkippedFiles);
        Assert.Equal(["tree/b.txt"], copied.Value.TransferredSources!);
        Assert.Equal("a-old", Encoding.UTF8.GetString((await destination.DownloadBytesAsync("copy/a.txt")).Value!));
        Assert.Equal("b-new", Encoding.UTF8.GetString((await destination.DownloadBytesAsync("copy/b.txt")).Value!));
    }

    [Fact]
    public async Task Directory_move_with_skips_deletes_only_the_transferred_sources()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var source = Local(directory.CreateDirectory("src"), "Source");
        var destination = Local(directory.CreateDirectory("dst"), "Destination");
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
        await source.UploadBytesAsync("tree/keep/a.txt", [1]);
        await source.UploadBytesAsync("tree/move/b.txt", [2]);
        await destination.UploadBytesAsync("moved/keep/a.txt", [9]);

        var moved = await library.MoveAsync("Source", "tree", "Destination", "moved",
            new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Skip });

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.True((await source.ExistsAsync("tree/keep/a.txt")).Value, "a skipped file must stay at the source");
        Assert.False((await source.ExistsAsync("tree/move/b.txt")).Value);
        Assert.False((await source.ExistsAsync("tree/move")).Value, "a directory emptied by the move is removed");
        Assert.Equal([2], (await destination.DownloadBytesAsync("moved/move/b.txt")).Value);
        Assert.Equal([9], (await destination.DownloadBytesAsync("moved/keep/a.txt")).Value);
    }

    [Fact]
    public async Task Single_file_transfer_with_rename_keeps_both_files()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var storage = Local(directory.CreateDirectory("data"), "Data");
        Assert.True(library.RegisterBackend("Data", storage).IsSuccess);
        await storage.UploadBytesAsync("a.txt", [1]);
        await storage.UploadBytesAsync("b.txt", [2]);

        var copied = await library.CopyAsync("Data", "a.txt", "Data", "b.txt",
            new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Rename });

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.Equal([2], (await storage.DownloadBytesAsync("b.txt")).Value);
        Assert.Equal([1], (await storage.DownloadBytesAsync("b (1).txt")).Value);
    }

    [Theory]
    [InlineData(StorageConflictPolicy.Skip, "local", false)]
    [InlineData(StorageConflictPolicy.Overwrite, "remote", false)]
    [InlineData(StorageConflictPolicy.Rename, "local", true)]
    public async Task Download_to_file_applies_the_policy_to_the_local_file(StorageConflictPolicy policy, string expected, bool renamed)
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.CreateDirectory("remote"));
        await storage.UploadBytesAsync("f.txt", Encoding.UTF8.GetBytes("remote"));
        var target = Path.Combine(directory.CreateDirectory("local"), "f.txt");
        await File.WriteAllTextAsync(target, "local");

        var result = await storage.DownloadToFileAsync("f.txt", target, conflictPolicy: policy);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(expected, await File.ReadAllTextAsync(target));
        Assert.Equal(renamed, File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "f (1).txt")));
    }

    private static LocalStorageBackend Local(string root, string id = "local") => new(id, new LocalConnectionConfig { RootPath = root });
}
