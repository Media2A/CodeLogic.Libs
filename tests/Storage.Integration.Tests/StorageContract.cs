using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Provider-neutral behaviour every live backend must satisfy.</summary>
internal static class StorageContract
{
    public static async Task RoundTripAsync(IStorageBackend storage)
    {
        var dir = $"cl-it-{Guid.NewGuid():N}";
        var content = Encoding.UTF8.GetBytes("hello live storage");
        try
        {
            var upload = await storage.UploadBytesAsync($"{dir}/nested/a.txt", content);
            Assert.True(upload.IsSuccess, upload.Error?.ToString());

            var info = await storage.GetInfoAsync($"{dir}/nested/a.txt");
            Assert.True(info.IsSuccess, info.Error?.ToString());
            Assert.Equal(StorageItemType.File, info.Value!.ItemType);
            Assert.Equal(content.Length, info.Value.Size);
            Assert.True((await storage.ExistsAsync($"{dir}/nested/a.txt")).Value);

            var listing = await storage.ListAsync($"{dir}/nested");
            Assert.True(listing.IsSuccess, listing.Error?.ToString());
            Assert.Contains(listing.Value!.Items, item => item.Name == "a.txt");

            var download = await storage.DownloadBytesAsync($"{dir}/nested/a.txt");
            Assert.Equal(content, download.Value);

            var range = await storage.DownloadBytesAsync($"{dir}/nested/a.txt", new StorageDownloadOptions { Offset = 6, Length = 4 });
            Assert.Equal("live", Encoding.UTF8.GetString(range.Value!));

            var copy = await storage.CopyAsync($"{dir}/nested/a.txt", $"{dir}/b.txt");
            Assert.True(copy.IsSuccess, copy.Error?.ToString());
            var move = await storage.MoveAsync($"{dir}/b.txt", $"{dir}/c.txt");
            Assert.True(move.IsSuccess, move.Error?.ToString());
            Assert.False((await storage.ExistsAsync($"{dir}/b.txt")).Value);

            var conflict = await storage.UploadBytesAsync($"{dir}/c.txt", content, new StorageUploadOptions { Overwrite = false });
            Assert.Equal(StorageErrors.ConflictCode, conflict.Error?.Code);

            var replaced = await storage.UploadBytesAsync($"{dir}/c.txt", Encoding.UTF8.GetBytes("v2"));
            Assert.True(replaced.IsSuccess, replaced.Error?.ToString());
            Assert.Equal("v2", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/c.txt")).Value!));
        }
        finally
        {
            var cleanup = await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            Assert.True(cleanup.IsSuccess, cleanup.Error?.ToString());
        }
        Assert.False((await storage.ExistsAsync(dir)).Value);
    }

    public static async Task MissingItemIsNotFoundAsync(IStorageBackend storage)
    {
        var info = await storage.GetInfoAsync($"missing-{Guid.NewGuid():N}.txt");
        Assert.Equal(StorageErrors.NotFoundCode, info.Error?.Code);
        var download = await storage.DownloadBytesAsync($"missing-{Guid.NewGuid():N}.txt");
        Assert.Equal(StorageErrors.NotFoundCode, download.Error?.Code);
    }

    public static async Task WrongCredentialsAreAuthenticationFailuresAsync(IStorageBackend storage)
    {
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.AuthenticationFailedCode, health.Error?.Code);
        Assert.False(StorageErrorInfo.IsTransient(health.Error));
    }

    public static async Task ClosedPortIsConnectionFailureAsync(IStorageBackend storage)
    {
        var health = await storage.CheckHealthAsync();
        Assert.NotNull(health.Error);
        Assert.Contains(health.Error!.Code, new[] { StorageErrors.ConnectionFailedCode, StorageErrors.TimeoutCode });
        Assert.True(StorageErrorInfo.IsTransient(health.Error));
    }
}
