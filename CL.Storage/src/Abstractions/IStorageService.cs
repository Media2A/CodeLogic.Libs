using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Provides root-scoped, provider-neutral storage operations.</summary>
public interface IStorageService
{
    string ConnectionId { get; }
    StorageProvider Provider { get; }
    string Root { get; }
    StorageCapabilities Capabilities { get; }

    Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default);
    Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default);
    Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default);
    Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default);
}
