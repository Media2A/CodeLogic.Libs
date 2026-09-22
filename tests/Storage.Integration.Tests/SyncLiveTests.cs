using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Models;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Skips unless both an SFTP server and an S3-compatible endpoint are configured.</summary>
public sealed class SftpAndS3FactAttribute : FactAttribute
{
    public SftpAndS3FactAttribute()
    {
        if (!LiveServers.SftpConfigured || !CloudEmulators.S3Configured)
            Skip = "Set CL_STORAGE_TEST_SFTP_* and CL_STORAGE_TEST_S3_* to run cross-provider sync tests.";
    }
}

/// <summary>Cross-provider sync against real servers.</summary>
public sealed class SyncLiveTests
{
    [SftpAndS3Fact]
    public async Task Sftp_to_s3_sync_is_idempotent_even_though_s3_cannot_keep_timestamps()
    {
        await using var sftp = LiveServers.Create(LiveServers.Sftp());
        await using var s3 = await CloudEmulators.CreateAsync(CloudEmulators.S3());
        var tree = $"sync-{Guid.NewGuid():N}";
        try
        {
            await sftp.UploadBytesAsync($"{tree}/a.txt", Encoding.UTF8.GetBytes("alpha"));
            await sftp.UploadBytesAsync($"{tree}/nested/b.txt", Encoding.UTF8.GetBytes("bravo"));
            Assert.False(s3.Capabilities.Supports(StorageFeature.SetTimestamps));

            var first = (await sftp.SyncAsync(tree, s3, tree)).Value!;
            var second = (await sftp.SyncAsync(tree, s3, tree)).Value!;

            Assert.Equal(2, first.Copied);
            Assert.Empty(first.Failed);
            Assert.Equal("bravo", Encoding.UTF8.GetString((await s3.DownloadBytesAsync($"{tree}/nested/b.txt")).Value!));
            // The S3 copy is newer than its source and the same size, so nothing is copied again.
            Assert.Equal(0, second.Copied);
        }
        finally
        {
            await sftp.DeleteAsync(tree, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            await s3.DeleteAsync(tree, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    [SftpFact]
    public async Task Sync_preserves_timestamps_on_sftp()
    {
        await using var sftp = LiveServers.Create(LiveServers.Sftp());
        var tree = $"sync-ts-{Guid.NewGuid():N}";
        var stamp = new DateTimeOffset(2022, 2, 2, 2, 2, 2, TimeSpan.Zero);
        try
        {
            await sftp.UploadBytesAsync($"{tree}/src/f.txt", Encoding.UTF8.GetBytes("x"));
            await sftp.SetTimestampsAsync($"{tree}/src/f.txt", stamp);

            var report = (await sftp.SyncAsync($"{tree}/src", sftp, $"{tree}/dst")).Value!;

            Assert.Equal(1, report.Copied);
            Assert.Equal(stamp, (await sftp.GetInfoAsync($"{tree}/dst/f.txt")).Value!.LastModified);
            Assert.True((await sftp.CompareAsync($"{tree}/src", sftp, $"{tree}/dst")).Value!.Identical);
        }
        finally { await sftp.DeleteAsync(tree, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }
}
