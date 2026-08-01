using CL.Storage.Abstractions;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal sealed class StorageServiceProxy : IStorageService
{
    private readonly StorageLibrary _library;
    private readonly string _connectionId;

    public StorageServiceProxy(StorageLibrary library, string connectionId)
    {
        _library = library;
        _connectionId = connectionId;
    }

    public string ConnectionId => Read(backend => backend.ConnectionId);
    public StorageProvider Provider => Read(backend => backend.Provider);
    public string Root => Read(backend => backend.Root);
    public StorageCapabilities Capabilities => Read(backend => backend.Capabilities);

    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.GetInfoAsync(path, cancellationToken));

    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.ExistsAsync(path, cancellationToken));

    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.ListAsync(path, options, cancellationToken));

    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.CreateDirectoryAsync(path, cancellationToken));

    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadWithEventAsync(path, backend => backend.UploadAsync(path, source, options, cancellationToken));

    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadWithEventAsync(path, backend => backend.UploadBytesAsync(path, content, options, cancellationToken));

    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.DownloadAsync(path, options, cancellationToken));

    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.DownloadBytesAsync(path, options, cancellationToken));

    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        MutateWithEventAsync(
            backend => backend.DeleteAsync(path, options, cancellationToken),
            backend => _library.PublishDeletedAsync(backend, path));

    public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        MutateWithEventAsync(
            backend => backend.CopyAsync(sourcePath, destinationPath, options, cancellationToken),
            backend => _library.PublishCopiedAsync(backend, sourcePath, destinationPath));

    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        MutateWithEventAsync(
            backend => backend.MoveAsync(sourcePath, destinationPath, options, cancellationToken),
            backend => _library.PublishMovedAsync(backend, sourcePath, destinationPath));

    private T Read<T>(Func<IStorageBackend, T> read)
    {
        using var lease = _library.AcquireOperation(_connectionId);
        return read(lease.Backend);
    }

    private async Task<T> InvokeAsync<T>(Func<IStorageBackend, Task<T>> operation)
    {
        using var lease = _library.AcquireOperation(_connectionId);
        return await operation(lease.Backend).ConfigureAwait(false);
    }

    private async Task<Result<StorageItem>> UploadWithEventAsync(
        string requestedPath,
        Func<IStorageBackend, Task<Result<StorageItem>>> operation)
    {
        using var lease = _library.AcquireOperation(_connectionId);
        var result = await operation(lease.Backend).ConfigureAwait(false);
        if (result.IsSuccess)
            await _library.PublishWrittenAsync(lease.Backend, result.Value?.Path ?? requestedPath).ConfigureAwait(false);
        return result;
    }

    private async Task<Result> MutateWithEventAsync(
        Func<IStorageBackend, Task<Result>> operation,
        Func<IStorageBackend, Task> publish)
    {
        using var lease = _library.AcquireOperation(_connectionId);
        var result = await operation(lease.Backend).ConfigureAwait(false);
        if (result.IsSuccess)
            await publish(lease.Backend).ConfigureAwait(false);
        return result;
    }
}
