using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>
/// Capability-gated file attributes: Unix permissions, ownership, timestamps, and symbolic links.
/// Check <see cref="StorageCapabilities"/> for <see cref="StorageFeature.Permissions"/>,
/// <see cref="StorageFeature.Ownership"/>, <see cref="StorageFeature.SetTimestamps"/>,
/// <see cref="StorageFeature.CreateLinks"/>, and <see cref="StorageFeature.ReadLinks"/>.
/// </summary>
public interface IStorageAttributeService
{
    /// <summary>Sets Unix permission bits on a file or directory.</summary>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="unixMode">Permission bits, for example octal 0755; see <see cref="UnixPermissions.TryParseOctal"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or a failure such as <c>storage.permission_denied</c> or <c>storage.unsupported</c>.</returns>
    Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default);

    /// <summary>Changes the numeric owner and/or group of a file or directory.</summary>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="ownerId">New owner ID, or <see langword="null"/> to keep the current owner.</param>
    /// <param name="groupId">New group ID, or <see langword="null"/> to keep the current group.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or a failure such as <c>storage.permission_denied</c>.</returns>
    Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default);

    /// <summary>Sets the modification time and, where supported, the access time.</summary>
    /// <param name="path">Item path relative to the mounted root.</param>
    /// <param name="lastModified">New modification time, or <see langword="null"/> to keep it.</param>
    /// <param name="lastAccessed">New access time, or <see langword="null"/> to keep it; ignored where unsupported.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or a failure such as <c>storage.unsupported</c> when the server lacks the command.</returns>
    Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a symbolic link pointing at another item inside the same mounted root.</summary>
    /// <param name="linkPath">Path of the link to create.</param>
    /// <param name="targetPath">Path of the item the link points to, relative to the mounted root.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>Success, or a failure such as <c>storage.conflict</c> when <paramref name="linkPath"/> exists.</returns>
    Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>Reads where a symbolic link points.</summary>
    /// <param name="path">Link path relative to the mounted root.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The raw target and, when it resolves inside the mounted root, its storage path.</returns>
    Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>Where a symbolic link points.</summary>
/// <param name="RawTarget">Target exactly as the server or filesystem reports it.</param>
/// <param name="StoragePath">The target as a storage path when it lies inside the mounted root; otherwise <see langword="null"/>.</param>
public sealed record StorageLinkInfo(string RawTarget, string? StoragePath);
