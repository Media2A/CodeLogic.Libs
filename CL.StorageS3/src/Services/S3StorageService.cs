using Amazon.S3;
using Amazon.S3.Model;
using CL.StorageS3.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

// Aliases to disambiguate from Amazon.S3.Model types with the same name
using BucketInfo = CL.StorageS3.Models.BucketInfo;
using DownloadOptions = CL.StorageS3.Models.DownloadOptions;
using ListObjectsResult = CL.StorageS3.Models.ListObjectsResult;
using S3ObjectInfo = CL.StorageS3.Models.S3ObjectInfo;
using UploadOptions = CL.StorageS3.Models.UploadOptions;

namespace CL.StorageS3.Services;

/// <summary>
/// High-level S3 storage service providing full CRUD operations over buckets and objects.
/// All methods return <see cref="Result{T}"/> or <see cref="Result"/> — exceptions are caught and wrapped.
/// </summary>
public class S3StorageService
{
    private readonly S3ConnectionManager _connectionManager;
    private readonly string _connectionId;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;

    /// <summary>
    /// Initializes a new <see cref="S3StorageService"/>.
    /// </summary>
    /// <param name="connectionManager">The connection manager that owns the underlying client.</param>
    /// <param name="connectionId">Connection ID to use. Defaults to <c>"Default"</c>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="events">Optional event bus for publishing domain events.</param>
    public S3StorageService(
        S3ConnectionManager connectionManager,
        string connectionId = "Default",
        ILogger? logger = null,
        IEventBus? events = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _connectionId = connectionId;
        _logger = logger;
        _events = events;
    }

    // ── Bucket operations ─────────────────────────────────────────────────────

