using System.Diagnostics.CodeAnalysis;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.S3;

/// <summary>Root-scoped storage over Amazon S3 or an S3-compatible service.</summary>
public sealed class S3StorageBackend : IStorageBackend
{
    private static readonly StorageCapabilities S3Capabilities = new(true, true, true, true, true, true);
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _keyPrefix;
    private readonly bool _ownsClient;
    private readonly bool _disablePayloadSigning;
    private readonly bool _disableChecksumValidation;
    private readonly long _maxBufferedDownloadBytes;
    private int _disposed;

    public S3StorageBackend(
        string connectionId,
        IAmazonS3 client,
        string bucket,
        string? prefix = null,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864,
        bool disablePayloadSigning = false,
        bool disableDefaultChecksumValidation = false)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
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
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.S3;
    public string Root { get; }
    public StorageCapabilities Capabilities => S3Capabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
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
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StoragePage>.Failure(valid.Error!);
        var normalized = Normalize(path);
        if (normalized.IsFailure) return Result<StoragePage>.Failure(normalized.Error!);

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

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
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

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        if (!options.Overwrite)
        {
            var exists = await ExistsAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result<StorageItem>.Failure(exists.Error!);
            if (exists.Value) return Result<StorageItem>.Failure(StorageErrors.Conflict("The S3 destination already exists."));
        }

        try
        {
            long? size = source.CanSeek ? Math.Max(0, source.Length - source.Position) : null;
            var request = NewPutRequest(ToKey(normalized.Value!), source, options.ContentType, options.Metadata);
            var response = await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(new StorageItem
            {
                Path = normalized.Value!,
                Name = NameOf(normalized.Value!),
                ItemType = StorageItemType.File,
                Size = size,
                LastModified = DateTimeOffset.UtcNow,
                ContentType = options.ContentType,
                ETag = response.ETag?.Trim('"'),
                Metadata = options.Metadata
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload S3 object")); }
    }

    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var stream = new MemoryStream(content, writable: false);
        return await UploadAsync(path, stream, options, cancellationToken).ConfigureAwait(false);
    }

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

    public async Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageDeleteOptions();
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
        try
        {
            var info = await GetInfoAsync(normalized.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure)
                return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode ? Result.Success() : Result.Failure(info.Error!);

            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                var prefix = ToDirectoryPrefix(normalized.Value!);
                string? token = null;
                do
                {
                    var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket, Prefix = prefix, ContinuationToken = token, MaxKeys = 1000
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
                await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = ToKey(normalized.Value!) }, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete S3 object")); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var sourceInfo = await GetInfoAsync(source.Value!, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure) return Result.Failure(sourceInfo.Error!);
        if (!options.Overwrite)
        {
            var exists = await ExistsAsync(destination.Value!, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result.Failure(exists.Error!);
            if (exists.Value) return Result.Failure(StorageErrors.Conflict("The S3 destination already exists."));
        }
        try
        {
            if (sourceInfo.Value!.ItemType == StorageItemType.Directory)
            {
                var sourcePrefix = ToDirectoryPrefix(source.Value!);
                var destinationPrefix = ToDirectoryPrefix(destination.Value!);
                string? token = null;
                do
                {
                    var page = await _client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        Prefix = sourcePrefix,
                        ContinuationToken = token,
                        MaxKeys = 1000
                    }, cancellationToken).ConfigureAwait(false);
                    foreach (var item in page.S3Objects ?? [])
                    {
                        await CopyObjectAsync(
                            item.Key,
                            destinationPrefix + item.Key[sourcePrefix.Length..],
                            cancellationToken).ConfigureAwait(false);
                    }
                    token = page.NextContinuationToken;
                } while (!string.IsNullOrEmpty(token));
            }
            else
            {
                await CopyObjectAsync(ToKey(source.Value!), ToKey(destination.Value!), cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy S3 object")); }
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copied = await CopyAsync(sourcePath, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return copied;
        var deleted = await DeleteAsync(sourcePath, new StorageDeleteOptions { Recursive = true }, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Result.Success() : Result.Failure(deleted.Error!);
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket, Prefix = _keyPrefix, MaxKeys = 1
            }, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check S3 health")); }
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
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"S3 does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

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
                BucketName = _bucket, Prefix = ToDirectoryPrefix(path), MaxKeys = 1
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

    private Task<CopyObjectResponse> CopyObjectAsync(
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken) =>
        _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            DestinationBucket = _bucket,
            DestinationKey = destinationKey
        }, cancellationToken);

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
        return StorageErrors.ProviderError($"{operation}: S3 provider failed.", exception.Message);
    }
}
