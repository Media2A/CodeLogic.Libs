using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Provides root-scoped, provider-neutral storage operations.</summary>
public interface IStorageService
{
    /// <summary>Gets the case-insensitive connection identifier registered with the storage library.</summary>
    string ConnectionId { get; }

    /// <summary>Gets the built-in provider kind used by this mounted connection.</summary>
    StorageProvider Provider { get; }

    /// <summary>Gets the provider root, bucket prefix, container prefix, or remote directory mounted by the connection.</summary>
    string Root { get; }

    /// <summary>Gets the operations and provider limits implemented by this connection.</summary>
    StorageCapabilities Capabilities { get; }

    /// <summary>Gets metadata describing one file, directory, or virtual directory.</summary>
    /// <param name="path">Provider-neutral path relative to <see cref="Root"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The item description, or a failed result when the path is missing or inaccessible.</returns>
    Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Determines whether a file or directory exists without converting provider failures into a missing result.</summary>
    /// <param name="path">Provider-neutral path relative to <see cref="Root"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns><see langword="true"/> when the item exists; otherwise <see langword="false"/> or a provider failure.</returns>
    Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Lists one page of children below a physical or virtual directory.</summary>
    /// <param name="path">Directory path relative to <see cref="Root"/>; an empty path selects the mounted root.</param>
    /// <param name="options">Paging and recursion settings, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>One page of items and an opaque continuation token when another page is available.</returns>
    Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a physical directory or an idempotent provider marker when supported.</summary>
    /// <param name="path">Non-root directory path relative to <see cref="Root"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>A result describing whether the directory was created or already usable.</returns>
    Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Uploads content from a caller-owned stream.</summary>
    /// <param name="path">Destination file path relative to <see cref="Root"/>.</param>
    /// <param name="source">Readable stream whose ownership remains with the caller.</param>
    /// <param name="options">Upload, metadata, and mutation settings, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The committed destination item.</returns>
    Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Uploads an in-memory byte array.</summary>
    /// <param name="path">Destination file path relative to <see cref="Root"/>.</param>
    /// <param name="content">Bytes to upload.</param>
    /// <param name="options">Upload, metadata, and mutation settings, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The committed destination item.</returns>
    Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Opens an owned readable stream for an object or byte range.</summary>
    /// <param name="path">Source file path relative to <see cref="Root"/>.</param>
    /// <param name="options">Range, version, and buffering settings, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Token used to cancel opening the download.</param>
    /// <returns>A stream that must be disposed to release its provider response, session, and registry lease.</returns>
    Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Downloads content into a bounded byte array.</summary>
    /// <param name="path">Source file path relative to <see cref="Root"/>.</param>
    /// <param name="options">Range, version, and maximum-buffer settings, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The downloaded bytes, or <c>storage.too_large</c> when the configured bound is exceeded.</returns>
    Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a file or directory according to the requested recursion and mutation conditions.</summary>
    /// <param name="path">Non-root path relative to <see cref="Root"/>.</param>
    /// <param name="options">Delete and identity-condition settings, or <see langword="null"/> for defaults.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>A provider-neutral mutation result.</returns>
    Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Copies a file or directory within this mounted connection.</summary>
    /// <param name="sourcePath">Existing source path relative to <see cref="Root"/>.</param>
    /// <param name="destinationPath">Distinct destination path relative to <see cref="Root"/>.</param>
    /// <param name="options">Overwrite, parent, and metadata-preservation settings.</param>
    /// <param name="cancellationToken">Token used to cancel the transfer.</param>
    /// <returns>A result that succeeds only after the destination has committed.</returns>
    Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Moves a file or directory within this mounted connection.</summary>
    /// <param name="sourcePath">Existing source path relative to <see cref="Root"/>.</param>
    /// <param name="destinationPath">Distinct destination path relative to <see cref="Root"/>.</param>
    /// <param name="options">Overwrite, parent, and metadata-preservation settings.</param>
    /// <param name="cancellationToken">Token used to cancel the transfer.</param>
    /// <returns>A result that succeeds only after destination commit and source deletion.</returns>
    Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default);
}
