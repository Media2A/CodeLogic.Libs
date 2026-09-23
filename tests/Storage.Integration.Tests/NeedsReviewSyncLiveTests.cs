using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Sync review findings (needs-review.md, round 3) against real servers.</summary>
public sealed class NeedsReviewSyncLiveTests
{
    /// <summary>
    /// A local folder spelled "Docs" and a remote one spelled "docs": a file added under the local spelling lands
    /// in the remote's folder, and the next comparison still works (acceptance check R2-1).
    /// </summary>
    private static async Task FolderSpelledDifferentlyAsync(IStorageService local, string localRoot, IStorageService remote, string remoteRoot)
    {
        Directory.CreateDirectory(Path.Combine(localRoot, "Docs"));
        await File.WriteAllTextAsync(Path.Combine(localRoot, "Docs", "a.txt"), "same");
        Assert.True((await remote.UploadBytesAsync($"{remoteRoot}/docs/a.txt", Encoding.UTF8.GetBytes("same"))).IsSuccess);
        var options = new StorageSyncOptions
        {
            Direction = StorageSyncDirection.TwoWay,
            StateStore = new InMemoryStorageSyncStateStore(),
            SyncId = remoteRoot,
            Compare = new StorageCompareOptions { CaseInsensitive = true, CompareBy = StorageCompareBy.Size }
        };

        var first = await local.SyncAsync("", remote, remoteRoot, options);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "Docs", "new.txt"), "new");
        var second = await local.SyncAsync("", remote, remoteRoot, options);
        var listed = (await remote.ListAsync(remoteRoot, new StorageListOptions { Recursive = true })).Value!.Items.Select(item => item.Path).ToArray();
        var third = await local.CompareAsync("", remote, remoteRoot, options.Compare);

        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Empty(second.Value!.Failed);
        Assert.Contains($"{remoteRoot}/docs/new.txt", listed);
        Assert.DoesNotContain(listed, path => path.Contains("/Docs", StringComparison.Ordinal));
        Assert.True(third.IsSuccess, third.Error?.ToString());
    }

    [S3Fact] // needs-review A19 and E (live Local <-> S3 with case-insensitive compare)
    public async Task A_folder_spelled_differently_on_local_and_s3_gets_no_second_folder()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "cl-storage-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        await using var live = await LiveLibrary.StartAsync(("local", new LocalConnectionConfig { RootPath = localRoot }), ("s3", CloudEmulators.S3()));
        var s3 = live.Library.GetStorage("s3");
        var dir = $"review-case-{Guid.NewGuid():N}";
        try
        {
            await FolderSpelledDifferentlyAsync(live.Library.GetStorage("local"), localRoot, s3, dir);
        }
        finally
        {
            await s3.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            try { Directory.Delete(localRoot, recursive: true); } catch (IOException) { }
        }
    }

    [SftpFact] // needs-review A19 and E (live Local <-> SFTP with case-insensitive compare)
    public async Task A_folder_spelled_differently_on_local_and_sftp_gets_no_second_folder()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "cl-storage-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        await using var live = await LiveLibrary.StartAsync(("local", new LocalConnectionConfig { RootPath = localRoot }), ("sftp", LiveServers.Sftp()));
        var sftp = live.Library.GetStorage("sftp");
        var dir = $"review-case-{Guid.NewGuid():N}";
        try
        {
            await FolderSpelledDifferentlyAsync(live.Library.GetStorage("local"), localRoot, sftp, dir);
        }
        finally
        {
            await sftp.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            try { Directory.Delete(localRoot, recursive: true); } catch (IOException) { }
        }
    }

    [S3Fact] // needs-review A2 (sync side): a kept conflict copy on S3 is moved pinned to the version planned
    public async Task Keep_both_on_s3_keeps_both_versions()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "cl-storage-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        await using var live = await LiveLibrary.StartAsync(("local", new LocalConnectionConfig { RootPath = localRoot }), ("s3", CloudEmulators.S3()));
        var s3 = live.Library.GetStorage("s3");
        var dir = $"review-keepboth-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(Path.Combine(localRoot, "f.txt"), "ours");
            Assert.True((await s3.UploadBytesAsync($"{dir}/f.txt", Encoding.UTF8.GetBytes("theirs, longer"))).IsSuccess);

            var report = await live.Library.SyncAsync("local", "", "s3", dir, new StorageSyncOptions
            {
                Direction = StorageSyncDirection.TwoWay,
                ConflictPolicy = StorageSyncConflictPolicy.KeepBoth
            });

            Assert.True(report.IsSuccess, report.Error?.ToString());
            Assert.Empty(report.Value!.Failed);
            Assert.Empty(report.Value.Stale);
            Assert.Equal("ours", Encoding.UTF8.GetString((await s3.DownloadBytesAsync($"{dir}/f.txt")).Value!));
            var copy = (await s3.ListAsync(dir)).Value!.Items.Single(item => item.Name.Contains("(conflict", StringComparison.Ordinal));
            Assert.Equal("theirs, longer", Encoding.UTF8.GetString((await s3.DownloadBytesAsync(copy.Path)).Value!));
            Assert.Equal("theirs, longer", await File.ReadAllTextAsync(Path.Combine(localRoot, copy.Name)));
        }
        finally
        {
            await s3.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            try { Directory.Delete(localRoot, recursive: true); } catch (IOException) { }
        }
    }
}
