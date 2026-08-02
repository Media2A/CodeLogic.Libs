using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Azure;

/// <summary>Root-scoped storage over an Azure Blob container.</summary>
public sealed class AzureBlobStorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService
{
    private static readonly StorageCapabilities AzureCapabilities = new(
        StorageFeature.VirtualDirectories |
        StorageFeature.FileCopy |
        StorageFeature.DirectoryCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.ServerSideCopy |
        StorageFeature.ServerSideMove |
        StorageFeature.RangeReads |
        StorageFeature.MetadataRead |
        StorageFeature.MetadataWrite |
        StorageFeature.Tags |
        StorageFeature.ConditionalCreate |
        StorageFeature.ConditionalUpdate |
        StorageFeature.ConditionalDelete |
        StorageFeature.AtomicReplace |
        StorageFeature.ServerPagination |
        StorageFeature.ResumableUpload |
        StorageFeature.Versioning,
        new StorageLimits
        {
            MaxPageSize = 5_000,
            MaxObjectBytes = 209_715_200_000_000,
            MaxMetadataBytes = 8 * 1024,
            MaxTags = 10
        });
    private readonly BlobContainerClient _container;
    private readonly StorageCapabilities _capabilities;
    private readonly string _prefix;
    private readonly long _maxBufferedDownloadBytes;

