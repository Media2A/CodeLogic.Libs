using CL.Storage.Configuration;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Round-4 transfer findings on real servers (needs-review.md, R4-A1 and R4-A9).</summary>
public sealed class NeedsReviewRound4TransferLiveTests
{
    private static readonly StorageSessionConfig OneSession = new() { MaxSessions = 1, MaxIdleSessions = 1, AcquireTimeoutSeconds = 5 };

    // needs-review R4-A1 (run R4-1): a same-server rename under ConflictPolicy.Rename is the server's rename
    [SftpFact]
    public Task An_SFTP_rename_move_with_one_session_uses_the_servers_rename() =>
        RenameMoveAsync(LiveServers.Sftp(c => c.Session = OneSession));

    // needs-review R4-A1
    [FtpFact]
    public Task An_FTP_rename_move_with_one_session_uses_the_servers_rename() =>
        RenameMoveAsync(LiveServers.Ftp(c => c.Session = OneSession));

    // needs-review R4-A1: WebDAV cannot pin a move, so the source is compared just before its MOVE
    [WebDavFact]
    public Task A_WebDAV_rename_move_uses_the_servers_move() => RenameMoveAsync(LiveServers.WebDav(), checkedBefore: true);

    private static async Task RenameMoveAsync(StorageConnectionConfigBase config, bool checkedBefore = false)
    {
        await using var live = await LiveLibrary.StartAsync(("s", config));
        var storage = live.Library.GetStorage("s");
        var dir = $"r4-rename-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/f.txt", [1, 2, 3]);
            await storage.UploadBytesAsync($"{dir}/g.txt", [9]);

            var report = await live.Library.MoveAsync("s", $"{dir}/f.txt", "s", $"{dir}/g.txt",
                new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Rename });

            Assert.True(report.IsSuccess, report.Error?.ToString());
            Assert.Equal($"{dir}/g (1).txt", report.WrittenPath);
            Assert.True(report.SourceDeleted);
            if (checkedBefore)
                Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, report.ConditionEnforcement);
            Assert.Equal([1, 2, 3], (await storage.DownloadBytesAsync($"{dir}/g (1).txt")).Value!);
            Assert.Equal([9], (await storage.DownloadBytesAsync($"{dir}/g.txt")).Value!);
            var left = (await storage.ListAsync(dir, new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Name).Order();
            Assert.Equal(["g (1).txt", "g.txt"], left);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review R4-A9: the part file's lock marker is created, held through the promote, and removed
    [WebDavFact]
    public async Task A_resumable_copy_to_WebDAV_leaves_no_lock_marker()
    {
        var local = Path.Combine(Path.GetTempPath(), "cl-storage-live-src", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        await using var live = await LiveLibrary.StartAsync(("local", new LocalConnectionConfig { RootPath = local }), ("dav", LiveServers.WebDav()));
        var dav = live.Library.GetStorage("dav");
        var dir = $"r4-lock-{Guid.NewGuid():N}";
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        try
        {
            await live.Library.GetStorage("local").UploadBytesAsync("big.bin", content);

            var report = await live.Library.CopyAsync("local", "big.bin", "dav", $"{dir}/big.bin",
                new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume });

            Assert.True(report.IsSuccess, report.Error?.ToString());
            Assert.Equal(content, (await dav.DownloadBytesAsync($"{dir}/big.bin")).Value!);
            var left = (await dav.ListAsync(dir, new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Name);
            Assert.Equal(["big.bin"], left);
        }
        finally
        {
            await dav.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            try { Directory.Delete(local, recursive: true); } catch { }
        }
    }
}
