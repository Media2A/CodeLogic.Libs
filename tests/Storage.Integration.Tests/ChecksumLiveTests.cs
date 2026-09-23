using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Server-reported checksums must match the content, and helpers must fall back when a server has none.</summary>
public sealed class ChecksumLiveTests
{
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("checksum me please");
    private static readonly string Md5 = Convert.ToHexStringLower(MD5.HashData(Content));

    [S3Fact]
    public async Task S3_reports_the_etag_md5() => await ServerMd5Async(await CloudEmulators.CreateAsync(CloudEmulators.S3()));

    [AzureFact]
    public async Task Azure_reports_the_content_md5() => await ServerMd5Async(await CloudEmulators.CreateAsync(CloudEmulators.Azure()));

    [GcsFact]
    public async Task Gcs_reports_the_object_md5() => await ServerMd5Async(await CloudEmulators.CreateAsync(CloudEmulators.Gcs()));

    [SwiftFact]
    public async Task Swift_reports_the_etag_md5() => await ServerMd5Async(await CloudEmulators.CreateAsync(CloudEmulators.Swift()));

    [FtpFact]
    public async Task Ftp_without_hash_support_falls_back_to_computing()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        await WithFileAsync(storage, async path =>
        {
            var server = await storage.GetServerChecksumAsync(path, StorageChecksumAlgorithm.Md5);
            Assert.Equal(StorageErrors.UnsupportedCode, server.Error?.Code);

            var computed = await storage.ComputeChecksumAsync(path, StorageChecksumAlgorithm.Md5);
            Assert.True(computed.IsSuccess, computed.Error?.ToString());
            Assert.Equal(StorageChecksumSource.Computed, computed.Value!.Source);
            Assert.Equal(Md5, computed.Value.HexValue);

            var serverOnly = await storage.ComputeChecksumAsync(path, StorageChecksumAlgorithm.Md5, mode: StorageChecksumMode.ServerOnly);
            Assert.Equal(StorageErrors.UnsupportedCode, serverOnly.Error?.Code);
        });
    }

    private static async Task ServerMd5Async(IStorageBackend storage)
    {
        await using (storage)
        {
            await WithFileAsync(storage, async path =>
            {
                var server = await storage.GetServerChecksumAsync(path, StorageChecksumAlgorithm.Md5);
                Assert.True(server.IsSuccess, server.Error?.ToString());
                Assert.Equal(StorageChecksumSource.Server, server.Value!.Source);
                Assert.Equal(Md5, server.Value.HexValue);

                var verified = await storage.VerifyChecksumAsync(path, Md5, StorageChecksumAlgorithm.Md5);
                Assert.True(verified.Value!.Matches);
                Assert.Equal(StorageChecksumSource.Server, verified.Value.Actual.Source);

                var computed = await storage.ComputeChecksumAsync(path, StorageChecksumAlgorithm.Md5, mode: StorageChecksumMode.ComputeOnly);
                Assert.Equal(Md5, computed.Value!.HexValue);
                Assert.Equal(StorageChecksumSource.Computed, computed.Value.Source);

                // SHA-512 is stored by none of these services, so the helper computes it.
                var sha512 = await storage.ComputeChecksumAsync(path, StorageChecksumAlgorithm.Sha512);
                Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(Content)), sha512.Value!.HexValue);
                Assert.Equal(StorageChecksumSource.Computed, sha512.Value.Source);
            });
        }
    }

    private static async Task WithFileAsync(IStorageBackend storage, Func<string, Task> test)
    {
        var dir = $"sum-{Guid.NewGuid():N}";
        var path = $"{dir}/file.bin";
        var upload = await storage.UploadBytesAsync(path, Content);
        Assert.True(upload.IsSuccess, upload.Error?.ToString());
        try { await test(path); }
        finally { await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }
}
