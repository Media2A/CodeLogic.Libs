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
        DownloadWithLeaseAsync(path, options, cancellationToken);

    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend.DownloadBytesAsync(path, options, cancellationToken));

    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        MutateWithEventAsync(
            backend => backend.DeleteAsync(path, options, cancellationToken),
            (connectionId, provider) => _library.PublishDeletedAsync(connectionId, provider, path));

    public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        MutateWithEventAsync(
            backend => backend.CopyAsync(sourcePath, destinationPath, options, cancellationToken),
            (connectionId, provider) => _library.PublishCopiedAsync(connectionId, provider, sourcePath, destinationPath));

    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        MutateWithEventAsync(
            backend => backend.MoveAsync(sourcePath, destinationPath, options, cancellationToken),
            (connectionId, provider) => _library.PublishMovedAsync(connectionId, provider, sourcePath, destinationPath));

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

    private async Task<Result<Stream>> DownloadWithLeaseAsync(
        string path,
        StorageDownloadOptions? options,
        CancellationToken cancellationToken)
    {
        var lease = _library.AcquireOperation(_connectionId);
        try
        {
            var result = await lease.Backend.DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                lease.Dispose();
                return Result<Stream>.Failure(result.Error!);
            }
            return Result<Stream>.Success(new LeaseOwnedStream(result.Value!, lease));
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private async Task<Result<StorageItem>> UploadWithEventAsync(
        string requestedPath,
        Func<IStorageBackend, Task<Result<StorageItem>>> operation)
    {
        var lease = _library.AcquireOperation(_connectionId);
        Result<StorageItem> result;
        string? connectionId = null;
        StorageProvider provider = default;
        string? eventPath = null;
        try
        {
            result = await operation(lease.Backend).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                connectionId = lease.Backend.ConnectionId;
                provider = lease.Backend.Provider;
                eventPath = result.Value?.Path ?? requestedPath;
            }
        }
        finally
        {
            lease.Dispose();
        }
        if (result.IsSuccess)
            await _library.PublishWrittenAsync(connectionId!, provider, eventPath!).ConfigureAwait(false);
        return result;
    }

    private async Task<Result> MutateWithEventAsync(
        Func<IStorageBackend, Task<Result>> operation,
        Func<string, StorageProvider, Task> publish)
    {
        var lease = _library.AcquireOperation(_connectionId);
        Result result;
        string? connectionId = null;
        StorageProvider provider = default;
        try
        {
            result = await operation(lease.Backend).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                connectionId = lease.Backend.ConnectionId;
                provider = lease.Backend.Provider;
            }
        }
        finally
        {
            lease.Dispose();
        }
        if (result.IsSuccess)
            await publish(connectionId!, provider).ConfigureAwait(false);
        return result;
    }
}
