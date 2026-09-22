using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

internal sealed class StorageServiceProxy :
    IStorageService,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService,
    IStorageAttributeService,
    IStorageChecksumService,
    IStorageAppendService,
    IStorageCommandService,
    IStorageSpaceService
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
        InvokePathAsync(path, cancellationToken,
            (backend, normalized) => backend.GetInfoAsync(normalized, cancellationToken));

    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken,
            (backend, normalized) => backend.ExistsAsync(normalized, cancellationToken));

    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, options?.Validate() ?? Result.Success(),
            (backend, normalized) => backend.ListAsync(normalized, options, cancellationToken));

    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken,
            (backend, normalized) => backend.CreateDirectoryAsync(normalized, cancellationToken));

    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadWithConflictPolicyAsync(path, options, source.CanSeek ? source.Length - source.Position : null, cancellationToken,
            (backend, normalized, resolved) => backend.UploadAsync(normalized, source, resolved, cancellationToken));

    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        UploadWithConflictPolicyAsync(path, options, content.LongLength, cancellationToken,
            (backend, normalized, resolved) => backend.UploadBytesAsync(normalized, content, resolved, cancellationToken));

    /// <summary>
    /// Resolves a conditional <see cref="StorageUploadOptions.ConflictPolicy"/> before uploading. A skipped
    /// upload succeeds with the existing destination item and publishes no write event.
    /// </summary>
    private async Task<Result<StorageItem>> UploadWithConflictPolicyAsync(
        string path,
        StorageUploadOptions? options,
        long? sourceLength,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, StorageUploadOptions?, Task<Result<StorageItem>>> upload)
    {
        var validation = options?.Validate() ?? Result.Success();
        if (options?.ConflictPolicy == StorageConflictPolicy.Resume && validation.IsSuccess)
            return await UploadPathWithEventAsync(path, validation, cancellationToken,
                (backend, normalized) => upload(backend, normalized, options)).ConfigureAwait(false);
        if (validation.IsFailure || options?.ConflictPolicy is null)
            return await UploadPathWithEventAsync(path, validation, cancellationToken,
                (backend, normalized) => upload(backend, normalized, options)).ConfigureAwait(false);

        var normalizedPath = StoragePath.Normalize(path);
        if (normalizedPath.IsFailure)
            return Result<StorageItem>.Failure(normalizedPath.Error!);
        var decision = await StorageConflictResolver.ResolveAsync(
            this,
            normalizedPath.Value!,
            options.ConflictPolicy,
            options.Overwrite,
            sourceLength,
            options.SourceLastModified,
            cancellationToken).ConfigureAwait(false);
        if (decision.IsFailure)
            return Result<StorageItem>.Failure(decision.Error!);
        if (decision.Value.Skip)
            return Result<StorageItem>.Success(decision.Value.Existing!);
        var resolved = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
        return await UploadPathWithEventAsync(decision.Value.Path, Result.Success(), cancellationToken,
            (backend, normalized) => upload(backend, normalized, resolved)).ConfigureAwait(false);
    }

    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        DownloadWithLeaseAsync(path, options, cancellationToken);

    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, options?.Validate() ?? Result.Success(),
            (backend, normalized) => backend.DownloadBytesAsync(normalized, options, cancellationToken));

    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        MutatePathWithEventAsync(
            path,
            options?.Validate() ?? Result.Success(),
            cancellationToken,
            (backend, normalized) => backend.DeleteAsync(normalized, options, cancellationToken),
            (connectionId, provider, normalized) => new StorageItemDeletedEvent(
                connectionId, provider, normalized, DateTimeOffset.UtcNow));

    public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _library.CopyAsync(
            _connectionId,
            sourcePath,
            _connectionId,
            destinationPath,
            options,
            cancellationToken);

    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _library.MoveAsync(
            _connectionId,
            sourcePath,
            _connectionId,
            destinationPath,
            options,
            cancellationToken);

    public Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        InvokePathAsync(
            path,
            cancellationToken,
            (backend, normalized) => backend is IStorageMetadataService metadata
                ? metadata.GetMetadataAsync(normalized, cancellationToken)
                : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support metadata operations."))));

    public Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageMetadataService metadataService
                ? metadataService.SetMetadataAsync(normalized, snapshot, options, cancellationToken)
                : Task.FromResult(Result<StorageItem>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support metadata updates."))));
    }

    public Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        InvokePathAsync(
            path,
            cancellationToken,
            (backend, normalized) => backend is IStorageTagService tags
                ? tags.GetTagsAsync(normalized, cancellationToken)
                : Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support object tags."))));

    public Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= new StorageTagUpdateOptions();
        var validation = options.Validate(tags);
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        var snapshot = StorageMetadataSnapshot.Create(tags);
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageTagService tagService
                ? tagService.SetTagsAsync(normalized, snapshot, options, cancellationToken)
                : Task.FromResult(Result<StorageItem>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support object tag updates."))));
    }

    public Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageSignedUrlService signedUrls
                ? signedUrls.CreateSignedUrlAsync(normalized, options, cancellationToken)
                : Task.FromResult(Result<StorageSignedUrl>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support signed URLs."))));
    }

    public Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        return InvokePathAsync(
            path,
            cancellationToken,
            validation,
            (backend, normalized) => backend is IStorageVersionService versions
                ? versions.ListVersionsAsync(normalized, options, cancellationToken)
                : Task.FromResult(Result<StorageVersionPage>.Failure(
                    StorageErrors.Unsupported("This storage connection does not support object versions."))));
    }

    public Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versionValidation = StorageOptionValidation.OptionalToken(versionId, nameof(versionId));
        if (versionValidation.IsFailure)
            return Task.FromResult(versionValidation);
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Task.FromResult(Result.Failure(normalized.Error!));
        if (normalized.Value!.Length == 0)
            return Task.FromResult(Result.Failure(StorageErrors.InvalidPath(
                "A non-root version path is required.")));
        return InvokeAsync(backend => backend is IStorageVersionService versions
            ? versions.DeleteVersionAsync(normalized.Value!, versionId, cancellationToken)
            : Task.FromResult(Result.Failure(
                StorageErrors.Unsupported("This storage connection does not support object versions."))));
    }

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

    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        unixMode is < 0 or > UnixPermissions.MaxMode
            ? Task.FromResult(Result.Failure(StorageErrors.InvalidContent("The permission mode must be between 0 and octal 7777.")))
            : InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
                ? attributes.SetPermissionsAsync(normalized, unixMode, cancellationToken)
                : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support permissions."))));

    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        ownerId is null && groupId is null
            ? Task.FromResult(Result.Failure(StorageErrors.InvalidContent("An owner or group ID is required.")))
            : InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
                ? attributes.SetOwnerAsync(normalized, ownerId, groupId, cancellationToken)
                : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support ownership changes."))));

    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        lastModified is null && lastAccessed is null
            ? Task.FromResult(Result.Failure(StorageErrors.InvalidContent("A modification or access time is required.")))
            : InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
                ? attributes.SetTimestampsAsync(normalized, lastModified, lastAccessed, cancellationToken)
                : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support setting timestamps."))));

    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default)
    {
        var target = StoragePath.Normalize(targetPath);
        if (target.IsFailure)
            return Task.FromResult(Result.Failure(target.Error!));
        return InvokePathAsync(linkPath, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
            ? attributes.CreateLinkAsync(normalized, target.Value!, cancellationToken)
            : Task.FromResult(Result.Failure(StorageErrors.Unsupported("This storage connection does not support creating links."))));
    }

    public Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageChecksumService checksums
            ? checksums.GetServerChecksumAsync(normalized, algorithm, cancellationToken)
            : Task.FromResult(Result<StorageChecksum>.Failure(StorageErrors.Unsupported("This storage connection does not report server checksums."))));

    public Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default) =>
        UploadPathWithEventAsync(path, Result.Success(), cancellationToken, (backend, normalized) => backend is IStorageAppendService append
            ? append.AppendAsync(normalized, source, cancellationToken)
            : Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unsupported("This storage connection cannot append."))));

    public Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default) =>
        InvokeAsync(backend => backend is IStorageCommandService commands
            ? commands.ExecuteCommandAsync(command, cancellationToken)
            : Task.FromResult(Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("This storage connection does not support raw commands."))));

    public Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageSpaceService space
            ? space.GetSpaceAsync(normalized, cancellationToken)
            : Task.FromResult(Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("This storage connection does not report free space."))));

    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        InvokePathAsync(path, cancellationToken, (backend, normalized) => backend is IStorageAttributeService attributes
            ? attributes.ReadLinkAsync(normalized, cancellationToken)
            : Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Unsupported("This storage connection does not support reading links."))));

    private Task<Result<T>> InvokePathAsync<T>(
        string path,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result<T>>> operation) =>
        InvokePathAsync(path, cancellationToken, Result.Success(), operation);

    private Task<Result<T>> InvokePathAsync<T>(
        string path,
        CancellationToken cancellationToken,
        Result validation,
        Func<IStorageBackend, string, Task<Result<T>>> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation.IsFailure)
            return Task.FromResult(Result<T>.Failure(validation.Error!));
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result<T>.Failure(normalized.Error!))
            : InvokeAsync(backend => operation(backend, normalized.Value!));
    }

    private Task<Result> InvokePathAsync(
        string path,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result>> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result.Failure(normalized.Error!))
            : InvokeAsync(backend => operation(backend, normalized.Value!));
    }

    private async Task<Result<Stream>> DownloadWithLeaseAsync(
        string path,
        StorageDownloadOptions? options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = options?.Validate() ?? Result.Success();
        if (validation.IsFailure)
            return Result<Stream>.Failure(validation.Error!);
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Result<Stream>.Failure(normalized.Error!);

        var lease = _library.AcquireOperation(_connectionId);
        try
        {
            var result = await lease.Backend.DownloadAsync(normalized.Value!, options, cancellationToken).ConfigureAwait(false);
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

    private Task<Result<StorageItem>> UploadPathWithEventAsync(
        string path,
        Result validation,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result<StorageItem>>> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(validation.Error!));
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result<StorageItem>.Failure(normalized.Error!))
            : UploadWithEventAsync(normalized.Value!, backend => operation(backend, normalized.Value!));
    }

    private async Task<Result<StorageItem>> UploadWithEventAsync(
        string requestedPath,
        Func<IStorageBackend, Task<Result<StorageItem>>> operation)
    {
        var lease = _library.AcquireOperation(_connectionId);
        Result<StorageItem> result;
        StorageEventPublisher? publisher = null;
        StorageItemWrittenEvent? @event = null;
        try
        {
            result = await operation(lease.Backend).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                var eventPath = requestedPath;
                if (result.Value?.Path is { } returnedPath)
                {
                    var normalized = StoragePath.Normalize(returnedPath);
                    if (normalized.IsSuccess)
                        eventPath = normalized.Value!;
                }
                @event = new StorageItemWrittenEvent(
                    lease.Backend.ConnectionId,
                    lease.Backend.Provider,
                    eventPath,
                    DateTimeOffset.UtcNow);
                publisher = _library.CaptureEventPublisher();
            }
        }
        finally
        {
            lease.Dispose();
        }
        if (result.IsSuccess)
            await publisher!.PublishAsync(@event!).ConfigureAwait(false);
        return result;
    }

    private Task<Result> MutatePathWithEventAsync<TEvent>(
        string path,
        Result validation,
        CancellationToken cancellationToken,
        Func<IStorageBackend, string, Task<Result>> operation,
        Func<string, StorageProvider, string, TEvent> createEvent)
        where TEvent : IEvent
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation.IsFailure)
            return Task.FromResult(Result.Failure(validation.Error!));
        var normalized = StoragePath.Normalize(path);
        return normalized.IsFailure
            ? Task.FromResult(Result.Failure(normalized.Error!))
            : MutateWithEventAsync(
                backend => operation(backend, normalized.Value!),
                (connectionId, provider) => createEvent(connectionId, provider, normalized.Value!));
    }

    private async Task<Result> MutateWithEventAsync<TEvent>(
        Func<IStorageBackend, Task<Result>> operation,
        Func<string, StorageProvider, TEvent> createEvent)
        where TEvent : IEvent
    {
        var lease = _library.AcquireOperation(_connectionId);
        Result result;
        StorageEventPublisher? publisher = null;
        TEvent? @event = default;
        try
        {
            result = await operation(lease.Backend).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                @event = createEvent(lease.Backend.ConnectionId, lease.Backend.Provider);
                publisher = _library.CaptureEventPublisher();
            }
        }
        finally
        {
            lease.Dispose();
        }
        if (result.IsSuccess)
            await publisher!.PublishAsync(@event!).ConfigureAwait(false);
        return result;
    }
}
