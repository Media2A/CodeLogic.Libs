using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.S3;

/// <summary>Root-scoped storage over Amazon S3 or an S3-compatible service.</summary>
public sealed class S3StorageBackend :
    IStorageBackend,
    IStorageMetadataService,
    IStorageTagService,
    IStorageSignedUrlService,
    IStorageVersionService
{
    private static readonly StorageCapabilities S3Capabilities = new(
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
        StorageFeature.MultipartUpload |
        StorageFeature.SignedReadUrls |
        StorageFeature.SignedWriteUrls |
        StorageFeature.Versioning);
    private readonly IAmazonS3 _client;
    private readonly StorageCapabilities _capabilities;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly bool _ownsClient;
    private readonly bool _disablePayloadSigning;
    private readonly bool _disableChecksumValidation;
    private readonly int _multipartPartSizeBytes;
    private readonly long _multipartThresholdBytes;
    private readonly long _maxBufferedDownloadBytes;
    private int _disposed;

    /// <summary>Initializes a backend over an Amazon S3 or S3-compatible client.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="client">S3 client used for all operations.</param>
    /// <param name="bucket">Bucket mounted by this connection.</param>
    /// <param name="prefix">Optional key prefix mounted as the connection root.</param>
    /// <param name="ownsClient">Whether disposal of this backend also disposes the client.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="disablePayloadSigning">Whether compatible endpoints receive unsigned request payloads.</param>
    /// <param name="disableDefaultChecksumValidation">Whether SDK default response checksum validation is disabled.</param>
    /// <param name="multipartPartSizeBytes">Part size used for multipart uploads.</param>
    /// <param name="multipartThresholdBytes">Content size at which multipart upload begins.</param>
    public S3StorageBackend(
        string connectionId,
        IAmazonS3 client,
        string bucket,
        string? prefix = null,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864,
        bool disablePayloadSigning = false,
        bool disableDefaultChecksumValidation = false,
        int multipartPartSizeBytes = 16 * 1024 * 1024,
        long multipartThresholdBytes = 64L * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        if (multipartPartSizeBytes is < 5 * 1024 * 1024 or > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(multipartPartSizeBytes));
        if (multipartThresholdBytes < multipartPartSizeBytes)
            throw new ArgumentOutOfRangeException(nameof(multipartThresholdBytes));
        ArgumentNullException.ThrowIfNull(client);

        var normalized = StoragePath.Normalize(prefix ?? string.Empty);
        if (normalized.IsFailure) throw new ArgumentException(normalized.Error!.Message, nameof(prefix));
        ConnectionId = connectionId;
        _client = client;
        _bucket = bucket;
        Root = normalized.Value!;
        _keyPrefix = Root.Length == 0 ? string.Empty : Root + "/";
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
        _disablePayloadSigning = disablePayloadSigning;
        _disableChecksumValidation = disableDefaultChecksumValidation;
        _multipartPartSizeBytes = multipartPartSizeBytes;
        _multipartThresholdBytes = multipartThresholdBytes;
        _capabilities = new StorageCapabilities(S3Capabilities.Features, new StorageLimits
        {
            MaxPageSize = 1_000,
            MaxObjectBytes = checked((long)multipartPartSizeBytes * 10_000),
            MaxSingleUploadBytes = 5L * 1024 * 1024 * 1024,
            MaxMetadataBytes = 2 * 1024,
            MaxTags = 10,
            PreferredUploadPartBytes = multipartPartSizeBytes
        });
    }

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.S3;
    /// <inheritdoc />
    public string Root { get; }
    /// <inheritdoc />
    public StorageCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));

        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value)
            }, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(FromMetadata(normalized.Value, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return await GetDirectoryInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get S3 item info")); }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsSuccess) return Result<bool>.Success(true);
        return info.Error?.Code == StorageErrors.NotFoundCode
            ? Result<bool>.Success(false)
            : Result<bool>.Failure(info.Error!);
    }

    /// <inheritdoc />
    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageListOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StoragePage>.Failure(valid.Error!);
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);
        if (normalized.Value!.Length > 0)
        {
            var directory = await GetInfoAsync(normalized.Value, cancellationToken).ConfigureAwait(false);
            if (directory.IsFailure) return Result<StoragePage>.Failure(directory.Error!);
            if (directory.Value!.ItemType != StorageItemType.Directory)
                return Result<StoragePage>.Failure(StorageErrors.Conflict("An S3 file cannot be listed as a directory."));
        }

        try
        {
            var request = new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = ToDirectoryPrefix(normalized.Value!),
                Delimiter = options.Recursive ? null : "/",
                MaxKeys = options.PageSize,
                ContinuationToken = options.ContinuationToken
            };
            var response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            var items = new List<StorageItem>();
            foreach (var prefix in response.CommonPrefixes ?? [])
            {
                var relative = FromKey(prefix).TrimEnd('/');
                if (relative.Length > 0) items.Add(DirectoryItem(relative));
            }
            foreach (var item in response.S3Objects ?? [])
            {
                var relative = FromKey(item.Key);
                if (relative.Length == 0) continue;
                if (relative.EndsWith('/'))
                    items.Add(DirectoryItem(relative.TrimEnd('/')));
                else
                    items.Add(FromListedObject(relative, item));
            }
            var unique = items.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            return Result<StoragePage>.Success(new StoragePage(unique, response.NextContinuationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List S3 objects")); }
    }

    /// <inheritdoc />
    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        if (normalized.Value!.Length == 0) return Result.Success();
        await using var empty = new MemoryStream([]);
        try
        {
            await _client.PutObjectAsync(NewPutRequest(ToKey(normalized.Value!) + "/", empty, "application/x-directory", null), cancellationToken)
                .ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create S3 directory")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (StorageOptionValidation.MetadataSizeBytes(options.Metadata) > 2 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("S3 user metadata exceeds the 2 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);

        try
        {
            long? size = source.CanSeek ? Math.Max(0, source.Length - source.Position) : null;
            var key = ToKey(normalized.Value!);
            var ifMatch = await ResolveUploadIfMatchAsync(
                key,
                options.Condition,
                cancellationToken).ConfigureAwait(false);
            if (ifMatch.IsFailure) return Result<StorageItem>.Failure(ifMatch.Error!);
            S3UploadCompletion completion;
            if (!size.HasValue || size.Value >= _multipartThresholdBytes || size.Value > 5L * 1024 * 1024 * 1024)
            {
                completion = await UploadMultipartAsync(
                    key,
                    source,
                    options,
                    ifMatch.Value,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var request = NewPutRequest(key, source, options.ContentType, options.Metadata);
                if (!options.Overwrite)
                    request.IfNoneMatch = "*";
                else if (ifMatch.Value is not null)
                    request.IfMatch = ifMatch.Value;
                var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                completion = new S3UploadCompletion(response.ETag?.Trim('"'), response.VersionId, size);
            }
            return Result<StorageItem>.Success(new StorageItem
            {
                Path = normalized.Value!,
                Name = NameOf(normalized.Value!),
                ItemType = StorageItemType.File,
                Size = completion.Bytes,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = options.ContentType,
                ETag = completion.ETag,
                VersionId = completion.VersionId,
                Metadata = options.Metadata
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload S3 object")); }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var stream = new MemoryStream(content, writable: false);
        return await UploadAsync(path, stream, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<Stream>.Failure(valid.Error!);
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<Stream>.Failure(normalized.Error!);
        try
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = ToKey(normalized.Value!) };
            if (options.VersionId is not null)
                request.VersionId = options.VersionId;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : long.MaxValue;
                request.ByteRange = new ByteRange(options.Offset, end);
            }
            var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return Result<Stream>.Success(new OwnedResourceStream(response.ResponseStream, response));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download S3 object")); }
    }

    /// <inheritdoc />
    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDownloadOptions();
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;
        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var target = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (target.Length > limit - read || target.Length > int.MaxValue - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            target.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(target.ToArray());
    }

    /// <inheritdoc />
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
            if (info.IsFailure)
                return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode ? Result.Success() : Result.Failure(info.Error!);

            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (options.Condition is { IsEmpty: false })
                    return Result.Failure(StorageErrors.Unsupported(
                        "S3 virtual directories do not have one atomic identity condition."));
                var prefix = ToDirectoryPrefix(normalized.Value!);
                string? token = null;
                do
                {
                    var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = prefix,
                        ContinuationToken = token,
                        MaxKeys = 1000
                    }, cancellationToken).ConfigureAwait(false);
                    if (!options.Recursive && (page.S3Objects?.Any(item => item.Key != prefix) ?? false))
                        return Result.Failure(StorageErrors.Conflict("The S3 directory is not empty."));
                    foreach (var item in page.S3Objects ?? [])
                        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = item.Key }, cancellationToken).ConfigureAwait(false);
                    token = page.NextContinuationToken;
                } while (!string.IsNullOrEmpty(token));
                await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = prefix }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var condition = ValidateCurrentCondition(info.Value, options.Condition, "S3 object");
                if (condition.IsFailure) return condition;
                await _client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = _bucket,
                    Key = ToKey(normalized.Value!),
                    IfMatch = options.Condition?.ExpectedETag ??
                        (options.Condition?.ExpectedVersionId is null ? null : info.Value.ETag)
                }, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object")); }
    }

    /// <inheritdoc />
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
        var sourceInfo = await GetInfoAsync(source.Value!, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure) return Result.Failure(sourceInfo.Error!);
        if (sourceInfo.Value!.ItemType == StorageItemType.Directory)
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
        if (!options.Overwrite)
        {
            var exists = await ExistsAsync(destination.Value!, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result.Failure(exists.Error!);
            if (exists.Value) return Result.Failure(StorageErrors.Conflict("The S3 destination already exists."));
        }
        try
        {
            await CopyObjectAsync(
                ToKey(source.Value!),
                ToKey(destination.Value!),
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy S3 object")); }
    }

    /// <inheritdoc />
    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copied = await CopyAsync(sourcePath, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return copied;
        var deleted = await DeleteAsync(sourcePath, new StorageDeleteOptions { Recursive = true }, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess
            ? Result.Success()
            : Result.Failure(StorageErrors.PartialFailure(
                "The S3 destination completed, but the source could not be deleted.",
                $"sourceDeleteError={deleted.Error!.Code};destinationState=complete"));
    }

    /// <inheritdoc />
    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = _keyPrefix,
                MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check S3 health")); }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        return info.IsSuccess
            ? Result<IReadOnlyDictionary<string, string>>.Success(info.Value!.Metadata)
            : Result<IReadOnlyDictionary<string, string>>.Failure(info.Error!);
    }

    /// <inheritdoc />
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
        if (StorageOptionValidation.MetadataSizeBytes(metadata) > 2 * 1024)
            return Result<StorageItem>.Failure(StorageErrors.TooLarge("S3 user metadata exceeds the 2 KiB limit."));
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var snapshot = StorageMetadataSnapshot.Create(metadata);
        try
        {
            var key = ToKey(normalized.Value!);
            var current = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken).ConfigureAwait(false);
            if (options.ExpectedVersionId is not null &&
                !string.Equals(options.ExpectedVersionId, current.VersionId, StringComparison.Ordinal))
            {
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    "The S3 object version no longer matches the metadata update condition."));
            }

            var values = options.Mode == StorageMetadataUpdateMode.Merge
                ? current.Metadata.Keys.ToDictionary(name => name, name => current.Metadata[name], StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in snapshot)
                values[name] = value;

            var request = new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = key,
                DestinationBucket = _bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.REPLACE,
                ContentType = current.Headers.ContentType,
                CacheControl = current.Headers.CacheControl,
                ContentDisposition = current.Headers.ContentDisposition,
                ContentEncoding = current.Headers.ContentEncoding,
                ContentLanguage = current.Headers.ContentLanguage,
                ETagToMatch = options.ExpectedETag,
                IfMatch = options.ExpectedETag ?? (options.ExpectedVersionId is null ? null : current.ETag)
            };
            foreach (var (name, value) in values)
                request.Metadata[name] = value;
            await _client.CopyObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                "The S3 object changed before its metadata could be updated."));
        }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update S3 metadata")); }
    }

    /// <inheritdoc />
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
            var response = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!)
            }, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyDictionary<string, string>>.Success(
                StorageMetadataSnapshot.Create(
                    (response.Tagging ?? []).Select(tag =>
                        new KeyValuePair<string, string>(tag.Key, tag.Value))));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            return Result<IReadOnlyDictionary<string, string>>.Failure(Map(error, "Read S3 object tags"));
        }
    }

    /// <inheritdoc />
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
            var key = ToKey(normalized.Value!);
            if (options.Mode == StorageTagUpdateMode.Merge)
            {
                var current = await _client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                {
                    BucketName = _bucket,
                    Key = key
                }, cancellationToken).ConfigureAwait(false);
                foreach (var tag in current.Tagging ?? [])
                    values[tag.Key] = tag.Value;
            }
            foreach (var (name, value) in tags)
                values[name] = value;
            validation = options.Validate(values);
            if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);

            await _client.PutObjectTaggingAsync(new PutObjectTaggingRequest
            {
                BucketName = _bucket,
                Key = key,
                Tagging = new Tagging
                {
                    TagSet = values.Select(pair => new Amazon.S3.Model.Tag
                    {
                        Key = pair.Key,
                        Value = pair.Value
                    }).ToList()
                }
            }, cancellationToken).ConfigureAwait(false);
            return await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Update S3 object tags")); }
    }

    /// <inheritdoc />
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
        var continuation = DecodeVersionContinuation(options.ContinuationToken);
        if (continuation.IsFailure) return Result<StorageVersionPage>.Failure(continuation.Error!);
        var key = ToKey(normalized.Value!);

        try
        {
            var response = await _client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = _bucket,
                Prefix = key,
                MaxKeys = Math.Min(options.PageSize, 1_000),
                KeyMarker = continuation.Value!.KeyMarker,
                VersionIdMarker = continuation.Value.VersionIdMarker
            }, cancellationToken).ConfigureAwait(false);
            var versions = new List<StorageVersion>();
            foreach (var version in response.Versions ?? [])
            {
                if (!string.Equals(version.Key, key, StringComparison.Ordinal) ||
                    (!options.IncludeDeleteMarkers && version.IsDeleteMarker == true))
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(version.VersionId))
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "S3 returned a version without a version identifier."));
                versions.Add(new StorageVersion
                {
                    Path = normalized.Value!,
                    VersionId = version.VersionId,
                    ETag = version.ETag?.Trim('"'),
                    Size = version.Size,
                    LastModified = version.LastModified.HasValue
                        ? new DateTimeOffset(version.LastModified.Value.ToUniversalTime())
                        : null,
                    IsLatest = version.IsLatest == true,
                    IsDeleteMarker = version.IsDeleteMarker == true
                });
            }

            string? next = null;
            if (response.IsTruncated == true)
            {
                if (string.IsNullOrEmpty(response.NextKeyMarker))
                    return Result<StorageVersionPage>.Failure(StorageErrors.ProviderError(
                        "S3 truncated a version page without returning a continuation marker."));
                next = EncodeVersionContinuation(new S3VersionContinuation(
                    response.NextKeyMarker,
                    response.NextVersionIdMarker));
            }
            return Result<StorageVersionPage>.Success(new StorageVersionPage(versions.AsReadOnly(), next));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageVersionPage>.Failure(Map(error, "List S3 object versions")); }
    }

    /// <inheritdoc />
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
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                VersionId = versionId
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object version")); }
    }

    /// <inheritdoc />
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
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(options.ExpiresIn);
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = ToKey(normalized.Value!),
                Expires = expiresAt.UtcDateTime,
                Verb = options.Method == StorageSignedUrlMethod.Read ? HttpVerb.GET : HttpVerb.PUT,
                ContentType = options.ContentType,
                VersionId = options.VersionId
            };
            var value = await _client.GetPreSignedURLAsync(request).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url))
                return Result<StorageSignedUrl>.Failure(StorageErrors.ProviderError(
                    "The S3 provider returned an invalid signed URL."));
            return Result<StorageSignedUrl>.Success(new StorageSignedUrl(url, options.Method, expiresAt));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageSignedUrl>.Failure(Map(error, "Create S3 signed URL")); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = _client as TClient;
        return client is not null;
    }

    /// <inheritdoc />
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not TClient typed)
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"S3 does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsClient && Interlocked.Exchange(ref _disposed, 1) == 0)
            _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<Result<StorageItem>> GetDirectoryInfoAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = ToDirectoryPrefix(path),
                MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return (response.KeyCount ?? 0) > 0
                ? Result<StorageItem>.Success(DirectoryItem(path))
                : Result<StorageItem>.Failure(StorageErrors.NotFound($"S3 item '{path}' was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get S3 directory info")); }
    }

    private PutObjectRequest NewPutRequest(string key, Stream source, string? contentType, IReadOnlyDictionary<string, string>? metadata)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = source,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            DisablePayloadSigning = _disablePayloadSigning,
            DisableDefaultChecksumValidation = _disableChecksumValidation
        };
        if (!string.IsNullOrWhiteSpace(contentType)) request.ContentType = contentType;
        if (metadata is not null)
            foreach (var (name, value) in metadata) request.Metadata[name] = value;
        return request;
    }

    private async Task<Result<string?>> ResolveUploadIfMatchAsync(
        string key,
        StorageMutationCondition? condition,
        CancellationToken cancellationToken)
    {
        if (condition is null or { IsEmpty: true })
            return Result<string?>.Success(null);
        if (condition.ExpectedVersionId is null)
            return Result<string?>.Success(condition.ExpectedETag);
        try
        {
            var current = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken).ConfigureAwait(false);
            var item = FromMetadata(FromKey(key), current);
            var validation = ValidateCurrentCondition(item, condition, "S3 object");
            return validation.IsFailure
                ? Result<string?>.Failure(validation.Error!)
                : Result<string?>.Success(condition.ExpectedETag ?? current.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AmazonS3Exception error) when (IsNotFound(error))
        {
            return Result<string?>.Failure(StorageErrors.Conflict(
                "The S3 object no longer exists for the requested upload condition."));
        }
        catch (Exception error)
        {
            return Result<string?>.Failure(Map(error, "Resolve S3 upload condition"));
        }
    }

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

    private async Task<S3UploadCompletion> UploadMultipartAsync(
        string key,
        Stream source,
        StorageUploadOptions options,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_multipartPartSizeBytes);
        string? uploadId = null;
        try
        {
            var first = await ReadPartAsync(source, buffer, _multipartPartSizeBytes, cancellationToken).ConfigureAwait(false);
            if (first.EndOfStream)
            {
                await using var content = new MemoryStream(buffer, 0, first.Count, writable: false, publiclyVisible: true);
                var request = NewPutRequest(key, content, options.ContentType, options.Metadata);
                if (!options.Overwrite)
                    request.IfNoneMatch = "*";
                else if (ifMatch is not null)
                    request.IfMatch = ifMatch;
                var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                return new S3UploadCompletion(response.ETag?.Trim('"'), response.VersionId, first.Count);
            }

            var initiate = new InitiateMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                ContentType = options.ContentType
            };
            foreach (var (name, value) in options.Metadata)
                initiate.Metadata[name] = value;
            var initiated = await _client.InitiateMultipartUploadAsync(initiate, cancellationToken).ConfigureAwait(false);
            uploadId = initiated.UploadId;
            var parts = new List<PartETag>();
            long totalBytes = 0;
            var partNumber = 1;
            var current = first;
            while (current.Count > 0)
            {
                if (partNumber > 10_000)
                    throw new StorageMultipartLimitException();
                await using var partStream = new MemoryStream(
                    buffer,
                    0,
                    current.Count,
                    writable: false,
                    publiclyVisible: true);
                var uploaded = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartNumber = partNumber,
                    PartSize = current.Count,
                    InputStream = partStream,
                    IsLastPart = current.EndOfStream,
                    DisablePayloadSigning = _disablePayloadSigning,
                    DisableDefaultChecksumValidation = _disableChecksumValidation
                }, cancellationToken).ConfigureAwait(false);
                parts.Add(new PartETag(uploaded));
                totalBytes = checked(totalBytes + current.Count);
                partNumber++;
                if (current.EndOfStream)
                    break;
                current = await ReadPartAsync(source, buffer, _multipartPartSizeBytes, cancellationToken).ConfigureAwait(false);
            }

            var completeRequest = new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                UploadId = uploadId,
                PartETags = parts
            };
            if (!options.Overwrite)
                completeRequest.IfNoneMatch = "*";
            else if (ifMatch is not null)
                completeRequest.IfMatch = ifMatch;
            var completed = await _client.CompleteMultipartUploadAsync(
                completeRequest,
                cancellationToken).ConfigureAwait(false);
            uploadId = null;
            return new S3UploadCompletion(completed.ETag?.Trim('"'), completed.VersionId, totalBytes);
        }
        catch (StorageMultipartLimitException)
        {
            throw;
        }
        finally
        {
            if (uploadId is not null)
            {
                try
                {
                    await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                    {
                        BucketName = _bucket,
                        Key = key,
                        UploadId = uploadId
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<S3PartRead> ReadPartAsync(
        Stream source,
        byte[] buffer,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var count = 0;
        while (count < maxCount)
        {
            var read = await source.ReadAsync(buffer.AsMemory(count, maxCount - count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return new S3PartRead(count, EndOfStream: true);
            count += read;
        }
        return new S3PartRead(count, EndOfStream: false);
    }

    private Task<CopyObjectResponse> CopyObjectAsync(
        string sourceKey,
        string destinationKey,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var request = new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            DestinationBucket = _bucket,
            DestinationKey = destinationKey
        };
        if (!overwrite)
            request.IfNoneMatch = "*";
        return _client.CopyObjectAsync(request, cancellationToken);
    }

    private Result<string> Normalize(string path) => StoragePath.Normalize(path);
    private Result<string> NormalizeRequired(string path)
    {
        var result = Normalize(path);
        return result.IsFailure || result.Value!.Length > 0
            ? result
            : Result<string>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
    }

    private string ToKey(string path) => _keyPrefix + path;
    private string ToDirectoryPrefix(string path) => path.Length == 0 ? _keyPrefix : ToKey(path).TrimEnd('/') + "/";
    private string FromKey(string key) => _keyPrefix.Length == 0 ? key : key.StartsWith(_keyPrefix, StringComparison.Ordinal) ? key[_keyPrefix.Length..] : string.Empty;
    private static string NameOf(string path) => path.Split('/')[^1];
    private static StorageItem DirectoryItem(string path) => new() { Path = path, Name = path.Length == 0 ? string.Empty : NameOf(path), ItemType = StorageItemType.Directory };

    private static StorageItem FromMetadata(string path, GetObjectMetadataResponse response) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = response.ContentLength,
        LastModified = response.LastModified.HasValue ? new DateTimeOffset(response.LastModified.Value) : null,
        ContentType = response.Headers.ContentType,
        ETag = response.ETag?.Trim('"'),
        VersionId = response.VersionId,
        Metadata = response.Metadata.Keys.ToDictionary(key => key, key => response.Metadata[key], StringComparer.Ordinal)
    };

    private static StorageItem FromListedObject(string path, S3Object item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = item.Size,
        LastModified = item.LastModified.HasValue ? new DateTimeOffset(item.LastModified.Value) : null,
        ETag = item.ETag?.Trim('"')
    };

    private static bool IsNotFound(AmazonS3Exception error) => error.StatusCode == HttpStatusCode.NotFound ||
        error.ErrorCode is "NoSuchKey" or "NoSuchBucket" or "NotFound";

    private static Error Map(Exception exception, string operation)
    {
        if (exception is StorageMultipartLimitException)
            return StorageErrors.TooLarge($"{operation}: the multipart upload exceeds 10,000 parts.");
        if (exception is AmazonS3Exception s3)
        {
            if (IsNotFound(s3)) return StorageErrors.NotFound($"{operation}: item was not found.");
            if (s3.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                s3.ErrorCode is "AccessDenied" or "InvalidAccessKeyId" or "SignatureDoesNotMatch")
                return StorageErrors.Unauthorized($"{operation}: access was denied.");
            if (s3.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                return StorageErrors.Conflict($"{operation}: provider conflict.");
            if ((int)s3.StatusCode >= 500 || s3.StatusCode == (HttpStatusCode)429)
                return StorageErrors.Unavailable($"{operation}: S3 service is unavailable.");
            return StorageErrors.ProviderError($"{operation}: S3 request failed.", s3.ErrorCode ?? string.Empty);
        }
        if (exception is TimeoutException or TaskCanceledException)
            return StorageErrors.Timeout($"{operation}: operation timed out.");
        if (exception is HttpRequestException)
            return StorageErrors.Unavailable($"{operation}: S3 service is unavailable.");
        return StorageErrors.ProviderError($"{operation}: S3 provider failed.");
    }

    private static string EncodeVersionContinuation(S3VersionContinuation continuation) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(continuation)));

    private static Result<S3VersionContinuation> DecodeVersionContinuation(string? continuationToken)
    {
        if (string.IsNullOrEmpty(continuationToken))
            return Result<S3VersionContinuation>.Success(new S3VersionContinuation(null, null));
        try
        {
            var value = JsonSerializer.Deserialize<S3VersionContinuation>(
                Encoding.UTF8.GetString(Convert.FromBase64String(continuationToken)));
            return value is null
                ? Result<S3VersionContinuation>.Failure(StorageErrors.InvalidPath(
                    "The S3 version continuation token is invalid."))
                : Result<S3VersionContinuation>.Success(value);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return Result<S3VersionContinuation>.Failure(StorageErrors.InvalidPath(
                "The S3 version continuation token is invalid."));
        }
    }

    private sealed record S3UploadCompletion(string? ETag, string? VersionId, long? Bytes);
    private sealed record S3VersionContinuation(string? KeyMarker, string? VersionIdMarker);
    private readonly record struct S3PartRead(int Count, bool EndOfStream);
    private sealed class StorageMultipartLimitException : Exception { }
}