    /// <summary>Creates a new bucket.</summary>
    public async Task<Result<bool>> CreateBucketAsync(string bucketName, CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new PutBucketRequest { BucketName = bucketName };
            await client.PutBucketAsync(request, ct);

            _logger?.Info($"Created bucket '{bucketName}' on connection '{_connectionId}'");
            _events?.Publish(new BucketCreatedEvent(_connectionId, bucketName, DateTime.UtcNow));

            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to create bucket '{bucketName}'", ex);
            return Result<bool>.Failure(Error.FromException(ex, "s3.bucket_create_failed"));
        }
    }

    /// <summary>Deletes a bucket.</summary>
    public async Task<Result<bool>> DeleteBucketAsync(string bucketName, CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new DeleteBucketRequest { BucketName = bucketName };
            await client.DeleteBucketAsync(request, ct);

            _logger?.Info($"Deleted bucket '{bucketName}' on connection '{_connectionId}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to delete bucket '{bucketName}'", ex);
            return Result<bool>.Failure(Error.FromException(ex, "s3.bucket_delete_failed"));
        }
    }

    /// <summary>Lists all buckets accessible to this connection.</summary>
    public async Task<Result<List<BucketInfo>>> ListBucketsAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var response = await client.ListBucketsAsync(ct);

            var buckets = response.Buckets
                .Select(BucketInfo.FromS3Bucket)
                .ToList();

            return Result<List<BucketInfo>>.Success(buckets);
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to list buckets", ex);
            return Result<List<BucketInfo>>.Failure(Error.FromException(ex, "s3.bucket_list_failed"));
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="bucketName"/> exists and is accessible.
    /// </summary>
    public async Task<bool> BucketExistsAsync(string bucketName, CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucketName }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"BucketExists check failed for '{bucketName}': {ex.Message}");
            return false;
        }
    }

    // ── Object operations ─────────────────────────────────────────────────────

    /// <summary>Uploads a byte array as an S3 object.</summary>
    public async Task<Result<S3ObjectInfo>> PutObjectAsync(
        string bucket,
        string key,
        byte[] data,
        UploadOptions? opts = null,
        CancellationToken ct = default)
    {
        using var stream = new MemoryStream(data);
        return await PutObjectAsync(bucket, key, stream, opts, ct);
    }

    /// <summary>Uploads a stream as an S3 object.</summary>
    public async Task<Result<S3ObjectInfo>> PutObjectAsync(
        string bucket,
        string key,
        Stream data,
        UploadOptions? opts = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            opts ??= UploadOptions.Default();

            var request = new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = data
            };

            ApplyUploadOptions(request, opts);
            var response = await client.PutObjectAsync(request, ct);

            // Fetch metadata to populate object info
            var metaRequest = new GetObjectMetadataRequest { BucketName = bucket, Key = key };
            var metaResponse = await client.GetObjectMetadataAsync(metaRequest, ct);
            var info = S3ObjectInfo.FromMetadata(bucket, key, metaResponse);
            info.PublicUrl = BuildPublicUrl(bucket, key);

            _logger?.Info($"Uploaded object '{key}' to bucket '{bucket}' ({info.Size} bytes)");
            _events?.Publish(new ObjectUploadedEvent(_connectionId, bucket, key, info.Size, DateTime.UtcNow));

            return Result<S3ObjectInfo>.Success(info);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to put object '{key}' in bucket '{bucket}'", ex);
            return Result<S3ObjectInfo>.Failure(Error.FromException(ex, "s3.put_object_failed"));
        }
    }

    /// <summary>Downloads an S3 object as a byte array.</summary>
    public async Task<Result<byte[]>> GetObjectAsync(
        string bucket,
        string key,
        DownloadOptions? opts = null,
        CancellationToken ct = default)
    {
        var streamResult = await GetObjectStreamAsync(bucket, key, opts, ct);
        if (streamResult.IsFailure)
            return Result<byte[]>.Failure(streamResult.Error!);

        try
        {
            using var stream = streamResult.Value!;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return Result<byte[]>.Success(ms.ToArray());
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to read stream for object '{key}' in bucket '{bucket}'", ex);
            return Result<byte[]>.Failure(Error.FromException(ex, "s3.get_object_read_failed"));
        }
    }

    /// <summary>Downloads an S3 object and returns its response stream.</summary>
    public async Task<Result<Stream>> GetObjectStreamAsync(
        string bucket,
        string key,
        DownloadOptions? opts = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            opts ??= DownloadOptions.Default();

            var request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            };

            if (!string.IsNullOrEmpty(opts.VersionId))
                request.VersionId = opts.VersionId;

            if (opts.RangeStart.HasValue || opts.RangeEnd.HasValue)
                request.ByteRange = new ByteRange(
                    opts.RangeStart ?? 0,
                    opts.RangeEnd ?? long.MaxValue);

            var response = await client.GetObjectAsync(request, ct);
            return Result<Stream>.Success(response.ResponseStream);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to get object '{key}' from bucket '{bucket}'", ex);
            return Result<Stream>.Failure(Error.FromException(ex, "s3.get_object_failed"));
        }
    }

    /// <summary>Retrieves metadata for an S3 object without downloading its content.</summary>
    public async Task<Result<S3ObjectInfo>> GetObjectInfoAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new GetObjectMetadataRequest { BucketName = bucket, Key = key };
            var response = await client.GetObjectMetadataAsync(request, ct);

            var info = S3ObjectInfo.FromMetadata(bucket, key, response);
            info.PublicUrl = BuildPublicUrl(bucket, key);

            return Result<S3ObjectInfo>.Success(info);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to get object info for '{key}' in bucket '{bucket}'", ex);
            return Result<S3ObjectInfo>.Failure(Error.FromException(ex, "s3.get_object_info_failed"));
        }
    }

    /// <summary>Deletes an S3 object.</summary>
    public async Task<Result<bool>> DeleteObjectAsync(
        string bucket,
        string key,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new DeleteObjectRequest { BucketName = bucket, Key = key };
            await client.DeleteObjectAsync(request, ct);

            _logger?.Info($"Deleted object '{key}' from bucket '{bucket}'");
            _events?.Publish(new ObjectDeletedEvent(_connectionId, bucket, key, DateTime.UtcNow));

            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to delete object '{key}' from bucket '{bucket}'", ex);
            return Result<bool>.Failure(Error.FromException(ex, "s3.delete_object_failed"));
        }
    }

    /// <summary>
    /// Lists objects in a bucket with optional prefix filtering and pagination.
    /// </summary>
    /// <param name="bucket">Target bucket name.</param>
    /// <param name="prefix">Optional key prefix filter.</param>
    /// <param name="continuationToken">Pagination token from a previous call.</param>
    /// <param name="maxKeys">Maximum number of objects to return per page.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<ListObjectsResult>> ListObjectsAsync(
        string bucket,
        string? prefix = null,
        string? continuationToken = null,
        int maxKeys = 1000,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new ListObjectsV2Request
            {
                BucketName = bucket,
                MaxKeys = maxKeys
            };

            if (!string.IsNullOrEmpty(prefix))
                request.Prefix = prefix;

            if (!string.IsNullOrEmpty(continuationToken))
                request.ContinuationToken = continuationToken;

            var response = await client.ListObjectsV2Async(request, ct);

            var result = new ListObjectsResult
            {
                Objects = response.S3Objects.Select(S3ObjectInfo.FromS3Object).ToList(),
                NextContinuationToken = response.NextContinuationToken,
                IsTruncated = response.IsTruncated ?? false,
                CommonPrefixes = [.. response.CommonPrefixes]
            };

            return Result<ListObjectsResult>.Success(result);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to list objects in bucket '{bucket}'", ex);
            return Result<ListObjectsResult>.Failure(Error.FromException(ex, "s3.list_objects_failed"));
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the object identified by <paramref name="key"/> exists in <paramref name="bucket"/>.
    /// </summary>
    public async Task<bool> ObjectExistsAsync(string bucket, string key, CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new GetObjectMetadataRequest { BucketName = bucket, Key = key };
            await client.GetObjectMetadataAsync(request, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"ObjectExists check failed for '{key}' in bucket '{bucket}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Copies an object within or across buckets.</summary>
    public async Task<Result<bool>> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destBucket,
        string destKey,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new CopyObjectRequest
            {
                SourceBucket = sourceBucket,
                SourceKey = sourceKey,
                DestinationBucket = destBucket,
                DestinationKey = destKey
            };

            await client.CopyObjectAsync(request, ct);

            _logger?.Info($"Copied '{sourceBucket}/{sourceKey}' → '{destBucket}/{destKey}'");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to copy '{sourceBucket}/{sourceKey}' to '{destBucket}/{destKey}'", ex);
            return Result<bool>.Failure(Error.FromException(ex, "s3.copy_object_failed"));
        }
    }

    /// <summary>Generates a pre-signed URL that allows temporary access to a private object.</summary>
    /// <param name="bucket">Bucket containing the object.</param>
    /// <param name="key">Object key.</param>
    /// <param name="expiry">How long the URL should remain valid.</param>
    /// <param name="ct">Cancellation token (not used by AWS SDK for pre-sign, provided for API consistency).</param>
    public Task<Result<string>> GeneratePresignedUrlAsync(
        string bucket,
        string key,
        TimeSpan expiry,
        CancellationToken ct = default)
    {
        try
        {
            var client = _connectionManager.GetClient(_connectionId);
            var request = new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = key,
                Expires = DateTime.UtcNow.Add(expiry),
                Verb = HttpVerb.GET
            };

            var url = client.GetPreSignedURL(request);
            return Task.FromResult(Result<string>.Success(url));
        }
        catch (Exception ex)
        {
            _logger?.Error($"Failed to generate pre-signed URL for '{key}' in bucket '{bucket}'", ex);
            return Task.FromResult(Result<string>.Failure(Error.FromException(ex, "s3.presign_failed")));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void ApplyUploadOptions(PutObjectRequest request, UploadOptions opts)
    {
        if (!string.IsNullOrEmpty(opts.ContentType))
            request.ContentType = opts.ContentType;

        if (!string.IsNullOrEmpty(opts.CacheControl))
            request.Headers.CacheControl = opts.CacheControl;

        if (!string.IsNullOrEmpty(opts.ContentDisposition))
            request.Headers.ContentDisposition = opts.ContentDisposition;

        if (!string.IsNullOrEmpty(opts.StorageClass))
            request.StorageClass = new S3StorageClass(opts.StorageClass);

        if (opts.MakePublic)
            request.CannedACL = S3CannedACL.PublicRead;

        foreach (var (metaKey, metaValue) in opts.Metadata)
            request.Metadata[metaKey] = metaValue;
    }

    private string BuildPublicUrl(string bucket, string key)
    {
        var config = _connectionManager.GetConfiguration(_connectionId);
        if (config is null || string.IsNullOrWhiteSpace(config.PublicUrl))
            return "";

        var baseUrl = config.PublicUrl.TrimEnd('/');
        return $"{baseUrl}/{bucket}/{key}";
    }
}
