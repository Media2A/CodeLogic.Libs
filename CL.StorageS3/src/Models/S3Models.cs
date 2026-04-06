using Amazon.S3.Model;

namespace CL.StorageS3.Models;

/// <summary>
/// Represents metadata and identity information about an S3 object.
/// </summary>
public class S3ObjectInfo
{
    /// <summary>The object key (path within the bucket).</summary>
    public string Key { get; set; } = "";

    /// <summary>The bucket containing this object.</summary>
    public string BucketName { get; set; } = "";

    /// <summary>Size of the object in bytes.</summary>
    public long Size { get; set; }

    /// <summary>UTC timestamp of when the object was last modified.</summary>
    public DateTime LastModified { get; set; }

    /// <summary>Entity tag (MD5 hash or multipart identifier) of the object.</summary>
    public string ETag { get; set; } = "";

    /// <summary>S3 storage class (e.g. STANDARD, INTELLIGENT_TIERING).</summary>
    public string StorageClass { get; set; } = "";

    /// <summary>MIME content type of the object.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>User-defined metadata key/value pairs attached to the object.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>Public-facing URL for this object (when configured via <c>PublicUrl</c>).</summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>
    /// Creates an <see cref="S3ObjectInfo"/> from an <see cref="S3Object"/> returned by list operations.
    /// </summary>
    public static S3ObjectInfo FromS3Object(S3Object s3Object) => new()
    {
        Key = s3Object.Key,
        BucketName = s3Object.BucketName,
        Size = s3Object.Size ?? 0,
        LastModified = s3Object.LastModified ?? DateTime.MinValue,
        ETag = s3Object.ETag?.Trim('"') ?? "",
        StorageClass = s3Object.StorageClass?.Value ?? ""
    };

    /// <summary>
    /// Creates an <see cref="S3ObjectInfo"/> from a <see cref="GetObjectMetadataResponse"/>.
    /// </summary>
    public static S3ObjectInfo FromMetadata(string bucket, string key, GetObjectMetadataResponse response) => new()
    {
        Key = key,
        BucketName = bucket,
        Size = response.ContentLength,
        LastModified = response.LastModified ?? DateTime.MinValue,
        ETag = response.ETag?.Trim('"') ?? "",
        StorageClass = response.StorageClass?.Value ?? "",
        ContentType = response.Headers.ContentType ?? "",
        Metadata = response.Metadata.Keys
            .ToDictionary(k => k, k => response.Metadata[k])
    };
}

/// <summary>
/// Represents information about an S3 bucket.
/// </summary>
public class BucketInfo
{
    /// <summary>Bucket name.</summary>
    public string Name { get; set; } = "";

    /// <summary>UTC timestamp when the bucket was created.</summary>
    public DateTime CreationDate { get; set; }

    /// <summary>AWS region the bucket resides in (may be empty for non-AWS providers).</summary>
    public string Region { get; set; } = "";

    /// <summary>
    /// Creates a <see cref="BucketInfo"/> from an <see cref="S3Bucket"/> descriptor.
    /// </summary>
    public static BucketInfo FromS3Bucket(S3Bucket bucket) => new()
    {
        Name = bucket.BucketName,
        CreationDate = bucket.CreationDate ?? DateTime.MinValue
    };
}

/// <summary>
/// Result of a paginated list-objects operation.
/// </summary>
public class ListObjectsResult
{
    /// <summary>Objects returned in this page.</summary>
    public List<S3ObjectInfo> Objects { get; set; } = [];

    /// <summary>Continuation token to pass to retrieve the next page, or <see langword="null"/> when exhausted.</summary>
    public string? NextContinuationToken { get; set; }

    /// <summary>Whether more results exist beyond this page.</summary>
    public bool IsTruncated { get; set; }

    /// <summary>Common key prefixes returned when a delimiter is specified (virtual folders).</summary>
    public List<string> CommonPrefixes { get; set; } = [];

    /// <summary>Number of objects in <see cref="Objects"/>.</summary>
    public int Count => Objects.Count;
}

/// <summary>
/// Options that control how an object is uploaded to S3.
/// </summary>
public class UploadOptions
{
    /// <summary>MIME content type (e.g. <c>image/jpeg</c>). Leave empty to omit.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>User-defined metadata key/value pairs to attach to the object.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];

    /// <summary>S3 storage class (e.g. STANDARD, REDUCED_REDUNDANCY). Leave empty for default.</summary>
    public string StorageClass { get; set; } = "";

    /// <summary>HTTP Cache-Control header value. Leave empty to omit.</summary>
    public string CacheControl { get; set; } = "";

    /// <summary>HTTP Content-Disposition header value. Leave empty to omit.</summary>
    public string ContentDisposition { get; set; } = "";

    /// <summary>Whether to set the object ACL to public-read.</summary>
    public bool MakePublic { get; set; } = false;

    /// <summary>Returns a default <see cref="UploadOptions"/> with no overrides.</summary>
    public static UploadOptions Default() => new();
}

/// <summary>
/// Options that control how an object is downloaded from S3.
/// </summary>
public class DownloadOptions
{
    /// <summary>Byte offset at which to start the download (for range requests). Null means start of object.</summary>
    public long? RangeStart { get; set; }

    /// <summary>Byte offset at which to end the download (inclusive, for range requests). Null means end of object.</summary>
    public long? RangeEnd { get; set; }

    /// <summary>Version ID to retrieve a specific object version. Null retrieves the current version.</summary>
    public string? VersionId { get; set; }

    /// <summary>Returns a default <see cref="DownloadOptions"/> with no overrides.</summary>
    public static DownloadOptions Default() => new();
}
