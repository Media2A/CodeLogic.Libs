using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Live coverage of permissions, ownership, timestamps, and links over SFTP and FTP.</summary>
public sealed class AttributeLiveTests
{
    private static readonly DateTimeOffset Stamp = new(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [SftpFact]
    public async Task Sftp_permissions_round_trip_including_special_bits()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        await WithFileAsync(storage, async path =>
        {
            Assert.True((await storage.SetPermissionsAsync(path, "640")).IsSuccess);
            Assert.Equal(0x1A0, (await storage.GetInfoAsync(path)).Value!.UnixMode);
            Assert.Equal("rw-r-----", (await storage.GetInfoAsync(path)).Value!.Permissions);

            Assert.True((await storage.SetPermissionsAsync(path, 0x9ED)).IsSuccess); // octal 4755
            var info = (await storage.GetInfoAsync(path)).Value!;
            Assert.Equal(0x9ED, info.UnixMode);
            Assert.Equal("rwsr-xr-x", info.Permissions);
        });
    }

    [SftpFact]
    public async Task Sftp_listing_reports_mode_owner_and_hidden_files()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        var dir = $"attrs-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/.hidden", [1]);
            await storage.UploadBytesAsync($"{dir}/visible.txt", [1]);
            await storage.SetPermissionsAsync($"{dir}/visible.txt", 0x1B4); // 0664

            var items = (await storage.ListAsync(dir)).Value!.Items;

            var visible = Assert.Single(items, item => item.Name == "visible.txt");
            Assert.Equal(0x1B4, visible.UnixMode);
            Assert.Equal(1001, visible.OwnerId);
            Assert.False(visible.IsHidden);
            Assert.True(Assert.Single(items, item => item.Name == ".hidden").IsHidden);
        }
        finally { await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }

    [SftpFact]
    public async Task Sftp_timestamps_can_be_set()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        await WithFileAsync(storage, async path =>
        {
            Assert.True((await storage.SetTimestampsAsync(path, Stamp, Stamp.AddDays(1))).IsSuccess);
            var info = (await storage.GetInfoAsync(path)).Value!;
            Assert.Equal(Stamp, info.LastModified);
            Assert.Equal(Stamp.AddDays(1), info.LastAccessed);
        });
    }

    [SftpFact]
    public async Task Sftp_owner_change_to_self_succeeds_and_to_root_is_denied()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c => c.Retry.RetryCount = 0));
        await WithFileAsync(storage, async path =>
        {
            Assert.True((await storage.SetOwnerAsync(path, 1001, 1001)).IsSuccess);
            var denied = await storage.SetOwnerAsync(path, 0, null);
            Assert.Equal(StorageErrors.PermissionDeniedCode, denied.Error?.Code);
        });
    }

    [SftpFact]
    public async Task Sftp_symbolic_link_is_created_and_listed_as_a_link()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        var dir = $"links-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/target.txt", Encoding.UTF8.GetBytes("linked"));

            var created = await storage.CreateLinkAsync($"{dir}/link.txt", $"{dir}/target.txt");
            Assert.True(created.IsSuccess, created.Error?.ToString());

            var items = (await storage.ListAsync(dir)).Value!.Items;
            Assert.Equal(StorageItemType.Link, Assert.Single(items, item => item.Name == "link.txt").ItemType);
            Assert.Equal("linked", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/link.txt")).Value!));

            var duplicate = await storage.CreateLinkAsync($"{dir}/link.txt", $"{dir}/target.txt");
            Assert.Equal(StorageErrors.ConflictCode, duplicate.Error?.Code);
        }
        finally { await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }

    [SftpFact]
    public async Task Sftp_recursive_permissions_apply_file_and_directory_modes()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        var dir = $"chmod-r-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/a.txt", [1]);
            await storage.UploadBytesAsync($"{dir}/sub/b.txt", [1]);

            var changed = await storage.SetPermissionsRecursiveAsync(dir, fileMode: 0x180, directoryMode: 0x1C0); // 0600 / 0700

            Assert.True(changed.IsSuccess, changed.Error?.ToString());
            Assert.Equal(4, changed.Value);
            Assert.Equal(0x180, (await storage.GetInfoAsync($"{dir}/sub/b.txt")).Value!.UnixMode);
            Assert.Equal(0x1C0, (await storage.GetInfoAsync($"{dir}/sub")).Value!.UnixMode);
            Assert.Equal(0x1C0, (await storage.GetInfoAsync(dir)).Value!.UnixMode);
        }
        finally { await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }

    [FtpFact]
    public async Task Ftp_permissions_are_set_with_site_chmod_and_read_from_listings()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        await WithFileAsync(storage, async path =>
        {
            var set = await storage.SetPermissionsAsync(path, "604");
            Assert.True(set.IsSuccess, set.Error?.ToString());
            var parent = path[..path.LastIndexOf('/')];
            var item = Assert.Single((await storage.ListAsync(parent)).Value!.Items, i => i.Path == path);
            Assert.Equal(0x184, item.UnixMode);
            Assert.NotNull(item.Owner);
        });
    }

    [FtpFact]
    public async Task Ftp_modification_time_can_be_set()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        await WithFileAsync(storage, async path =>
        {
            var set = await storage.SetTimestampsAsync(path, Stamp);
            Assert.True(set.IsSuccess, set.Error?.ToString());
            Assert.Equal(Stamp, (await storage.GetInfoAsync(path)).Value!.LastModified);
        });
    }

    [FtpFact]
    public async Task Ftp_reports_ownership_and_link_creation_as_unsupported()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        Assert.False(storage.Capabilities.Supports(StorageFeature.Ownership));
        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.SetOwnerAsync("x", 1, 1)).Error?.Code);
        Assert.Equal(StorageErrors.UnsupportedCode, (await storage.CreateLinkAsync("a", "b")).Error?.Code);
    }

    private static async Task WithFileAsync(IStorageBackend storage, Func<string, Task> test)
    {
        var dir = $"attr-{Guid.NewGuid():N}";
        var path = $"{dir}/file.txt";
        var upload = await storage.UploadBytesAsync(path, Encoding.UTF8.GetBytes("attributes"));
        Assert.True(upload.IsSuccess, upload.Error?.ToString());
        try { await test(path); }
        finally { await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }
}
