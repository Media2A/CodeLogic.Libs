using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Object-store folders that exist only as key prefixes appear once in recursive listings.</summary>
public sealed class ImplicitFolderLiveTests
{
    [S3Fact]
    public Task S3() => AssertImplicitFoldersAsync(CloudEmulators.S3());

    [AzureFact]
    public Task Azure() => AssertImplicitFoldersAsync(CloudEmulators.Azure());

    [SwiftFact]
    public Task Swift() => AssertImplicitFoldersAsync(CloudEmulators.Swift());

    [GcsFact]
    public Task Gcs() => AssertImplicitFoldersAsync(CloudEmulators.Gcs());

    private static async Task AssertImplicitFoldersAsync(StorageConnectionConfigBase config)
    {
        await using var storage = await CloudEmulators.CreateAsync(config);
        var root = $"implicit-{Guid.NewGuid():N}";
        try
        {
            foreach (var file in new[] { "a/b/1.txt", "a/b/2.txt", "a/c.txt", "d/3.txt" })
            {
                var put = await storage.UploadBytesAsync($"{root}/{file}", [1], new StorageUploadOptions { CreateParents = false });
                Assert.True(put.IsSuccess, put.Error?.ToString());
            }

            // Page size 1 forces every folder boundary to straddle a page.
            var folders = new List<string>();
            await foreach (var item in storage.EnumerateItemsAsync(root, new StorageListOptions { Recursive = true, PageSize = 1 }))
            {
                Assert.True(item.IsSuccess, item.Error?.ToString());
                if (item.Value!.ItemType == StorageItemType.Directory) folders.Add(item.Value.Path);
            }

            Assert.Equal([$"{root}/a", $"{root}/a/b", $"{root}/d"], folders.Order(StringComparer.Ordinal));
        }
        finally
        {
            await storage.DeleteAsync(root, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }
}

/// <summary>A refused server certificate is explained and carries the pin to trust it.</summary>
public sealed class TlsReasonLiveTests
{
    [FtpsFact]
    public async Task Untrusted_ftps_certificate_reports_the_reason_and_its_pin()
    {
        await using var storage = LiveServers.Create(new FtpConnectionConfig
        {
            Host = LiveServers.Env("CL_STORAGE_TEST_FTPS_HOST")!,
            Port = int.Parse(LiveServers.Env("CL_STORAGE_TEST_FTPS_PORT") ?? "21"),
            Username = LiveServers.Env("CL_STORAGE_TEST_FTPS_USER")!,
            Password = LiveServers.Env("CL_STORAGE_TEST_FTPS_PASS")!,
            EncryptionMode = StorageFtpEncryptionMode.Explicit,
            TimeoutSeconds = 15,
            Retry = new StorageRetryConfig { RetryCount = 0 }
        });

        var health = await storage.CheckHealthAsync();

        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(health.Error, StorageErrorInfo.TlsReasonKey, out var reason), health.Error?.Details);
        Assert.Equal("server_certificate_rejected", reason);
        Assert.True(StorageErrorInfo.TryGetDetail(health.Error, StorageErrorInfo.PresentedPublicKeyKey, out var pin));
        Assert.Equal(64, pin.Length);
    }
}