    public AzureBlobStorageBackend(
        string connectionId,
        BlobContainerClient container,
        string? prefix = null,
        long maxBufferedDownloadBytes = 67_108_864)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(container);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        var normalized = StoragePath.Normalize(prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(prefix));
        ConnectionId = connectionId;
        _container = container;
        var features = AzureCapabilities.Features;
        if (container.GetBlobClient(".clstorage-capability-probe").CanGenerateSasUri)
            features |= StorageFeature.SignedReadUrls | StorageFeature.SignedWriteUrls;
        _capabilities = new StorageCapabilities(features, AzureCapabilities.Limits);
        Root = normalized.Value!;
        _prefix = Root.Length == 0 ? string.Empty : Root + "/";
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.AzureBlob;
    public string Root { get; }
    public StorageCapabilities Capabilities => _capabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        try
        {
            var properties = await Blob(normalized.Value).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalized.Value, properties.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (error.Status == 404)
        {
            return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Azure blob info")); }
    }

    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsSuccess) return Result<bool>.Success(true);
        return info.Error?.Code == StorageErrors.NotFoundCode
            ? Result<bool>.Success(false)
            : Result<bool>.Failure(info.Error!);
    }

    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);
        if (normalized.Value!.Length > 0)
        {
            var directory = await GetInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (directory.IsFailure) return Result<StoragePage>.Failure(directory.Error!);
            if (directory.Value!.ItemType != StorageItemType.Directory)
                return Result<StoragePage>.Failure(StorageErrors.Conflict("An Azure blob cannot be listed as a directory."));
        }
        var prefix = DirectoryPrefix(normalized.Value!);
        try
        {
            if (options.Recursive)
            {
                await foreach (var page in _container.GetBlobsAsync(
                    BlobTraits.Metadata,
                    BlobStates.None,
                    prefix,
                    cancellationToken).AsPages(options.ContinuationToken, options.PageSize).ConfigureAwait(false))
                {
                    var items = page.Values.Select(ToItem).Where(item => item is not null).Cast<StorageItem>().ToArray();
                    return Result<StoragePage>.Success(new StoragePage(items, page.ContinuationToken));
                }
            }
            else
            {
                await foreach (var page in _container.GetBlobsByHierarchyAsync(
                    BlobTraits.Metadata,
                    BlobStates.None,
                    delimiter: "/",
                    prefix,
                    cancellationToken).AsPages(options.ContinuationToken, options.PageSize).ConfigureAwait(false))
                {
                    var items = page.Values.Select(ToItem).Where(item => item is not null).Cast<StorageItem>().ToArray();
                    return Result<StoragePage>.Success(new StoragePage(items, page.ContinuationToken));
                }
            }
            return Result<StoragePage>.Success(new StoragePage([], null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Azure blobs")); }
    }

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result.Success();
        await using var empty = new MemoryStream([]);
        try
        {
            await Blob(normalized.Value!.TrimEnd('/') + "/").UploadAsync(empty, overwrite: true, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create Azure blob directory")); }
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(options.Metadata) > 8 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Azure Blob metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
            BlobRequestConditions? conditions;
            if (!options.Overwrite)
            {
                conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }
            else if (options.Condition is { IsEmpty: false } condition)
            {
                ETag? ifMatch = condition.ExpectedETag is null
                    ? null
                    : new ETag(condition.ExpectedETag);
                if (condition.ExpectedVersionId is not null)
                {
                    var current = (await Blob(normalizedPath).GetPropertiesAsync(
                        cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
                    var conditionResult = ValidateCurrentCondition(
                        ToItem(normalizedPath, current),
                        condition,
                        "Azure blob");
                    if (conditionResult.IsFailure)
                        return Result<StorageItem>.Failure(conditionResult.Error!);
                    ifMatch ??= current.ETag;
                }
                conditions = new BlobRequestConditions { IfMatch = ifMatch };
            }
            else
            {
                conditions = null;
            }
            var uploadOptions = new BlobUploadOptions
            {
                HttpHeaders = string.IsNullOrWhiteSpace(options.ContentType) ? null : new BlobHttpHeaders { ContentType = options.ContentType },
                Metadata = options.Metadata.Count == 0 ? null : new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal),
                Conditions = conditions
            };
            await Blob(normalizedPath).UploadAsync(source, uploadOptions, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (!options.Overwrite && error.Status is 409 or 412)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The Azure blob destination already exists."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload Azure blob")); }
    }

    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = new MemoryStream(content, writable: false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        try
        {
            BlobDownloadOptions? downloadOptions = null;
            if (options.Offset > 0 || options.Length.HasValue)
                downloadOptions = new BlobDownloadOptions { Range = new HttpRange(options.Offset, options.Length) };
            var blob = Blob(normalized.Value!);
            if (options.VersionId is not null)
                blob = blob.WithVersion(options.VersionId);
            var response = await blob.DownloadStreamingAsync(downloadOptions, cancellationToken).ConfigureAwait(false);
            return Result<Stream>.Success(response.Value.Content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Azure blob")); }
    }

    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length > Math.Min(limit, int.MaxValue) - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
    }

    public async Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
            var info = await GetInfoAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "Azure virtual directories do not have one atomic identity condition."));
                var prefix = DirectoryPrefix(normalizedPath);
                var names = new List<string>();
                await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken).ConfigureAwait(false))
                    names.Add(item.Name);
                if (!options.Recursive && names.Any(name => name != prefix))
                    return Result.Failure(StorageErrors.Conflict("The Azure blob directory is not empty."));
                foreach (var name in names)
                    await _container.DeleteBlobIfExistsAsync(name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
                await _container.DeleteBlobIfExistsAsync(prefix, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "Azure blob");
                if (condition.IsFailure) return condition;
                var requestConditions = options.Condition is { IsEmpty: false }
                    ? new BlobRequestConditions
                    {
                        IfMatch = options.Condition.ExpectedETag is not null
                            ? new ETag(options.Condition.ExpectedETag)
                            : info.Value.ETag is not null ? new ETag(info.Value.ETag) : null
                    }
                    : null;
                await Blob(normalizedPath).DeleteIfExistsAsync(
                    DeleteSnapshotsOption.IncludeSnapshots,
                    requestConditions,
                    cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Azure blob")); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var sourceValue = source.Value!;
        var destinationValue = destination.Value!;
        var relationship = StorageTransferPath.ValidateDistinct(sourceValue, destinationValue);
        if (relationship.IsFailure) return relationship;
        try
        {
            var info = await GetInfoAsync(sourceValue, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(sourceValue, destinationValue);
                if (relationship.IsFailure) return relationship;
                var relayed = await StorageTransferCoordinator.CopyAsync(
                    this,
                    sourceValue,
                    this,
                    destinationValue,
                    options,
                    cancellationToken).ConfigureAwait(false);
                return relayed.IsSuccess ? Result.Success() : Result.Failure(relayed.Error!);
            }
            await CopyBlobAsync(ToKey(sourceValue), ToKey(destinationValue), options.Overwrite, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (!options.Overwrite && error.Status is 409 or 412)
        {
            return Result.Failure(StorageErrors.Conflict("The Azure blob destination already exists."));
        }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Azure blob")); }
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copied = await CopyAsync(sourcePath, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return copied;
        var deleted = await DeleteAsync(sourcePath, new StorageDeleteOptions { Recursive = true }, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess
            ? Result.Success()
            : Result.Failure(StorageErrors.PartialFailure(
                "The Azure Blob destination completed, but the source could not be deleted.",
                $"sourceDeleteError={deleted.Error!.Code};destinationState=complete"));
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var exists = await _container.ExistsAsync(cancellationToken).ConfigureAwait(false);
            return exists.Value ? Result.Success() : Result.Failure(StorageErrors.NotFound("The Azure blob container was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Azure blob health")); }
    }

    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }

    public async Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new StorageMetadataUpdateOptions();
        var validation = options.Validate(metadata);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(metadata) > 8 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Azure Blob metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var blob = Blob(normalized.Value!);
            var current = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value;
            if (options.ExpectedVersionId is not null &&
                !string.Equals(options.ExpectedVersionId, current.VersionId, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Azure blob version no longer matches the metadata update condition."));
            }
            var values = options.Mode == StorageMetadataUpdateMode.Merge
                ? new Dictionary<string, string>(current.Metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in snapshot)
                values[name] = value;
            var expected = options.ExpectedETag is not null
                ? new ETag(options.ExpectedETag)
                : options.ExpectedVersionId is not null ? current.ETag : (ETag?)null;
            await blob.SetMetadataAsync(
                values,
                expected.HasValue ? new BlobRequestConditions { IfMatch = expected.Value } : null,
                cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RequestFailedException error) when (error.Status is 409 or 412)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The Azure blob changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Azure blob metadata")); }
    }

    public async Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure)
            return Result<IReadOnlyDictionary<string, string>>.Failure(normalized.Error!);
        try
        {
            var response = await Blob(normalized.Value!).GetTagsAsync(
                conditions: null,
                cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyDictionary<string, string>>.Success(
                StorageMetadataSnapshot.Create(response.Value.Tags));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(Map(error, "Read Azure blob tags"));
        }
    }

    public async Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        options ??= new StorageTagUpdateOptions();
        var validation = options.Validate(tags);
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var blob = Blob(normalized.Value!);
            if (options.Mode == StorageTagUpdateMode.Merge)
            {
                var current = await blob.GetTagsAsync(
                    conditions: null,
                    cancellationToken).ConfigureAwait(false);
                foreach (var (name, value) in current.Value.Tags)
                    values[name] = value;
            }
            foreach (var (name, value) in tags)
                values[name] = value;
            validation = options.Validate(values);
            if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);

            await blob.SetTagsAsync(values, conditions: null, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Azure blob tags")); }
    }

    public async Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageVersionListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageVersionPage>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageVersionPage>.Failure(normalized.Error!);
        var key = ToKey(normalized.Value!);
        var states = options.IncludeDeleteMarkers
            ? BlobStates.Version | BlobStates.DeletedWithVersions
            : BlobStates.Version;
        try
        {
            await foreach (var page in _container.GetBlobsAsync(
                BlobTraits.None,
                states,
                key,
                cancellationToken).AsPages(
                    options.ContinuationToken,
                    Math.Min(options.PageSize, 5_000)).ConfigureAwait(false))
            {
                var versions = new List<StorageVersion>();
                foreach (var version in page.Values)
                {
                    if (!string.Equals(version.Name, key, StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrWhiteSpace(version.VersionId))
                        return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                            "Azure Blob returned a version without a version identifier."));
                    versions.Add(new StorageVersion
                    {
                        Path = normalized.Value!,
                        VersionId = version.VersionId,
                        ETag = version.Properties.ETag?.ToString(),
                        Size = version.Properties.ContentLength,
                        LastModified = version.Properties.LastModified,
                        IsLatest = version.IsLatestVersion == true,
                        IsDeleteMarker = version.Deleted == true
                    });
                }
                return Result<StorageVersionPage>.Success(new StorageVersionPage(
                    versions.AsReadOnly(),
                    page.ContinuationToken));
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage([], null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List Azure blob versions")); }
    }

    public async Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versionValidation = StorageOptionValidation.OptionalToken(versionId, nameof(versionId));
        if (versionValidation.IsFailure) return versionValidation;
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            var deleted = await Blob(normalized.Value!).WithVersion(versionId).DeleteIfExistsAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return deleted.Value
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The Azure blob version was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Azure blob version")); }
    }

    public Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result<StorageSignedUrl>.Failure(validation.Error!));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure)
            return Task.FromResult(Result<StorageSignedUrl>.Failure(normalized.Error!));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var blob = Blob(normalized.Value!);
            if (options.VersionId is not null)
                blob = blob.WithVersion(options.VersionId);
            if (!blob.CanGenerateSasUri)
                return Task.FromResult(Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported(
                    "The Azure credential cannot generate SAS URLs; use a shared-key credential or native user-delegation SAS.")));
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var permissions = options.Method == StorageSignedUrlMethod.Read
                ? BlobSasPermissions.Read
                : BlobSasPermissions.Create | BlobSasPermissions.Write;
            var url = blob.GenerateSasUri(permissions, expiresAt);
            return Task.FromResult(Result<StorageSignedUrl>.Success(
                new StorageSignedUrl(url, options.Method, expiresAt)));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result<StorageSignedUrl>.Failure(Map(error, "Create Azure blob signed URL")));
        }
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _container as TClient;
        return client is not null;
    }

    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_container is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Azure Blob does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, DirectoryPrefix(path), cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Success(DirectoryItem(path));
            return Result<StorageItem>.Failure(StorageErrors.NotFound($"Azure blob item '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Azure blob directory info")); }
    }

    private async Task CopyBlobAsync(string sourceName, string destinationName, bool overwrite, CancellationToken cancellationToken)
    {
        var source = _container.GetBlobClient(sourceName);
        var destination = _container.GetBlobClient(destinationName);
        var operation = await destination.StartCopyFromUriAsync(source.Uri, new BlobCopyFromUriOptions
        {
            DestinationConditions = overwrite ? null : new BlobRequestConditions { IfNoneMatch = ETag.All }
        }, cancellationToken).ConfigureAwait(false);
        await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
    }

    private BlobClient Blob(string path) => _container.GetBlobClient(ToKey(path));
    private string ToKey(string path) => _prefix + path;
    private string DirectoryPrefix(string path) => path.Length == 0 ? _prefix : ToKey(path).TrimEnd('/') + "/";
    private string FromKey(string key) => _prefix.Length == 0 ? key : key.StartsWith(_prefix, StringComparison.Ordinal) ? key[_prefix.Length..] : string.Empty;
    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var normalized = Normalize(path);
        return normalized.IsFailure || normalized.Value!.Length > 0
            ? normalized
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private StorageItem? ToItem(BlobHierarchyItem hierarchy)
    {
        if (hierarchy.IsPrefix)
        {
            var path = FromKey(hierarchy.Prefix).TrimEnd('/');
            return path.Length == 0 ? null : DirectoryItem(path);
        }
        return hierarchy.Blob is null ? null : ToItem(hierarchy.Blob);
    }

    private StorageItem? ToItem(BlobItem blob)
    {
        var path = FromKey(blob.Name);
        if (path.Length == 0) return null;
        if (path.EndsWith('/')) return DirectoryItem(path.TrimEnd('/'));
        return new StorageItem
        {
            Path = path,
            Name = NameOf(path),
            ItemType = StorageItemType.File,
            Size = blob.Properties.ContentLength,
            LastModified = blob.Properties.LastModified,
            ContentType = blob.Properties.ContentType,
            ETag = blob.Properties.ETag?.ToString().Trim('"'),
            VersionId = blob.VersionId,
            Metadata = blob.Metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(blob.Metadata, StringComparer.Ordinal)
        };
    }

    private static StorageItem ToItem(string path, BlobProperties properties) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
        Size = path.EndsWith('/') ? null : properties.ContentLength,
        LastModified = properties.LastModified,
        ContentType = properties.ContentType,
        ETag = properties.ETag.ToString().Trim('"'),
        VersionId = properties.VersionId,
        Metadata = new Dictionary<string, string>(properties.Metadata, StringComparer.Ordinal)
    };

    private static Result ValidateCurrentCondition(
        StorageItem current,
        StorageMutationCondition? condition,
        string itemName)
    {
        if (condition is null or { IsEmpty: true })
            return Result.Success();
        if (condition.ExpectedETag is not null &&
            !string.Equals(
                condition.ExpectedETag.Trim('"'),
                current.ETag?.Trim('"'),
                StringComparison.Ordinal))
        {
            return Result.Failure(StorageErrors.Conflict(
                $"The {itemName} ETag no longer matches the requested condition."));
        }
        return condition.ExpectedVersionId is not null &&
               !string.Equals(condition.ExpectedVersionId, current.VersionId, StringComparison.Ordinal)
            ? Result.Failure(StorageErrors.Conflict(
                $"The {itemName} version no longer matches the requested condition."))
            : Result.Success();
    }

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error Map(Exception exception, string operation)
    {
        if (exception is RequestFailedException azure)
        {
            return azure.Status switch
            {
                401 or 403 => StorageErrors.Unauthorized($"{operation}: access was denied."),
                404 => StorageErrors.NotFound($"{operation}: item was not found."),
                408 or 504 => StorageErrors.Timeout($"{operation}: operation timed out."),
                409 or 412 => StorageErrors.Conflict($"{operation}: Azure Blob conflict."),
                429 or >= 500 => StorageErrors.Unavailable($"{operation}: Azure Blob service is unavailable."),
                _ => StorageErrors.ProviderError($"{operation}: Azure Blob request failed.", azure.ErrorCode ?? string.Empty)
            };
        }
        if (exception is TimeoutException or TaskCanceledException) return StorageErrors.Timeout($"{operation}: operation timed out.");
        return StorageErrors.ProviderError($"{operation}: Azure Blob provider failed.");
    }
}
