using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CL.Storage.Providers.GoogleCloud;

/// <summary>Root-scoped storage over Google Cloud Storage.</summary>
public sealed class GoogleCloudStorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageSignedUrlService,
    IStorageVersionService
{
    private static readonly StorageCapabilities GcsCapabilities = new(
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
        StorageFeature.ConditionalCreate |
        StorageFeature.ConditionalUpdate |
        StorageFeature.ConditionalDelete |
        StorageFeature.AtomicReplace |
        StorageFeature.ServerPagination |
        StorageFeature.ResumableUpload |
        StorageFeature.Versioning,
        new StorageLimits { MaxPageSize = 1_000, MaxObjectBytes = 5_497_558_138_880, MaxMetadataBytes = 8 * 1024 });
    private readonly StorageClient _client;
    private readonly UrlSigner? _urlSigner;
    private readonly StorageCapabilities _capabilities;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly int _uploadChunkSize;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private int _disposed;

    public GoogleCloudStorageBackend(
        string connectionId,
        StorageClient client,
        string bucket,
        string? prefix = null,
        int uploadChunkSizeBytes = 10 * 1024 * 1024,
        long maxBufferedDownloadBytes = 67_108_864,
        bool ownsClient = false)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));
        ArgumentNullException.ThrowIfNull(client);
        if (uploadChunkSizeBytes <= 0 || uploadChunkSizeBytes % (256 * 1024) != 0) throw new ArgumentOutOfRangeException(nameof(uploadChunkSizeBytes));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        var normalized = StoragePath.Normalize(prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(prefix));
        ConnectionId = connectionId;
        _client = client;
        try { _urlSigner = client.CreateUrlSigner(); }
        catch (InvalidOperationException) { }
        var features = GcsCapabilities.Features;
        if (_urlSigner is not null)
            features |= StorageFeature.SignedReadUrls | StorageFeature.SignedWriteUrls;
        _capabilities = new StorageCapabilities(features, GcsCapabilities.Limits);
        _bucket = bucket;
        Root = normalized.Value!;
        _prefix = Root.Length == 0 ? string.Empty : Root + "/";
        _uploadChunkSize = uploadChunkSizeBytes;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        _ownsClient = ownsClient;
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.GoogleCloudStorage;
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
            var item = await _client.GetObjectAsync(_bucket, ToKey(normalized.Value), cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalized.Value, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (error.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Google Cloud object info")); }
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
                return Result<StoragePage>.Failure(StorageErrors.Conflict(
                    "A Google Cloud object cannot be listed as a directory."));
        }
        var listingPath = normalized.Value!;
        var listingPrefix = DirectoryPrefix(listingPath);
        var continuation = DecodeContinuationToken(options.ContinuationToken);
        if (continuation.IsFailure)
            return Result<StoragePage>.Failure(continuation.Error!);
        try
        {
            var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
            var providerPageSize = Math.Min(options.PageSize, 1_000);
            var listing = _client.ListObjectsAsync(_bucket, listingPrefix, new ListObjectsOptions
            {
                PageSize = providerPageSize,
                PageToken = continuation.Value!.ProviderPageToken
            });
            var providerPage = await listing.ReadPageAsync(providerPageSize, cancellationToken).ConfigureAwait(false);
            foreach (var item in providerPage)
            {
                var pathValue = FromKey(item.Name);
                if (pathValue.Length == 0) continue;
                if (string.Equals(pathValue.TrimEnd('/'), listingPath, StringComparison.Ordinal)) continue;
                if (options.Recursive)
                {
                    AddParentDirectories(items, pathValue, listingPath);
                    if (pathValue.EndsWith('/')) items[pathValue.TrimEnd('/')] = DirectoryItem(pathValue.TrimEnd('/'));
                    else items[pathValue] = ToItem(pathValue, item);
                }
                else
                {
                    var remainder = pathValue[(listingPath.Length == 0 ? 0 : listingPath.Length + 1)..];
                    var slash = remainder.IndexOf('/');
                    if (slash >= 0)
                    {
                        var directory = listingPath.Length == 0 ? remainder[..slash] : listingPath + "/" + remainder[..slash];
                        items[directory] = DirectoryItem(directory);
                    }
                    else
                    {
                        items[pathValue] = ToItem(pathValue, item);
                    }
                }
            }
            var ordered = items.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            if (continuation.Value.Skip > ordered.Length)
                return Result<StoragePage>.Failure(StorageErrors.InvalidPath(
                    "The Google Cloud continuation token offset is invalid."));
            var pageItems = ordered.Skip(continuation.Value.Skip).Take(options.PageSize).ToArray();
            string? nextToken;
            var nextSkip = continuation.Value.Skip + pageItems.Length;
            if (nextSkip < ordered.Length)
            {
                nextToken = EncodeContinuationToken(new GcsContinuationToken(
                    continuation.Value.ProviderPageToken,
                    nextSkip));
            }
            else
            {
                nextToken = string.IsNullOrEmpty(providerPage.NextPageToken)
                    ? null
                    : EncodeContinuationToken(new GcsContinuationToken(providerPage.NextPageToken, 0));
            }
            return Result<StoragePage>.Success(new StoragePage(pageItems, nextToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Google Cloud objects")); }
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
            await _client.UploadObjectAsync(
                _bucket,
                ToKey(normalized.Value!.TrimEnd('/') + "/"),
                "application/x-directory",
                empty,
                new UploadObjectOptions { ChunkSize = _uploadChunkSize },
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create Google Cloud directory")); }
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(options.Metadata) > 8 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Google Cloud metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
            long? ifGenerationMatch = null;
            if (options.Condition is { IsEmpty: false } condition)
            {
                var expectedGeneration = ParseGeneration(condition.ExpectedVersionId);
                if (expectedGeneration.IsFailure)
                    return Result<StorageItem>.Failure(expectedGeneration.Error!);
                ifGenerationMatch = expectedGeneration.Value;
                if (condition.ExpectedETag is not null)
                {
                    var current = await _client.GetObjectAsync(
                        _bucket,
                        ToKey(normalizedPath),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var conditionResult = ValidateCurrentCondition(
                        ToItem(normalizedPath, current),
                        condition,
                        "Google Cloud object");
                    if (conditionResult.IsFailure)
                        return Result<StorageItem>.Failure(conditionResult.Error!);
                    ifGenerationMatch ??= checked((long?)current.Generation);
                }
            }
            var item = new GcsObject
            {
                Bucket = _bucket,
                Name = ToKey(normalizedPath),
                ContentType = options.ContentType,
                Metadata = options.Metadata.Count == 0 ? null : new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal)
            };
            var uploaded = await _client.UploadObjectAsync(item, source, new UploadObjectOptions
            {
                ChunkSize = _uploadChunkSize,
                IfGenerationMatch = options.Overwrite ? ifGenerationMatch : 0
            }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalizedPath, uploaded));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (!options.Overwrite && error.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The Google Cloud destination already exists."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload Google Cloud object")); }
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
        var generation = ParseGeneration(options.VersionId);
        if (generation.IsFailure) return Result<Stream>.Failure(generation.Error!);
        try
        {
            var item = await _client.GetObjectAsync(
                _bucket,
                ToKey(normalized.Value!),
                new GetObjectOptions { Generation = generation.Value },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var size = item.Size.HasValue ? checked((long)item.Size.Value) : (long?)null;
            if (size.HasValue && options.Offset > size.Value)
                return Result<Stream>.Failure(StorageErrors.InvalidPath(
                    "The range offset exceeds the Google Cloud object length."));
            if (size.HasValue && options.Offset == size.Value)
                return Result<Stream>.Success(new MemoryStream([], writable: false));

            DownloadObjectOptions? downloadOptions = generation.Value.HasValue
                ? new DownloadObjectOptions { Generation = generation.Value }
                : null;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var requestedLength = options.Length;
                if (size.HasValue)
                {
                    var available = size.Value - options.Offset;
                    requestedLength = requestedLength.HasValue
                        ? Math.Min(requestedLength.Value, available)
                        : available;
                }
                if (requestedLength == 0)
                    return Result<Stream>.Success(new MemoryStream([], writable: false));
                var end = requestedLength.HasValue
                    ? options.Offset + requestedLength.Value - 1
                    : (long?)null;
                downloadOptions ??= new DownloadObjectOptions();
                downloadOptions.Range = new RangeHeaderValue(options.Offset, end);
            }

            var pipe = new Pipe(new PipeOptions(
                pool: MemoryPool<byte>.Shared,
                readerScheduler: PipeScheduler.ThreadPool,
                writerScheduler: PipeScheduler.ThreadPool,
                pauseWriterThreshold: 1_048_576,
                resumeWriterThreshold: 524_288,
                minimumSegmentSize: 65_536,
                useSynchronizationContext: false));
            var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producer = DownloadToPipeAsync(
                _bucket,
                ToKey(normalized.Value!),
                pipe.Writer,
                downloadOptions,
                downloadCancellation.Token);
            var stream = new AsyncOwnedResourceStream(
                pipe.Reader.AsStream(),
                async () =>
                {
                    downloadCancellation.Cancel();
                    try { await producer.ConfigureAwait(false); }
                    finally { downloadCancellation.Dispose(); }
                });
            return Result<Stream>.Success(stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Google Cloud object")); }
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
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "Google Cloud virtual directories do not have one atomic identity condition."));
                var prefix = DirectoryPrefix(normalized.Value!);
                var names = new List<string>();
                await foreach (var item in _client.ListObjectsAsync(_bucket, prefix)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    names.Add(item.Name);
                }
                if (!options.Recursive && names.Any(name => name != prefix))
                    return Result.Failure(StorageErrors.Conflict("The Google Cloud directory is not empty."));
                foreach (var name in names)
                    await _client.DeleteObjectAsync(_bucket, name, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "Google Cloud object");
                if (condition.IsFailure) return condition;
                var expectedGeneration = ParseGeneration(
                    options.Condition?.ExpectedVersionId ??
                    (options.Condition?.ExpectedETag is null ? null : info.Value.VersionId));
                if (expectedGeneration.IsFailure) return Result.Failure(expectedGeneration.Error!);
                await _client.DeleteObjectAsync(
                    _bucket,
                    ToKey(normalized.Value!),
                    new DeleteObjectOptions { IfGenerationMatch = expectedGeneration.Value },
                    cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Google Cloud object")); }
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
        var relationship = StorageTransferPath.ValidateDistinct(source.Value!, destination.Value!);
        if (relationship.IsFailure) return relationship;
        try
        {
            var info = await GetInfoAsync(source.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(source.Value!, destination.Value!);
                if (relationship.IsFailure) return relationship;
                var relayed = await StorageTransferCoordinator.CopyAsync(
                    this,
                    source.Value!,
                    this,
                    destination.Value!,
                    options,
                    cancellationToken).ConfigureAwait(false);
                return relayed.IsSuccess ? Result.Success() : Result.Failure(relayed.Error!);
            }
            await CopyObjectAsync(ToKey(source.Value!), ToKey(destination.Value!), options.Overwrite, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (!options.Overwrite && error.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result.Failure(StorageErrors.Conflict("The Google Cloud destination already exists."));
        }
        catch (Exception error) { return Result.Failure(Map(error, "Copy Google Cloud object")); }
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copied = await CopyAsync(sourcePath, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return copied;
        var deleted = await DeleteAsync(sourcePath, new StorageDeleteOptions { Recursive = true }, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess
            ? Result.Success()
            : Result.Failure(StorageErrors.PartialFailure(
                "The Google Cloud destination completed, but the source could not be deleted.",
                $"sourceDeleteError={deleted.Error!.Code};destinationState=complete"));
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.GetBucketAsync(_bucket, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check Google Cloud Storage health")); }
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
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("Google Cloud metadata exceeds the 8 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var expectedGeneration = ParseGeneration(options.ExpectedVersionId);
        if (expectedGeneration.IsFailure) return Result<StorageItem>.Failure(expectedGeneration.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var key = ToKey(normalized.Value!);
            var current = await _client.GetObjectAsync(
                _bucket,
                key,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (options.ExpectedETag is not null &&
                !string.Equals(options.ExpectedETag, current.ETag, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Google Cloud object ETag no longer matches the metadata update condition."));
            }
            if (expectedGeneration.Value.HasValue && current.Generation != expectedGeneration.Value)
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The Google Cloud object generation no longer matches the metadata update condition."));
            }

            var values = options.Mode == StorageMetadataUpdateMode.Merge && current.Metadata is not null
                ? new Dictionary<string, string>(current.Metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in snapshot)
                values[name] = value;
            var patched = await _client.PatchObjectAsync(new GcsObject
            {
                Bucket = _bucket,
                Name = key,
                Metadata = values
            }, new PatchObjectOptions
            {
                IfGenerationMatch = expectedGeneration.Value,
                IfMetagenerationMatch = current.Metageneration
            }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(normalized.Value!, patched));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (GoogleApiException error) when (error.HttpStatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The Google Cloud object changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update Google Cloud metadata")); }
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
        try
        {
            var providerPageSize = Math.Min(options.PageSize, 1_000);
            var listing = _client.ListObjectsAsync(_bucket, key, new ListObjectsOptions
            {
                Versions = true,
                PageSize = providerPageSize,
                PageToken = options.ContinuationToken
            });
            var page = await listing.ReadPageAsync(providerPageSize, cancellationToken).ConfigureAwait(false);
            var versions = new List<StorageVersion>();
            foreach (var version in page)
            {
                if (!string.Equals(version.Name, key, StringComparison.Ordinal))
                    continue;
                if (!version.Generation.HasValue)
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "Google Cloud Storage returned a version without a generation."));
                versions.Add(new StorageVersion
                {
                    Path = normalized.Value!,
                    VersionId = version.Generation.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ETag = version.ETag,
                    Size = version.Size.HasValue ? checked((long)version.Size.Value) : null,
                    LastModified = version.UpdatedDateTimeOffset,
                    IsLatest = version.TimeDeletedDateTimeOffset is null,
                    IsDeleteMarker = false
                });
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage(
                versions.AsReadOnly(),
                page.NextPageToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List Google Cloud object versions")); }
    }

    public async Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = ParseGeneration(versionId);
        if (generation.IsFailure) return Result.Failure(generation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            await _client.DeleteObjectAsync(
                _bucket,
                ToKey(normalized.Value!),
                new DeleteObjectOptions { Generation = generation.Value },
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Google Cloud object version")); }
    }

    public async Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageSignedUrlOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageSignedUrl>.Failure(validation.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageSignedUrl>.Failure(normalized.Error!);
        if (_urlSigner is null)
            return Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported(
                "The Google Cloud credential cannot sign URLs."));
        if (options.VersionId is not null)
            return Result<StorageSignedUrl>.Failure(StorageErrors.Unsupported(
                "Version-specific Google Cloud signed URLs require the native URL signer request template."));
        try
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var urlValue = await _urlSigner.SignAsync(
                _bucket,
                ToKey(normalized.Value!),
                options.ExpiresIn,
                options.Method == StorageSignedUrlMethod.Read ? HttpMethod.Get : HttpMethod.Put,
                signingVersion: null,
                cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(urlValue, UriKind.Absolute, out var url))
                return Result<StorageSignedUrl>.Failure(StorageErrors.ProviderError(
                    "The Google Cloud provider returned an invalid signed URL."));
            return Result<StorageSignedUrl>.Success(new StorageSignedUrl(url, options.Method, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSignedUrl>.Failure(Map(error, "Create Google Cloud signed URL")); }
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"Google Cloud Storage does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient && Interlocked.Exchange(ref _disposed, 1) == 0) _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task DownloadToPipeAsync(
        string bucket,
        string key,
        PipeWriter writer,
        DownloadObjectOptions? options,
        CancellationToken cancellationToken)
    {
        Exception? completionError = null;
        try
        {
            await using var destination = writer.AsStream(leaveOpen: true);
            await _client.DownloadObjectAsync(
                bucket,
                key,
                destination,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            completionError = error is OperationCanceledException
                ? error
                : new IOException("The Google Cloud download stream failed.");
        }
        finally
        {
            try { await writer.CompleteAsync(completionError).ConfigureAwait(false); }
            catch { }
        }
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _client.ListObjectsAsync(_bucket, DirectoryPrefix(path), new ListObjectsOptions { PageSize = 1 })
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                return Result<StorageItem>.Success(DirectoryItem(path));
            }
            return Result<StorageItem>.Failure(StorageErrors.NotFound($"Google Cloud object '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get Google Cloud directory info")); }
    }

    private async Task CopyObjectAsync(string sourceName, string destinationName, bool overwrite, CancellationToken cancellationToken)
    {
        await _client.CopyObjectAsync(
            _bucket,
            sourceName,
            _bucket,
            destinationName,
            new CopyObjectOptions { IfGenerationMatch = overwrite ? null : 0 },
            cancellationToken).ConfigureAwait(false);
    }

    private static void AddParentDirectories(IDictionary<string, StorageItem> items, string itemPath, string listingPath)
    {
        var parent = itemPath.TrimEnd('/');
        while ((parent = Parent(parent)).Length > listingPath.Length)
            items[parent] = DirectoryItem(parent);
    }

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string EncodeContinuationToken(GcsContinuationToken token) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(token)));

    private static Result<GcsContinuationToken> DecodeContinuationToken(string? token)
    {
        if (token is null)
            return Result<GcsContinuationToken>.Success(new GcsContinuationToken(null, 0));
        try
        {
            var decoded = JsonSerializer.Deserialize<GcsContinuationToken>(
                Encoding.UTF8.GetString(Convert.FromBase64String(token)));
            return decoded is not null && decoded.Skip >= 0
                ? Result<GcsContinuationToken>.Success(decoded)
                : Result<GcsContinuationToken>.Failure(StorageErrors.InvalidPath(
                    "The Google Cloud continuation token is invalid."));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return Result<GcsContinuationToken>.Failure(StorageErrors.InvalidPath(
                "The Google Cloud continuation token is invalid."));
        }
    }

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

    private static Result<long?> ParseGeneration(string? value)
    {
        if (value is null) return Result<long?>.Success(null);
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation) && generation >= 0
            ? Result<long?>.Success(generation)
            : Result<long?>.Failure(StorageErrors.InvalidPath(
                "A Google Cloud version ID must be a non-negative numeric generation."));
    }

    private static Result ValidateCurrentCondition(
        StorageItem current,
        StorageMutationCondition? condition,
        string itemName)
    {
        if (condition is null or { IsEmpty: true })
            return Result.Success();
        if (condition.ExpectedETag is not null &&
            !string.Equals(condition.ExpectedETag, current.ETag, StringComparison.Ordinal))
        {
            return Result.Failure(StorageErrors.Conflict(
                $"The {itemName} ETag no longer matches the requested condition."));
        }
        return condition.ExpectedVersionId is not null &&
               !string.Equals(condition.ExpectedVersionId, current.VersionId, StringComparison.Ordinal)
            ? Result.Failure(StorageErrors.Conflict(
                $"The {itemName} generation no longer matches the requested condition."))
            : Result.Success();
    }

    private static StorageItem ToItem(string path, GcsObject item) => new()
    {
        Path = path.TrimEnd('/'),
        Name = NameOf(path.TrimEnd('/')),
        ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
        Size = path.EndsWith('/') || !item.Size.HasValue ? null : checked((long)item.Size.Value),
        LastModified = item.UpdatedDateTimeOffset,
        ContentType = item.ContentType,
        ETag = item.ETag,
        VersionId = item.Generation?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Metadata = item.Metadata is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(item.Metadata, StringComparer.Ordinal)
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error Map(Exception exception, string operation)
    {
        if (exception is GoogleApiException google)
        {
            return google.HttpStatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => StorageErrors.Unauthorized($"{operation}: access was denied."),
                HttpStatusCode.NotFound => StorageErrors.NotFound($"{operation}: item was not found."),
                HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => StorageErrors.Timeout($"{operation}: operation timed out."),
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => StorageErrors.Conflict($"{operation}: Google Cloud Storage conflict."),
                HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => StorageErrors.Unavailable($"{operation}: Google Cloud Storage is unavailable."),
                _ => StorageErrors.ProviderError($"{operation}: Google Cloud request failed.", google.HttpStatusCode.ToString())
            };
        }
        if (exception is TimeoutException or TaskCanceledException) return StorageErrors.Timeout($"{operation}: operation timed out.");
        return StorageErrors.ProviderError($"{operation}: Google Cloud Storage provider failed.");
    }

    private sealed record GcsContinuationToken(string? ProviderPageToken, int Skip);
}
