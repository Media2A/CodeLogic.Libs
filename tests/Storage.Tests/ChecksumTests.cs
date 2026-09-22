using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers conversion of provider digests and the server-first checksum helpers.</summary>
public sealed class ChecksumTests
{
    [Theory]
    [InlineData("\"5d41402abc4b2a76b9719d911017c592\"", true)]
    [InlineData("5D41402ABC4B2A76B9719D911017C592", true)]
    [InlineData("5d41402abc4b2a76b9719d911017c592-3", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void Md5_hex_etags_are_accepted_only_when_they_are_plain_digests(string etag, bool accepted)
    {
        var result = ProviderChecksums.FromHex(StorageChecksumAlgorithm.Md5, etag);

        Assert.Equal(accepted, result.IsSuccess);
        if (accepted)
        {
            Assert.Equal("5d41402abc4b2a76b9719d911017c592", result.Value!.HexValue);
            Assert.Equal(StorageChecksumSource.Server, result.Value.Source);
            Assert.Equal(0, result.Value.BytesProcessed);
        }
        else
        {
            Assert.Equal(StorageErrors.UnsupportedCode, result.Error?.Code);
        }
    }

    [Fact]
    public void Composite_base64_checksums_are_not_reported_as_whole_content_digests()
    {
        var digest = Convert.ToBase64String(new byte[32]);

        Assert.True(ProviderChecksums.FromBase64(StorageChecksumAlgorithm.Sha256, digest).IsSuccess);
        Assert.False(ProviderChecksums.FromBase64(StorageChecksumAlgorithm.Sha256, digest + "-4").IsSuccess);
        Assert.False(ProviderChecksums.FromBase64(StorageChecksumAlgorithm.Md5, digest).IsSuccess);
    }

    [Fact]
    public async Task Connections_without_server_checksums_compute_and_say_so()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        await storage.UploadBytesAsync("a.bin", [1, 2, 3]);

        var computed = await storage.ComputeChecksumAsync("a.bin");
        var serverOnly = await storage.ComputeChecksumAsync("a.bin", mode: StorageChecksumMode.ServerOnly);

        Assert.Equal(StorageChecksumSource.Computed, computed.Value!.Source);
        Assert.Equal(3, computed.Value.BytesProcessed);
        Assert.Equal(StorageErrors.UnsupportedCode, serverOnly.Error?.Code);
        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.GetServerChecksumAsync("a.bin")).Error?.Code);
    }
}
