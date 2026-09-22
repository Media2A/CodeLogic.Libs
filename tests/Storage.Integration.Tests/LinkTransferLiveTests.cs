using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Relayed transfers of trees that contain symbolic links, under each link-handling mode.</summary>
public sealed class LinkTransferLiveTests
{
    [SftpFact]
    public async Task Sftp_tree_with_a_link_transfers_under_each_mode()
    {
        await using var sftp = LiveServers.Create(LiveServers.Sftp());
        var tree = $"linktree-{Guid.NewGuid():N}";
        try
        {
            await sftp.UploadBytesAsync($"{tree}/data/target.txt", Encoding.UTF8.GetBytes("target content"));
            Assert.True((await sftp.CreateLinkAsync($"{tree}/data/link.txt", $"{tree}/data/target.txt")).IsSuccess);

            var rejected = await CopyToLocalAsync(sftp, tree, StorageLinkHandling.Reject);
            Assert.Equal(StorageErrors.UnsupportedCode, rejected.Result.Error?.Code);
            Assert.False(File.Exists(Path.Combine(rejected.Root, "copy", "data", "target.txt")), "a rejected transfer must roll back");

            var skipped = await CopyToLocalAsync(sftp, tree, StorageLinkHandling.Skip);
            Assert.True(skipped.Result.IsSuccess, skipped.Result.Error?.ToString());
            Assert.True(File.Exists(Path.Combine(skipped.Root, "copy", "data", "target.txt")));
            Assert.False(File.Exists(Path.Combine(skipped.Root, "copy", "data", "link.txt")));

            var followed = await CopyToLocalAsync(sftp, tree, StorageLinkHandling.Follow);
            Assert.True(followed.Result.IsSuccess, followed.Result.Error?.ToString());
            Assert.Equal("target content", File.ReadAllText(Path.Combine(followed.Root, "copy", "data", "link.txt")));

            // SSH.NET cannot read link targets, so SFTP links cannot be recreated elsewhere.
            var recreated = await CopyToLocalAsync(sftp, tree, StorageLinkHandling.Recreate);
            Assert.Equal(StorageErrors.UnsupportedCode, recreated.Result.Error?.Code);
        }
        finally { await sftp.DeleteAsync(tree, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }); }
    }

    [SftpFact]
    public async Task Local_links_are_recreated_on_sftp_and_remapped_into_the_copy()
    {
        var root = Directory.CreateTempSubdirectory("cl-links-").FullName;
        await using var sftp = LiveServers.Create(LiveServers.Sftp());
        var remote = $"recreated-{Guid.NewGuid():N}";
        try
        {
            var local = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = root, FollowLinks = true });
            await local.UploadBytesAsync("tree/data/target.txt", Encoding.UTF8.GetBytes("via link"));
            var link = await local.CreateLinkAsync("tree/links/link.txt", "tree/data/target.txt");
            if (link.Error?.Code == StorageErrors.PermissionDeniedCode)
                return; // Windows without Developer Mode cannot create the source link; CI runs on Linux.
            Assert.True(link.IsSuccess, link.Error?.ToString());

            var copied = await StorageTransferCoordinator.CopyAsync(local, "tree", sftp, remote,
                new StorageTransferOptions { LinkHandling = StorageLinkHandling.Recreate }, CancellationToken.None);

            Assert.True(copied.IsSuccess, copied.Error?.ToString());
            var listed = (await sftp.ListAsync($"{remote}/links")).Value!.Items;
            Assert.Equal(StorageItemType.Link, Assert.Single(listed).ItemType);
            // The link points at the copy, not back at the source tree.
            Assert.Equal("via link", Encoding.UTF8.GetString((await sftp.DownloadBytesAsync($"{remote}/links/link.txt")).Value!));
        }
        finally
        {
            await sftp.DeleteAsync(remote, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(Result<StorageTransferSummary> Result, string Root)> CopyToLocalAsync(
        IStorageBackend source,
        string tree,
        StorageLinkHandling handling)
    {
        var root = Directory.CreateTempSubdirectory("cl-linkcopy-").FullName;
        var local = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = root });
        var result = await StorageTransferCoordinator.CopyAsync(source, tree, local, "copy",
            new StorageTransferOptions { LinkHandling = handling }, CancellationToken.None);
        return (result, root);
    }
}
