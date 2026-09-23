using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.S3;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Needs-review round 3, providers, against the live servers (the acceptance checks R2-2, R3-8, R3-9 among them).</summary>
public sealed class NeedsReviewProviderLiveTests
{
    // needs-review A10 (acceptance R2-2)
    [S3Fact]
    public async Task R2_2_an_S3_create_only_copy_keeps_its_properties_a_plain_ETag_and_the_server_MD5()
    {
        var prefix = $"accept-{Guid.NewGuid():N}";
        await using var live = await LiveLibrary.StartAsync(("s3", CloudEmulators.S3(c => c.Prefix = prefix)));
        var s3 = live.Library.GetStorage("s3");
        var payload = new byte[20 << 20];
        Random.Shared.NextBytes(payload);
        try
        {
            await using (var stream = new MemoryStream(payload))
                Assert.True((await s3.UploadAsync("r2-2/src.bin", stream, new StorageUploadOptions { ContentType = "application/x-test" })).IsSuccess);

            var copy = await live.Library.CopyAsync("s3", "r2-2/src.bin", "s3", "r2-2/dst.bin", new StorageTransferOptions { Overwrite = false });
            var info = await s3.GetInfoAsync("r2-2/dst.bin");
            var md5 = await s3.GetServerChecksumAsync("r2-2/dst.bin", StorageChecksumAlgorithm.Md5);

            Assert.Equal(StorageTransferOutcome.Completed, copy.Outcome);
            Assert.True(info.IsSuccess, info.Error?.ToString());
            Assert.DoesNotContain('-', info.Value!.ETag!);
            Assert.Equal("application/x-test", info.Value.ContentType);
            Assert.True(md5.IsSuccess, md5.Error?.ToString());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(payload)), md5.Value!.HexValue);
        }
        finally
        {
            await s3.DeleteAsync("r2-2", new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review A10 / B64 (contract C3): the probe says what the server really does with copy and delete conditions
    [S3Fact]
    public async Task The_S3_condition_probe_matches_what_the_server_does()
    {
        var config = CloudEmulators.S3(c => c.Prefix = $"probe-{Guid.NewGuid():N}");
        await using var storage = (S3StorageBackend)await CloudEmulators.CreateAsync(config);
        var source = (IStorageConditionEnforcementSource)storage;
        using var client = new AmazonS3Client(config.AccessKey, config.SecretKey,
            new AmazonS3Config { ServiceURL = config.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = config.Region, MaxErrorRetry = 0 });
        await storage.UploadBytesAsync("a.bin", [1]);
        await storage.UploadBytesAsync("b.bin", [2]);
        try
        {
            var copyEnforced = true;
            try
            {
                await client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = config.Bucket, SourceKey = $"{config.Prefix}/a.bin",
                    DestinationBucket = config.Bucket, DestinationKey = $"{config.Prefix}/b.bin",
                    IfNoneMatch = "*"
                });
                copyEnforced = false;
            }
            catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.PreconditionFailed) { }

            var reported = await source.GetEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true, default);
            var uploads = await source.GetEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: false, default);

            Assert.Equal(copyEnforced ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.CheckedBeforeCommit, reported);
            Assert.Equal(StorageConditionEnforcement.Atomic, uploads);
            var left = (await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Path).Order();
            Assert.Equal(["a.bin", "b.bin"], left);
        }
        finally
        {
            await storage.DeleteAsync("a.bin", new StorageDeleteOptions { IgnoreMissing = true });
            await storage.DeleteAsync("b.bin", new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review A2 / B23: a pinned server-side copy refuses a source that changed
    [S3Fact]
    public async Task An_S3_copy_pinned_to_an_old_ETag_is_refused_and_a_move_deletes_only_what_it_copied()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.S3(c => c.Prefix = $"pin-{Guid.NewGuid():N}"));
        var first = (await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("one"))).Value!;
        await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("two"));
        try
        {
            var stale = await storage.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { ExpectedSourceETag = first.ETag });
            var moved = await storage.MoveAsync("a.txt", "c.txt");

            Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
            Assert.False((await storage.ExistsAsync("b.txt")).Value);
            Assert.True(moved.IsSuccess, moved.Error?.ToString());
            Assert.False((await storage.ExistsAsync("a.txt")).Value);
            Assert.Equal("two", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("c.txt")).Value!));
        }
        finally
        {
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
                await storage.DeleteAsync(name, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }
}
