using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace Storage.Tests;

/// <summary>
/// An in-memory S3 bucket behind <see cref="IAmazonS3"/>, enough for the backend's copy, move, upload, and probe
/// paths. Conditions are enforced as configured, so both AWS (enforces everything) and MinIO (ignores
/// If-None-Match/If-Match on CopyObject and If-Match on DeleteObject) can be imitated.
/// </summary>
public class ProviderFakeS3 : DispatchProxy
{
    public sealed class FakeObject
    {
        public byte[]? Content { get; set; }
        public long Size { get; set; }
        public string ETag { get; set; } = "";
        public string VersionId { get; set; } = Guid.NewGuid().ToString("N");
        public string? ContentType { get; set; }
        public string? CacheControl { get; set; }
        public string? ContentEncoding { get; set; }
        public string? ContentDisposition { get; set; }
        public string? StorageClass { get; set; }
        public string? Encryption { get; set; }
        public string? KmsKeyId { get; set; }
        public string? CustomerEncryption { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Amazon.S3.Model.Tag> Tags { get; set; } = [];
    }

    private sealed class Upload
    {
        public required InitiateMultipartUploadRequest Request { get; init; }
        public ConcurrentDictionary<int, (byte[]? Bytes, long Size, string ETag)> Parts { get; } = new();
    }

    public static ProviderFakeS3 Create(bool enforceCopyConditions = true, bool enforceDeleteCondition = true)
    {
        var client = (ProviderFakeS3)(object)DispatchProxy.Create<IAmazonS3, ProviderFakeS3>();
        client.EnforceCopyConditions = enforceCopyConditions;
        client.EnforceDeleteCondition = enforceDeleteCondition;
        return client;
    }

    public IAmazonS3 Client => (IAmazonS3)(object)this;
    public ConcurrentDictionary<string, FakeObject> Objects { get; } = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Upload> _uploads = new();
    public bool EnforceCopyConditions { get; set; } = true;
    public bool EnforceDeleteCondition { get; set; } = true;
    public List<CopyObjectRequest> Copies { get; } = [];
    public ConcurrentQueue<CopyPartRequest> CopiedParts { get; } = new();
    public List<InitiateMultipartUploadRequest> Initiated { get; } = [];
    public List<CompleteMultipartUploadRequest> Completed { get; } = [];
    public List<PutObjectRequest> Puts { get; } = [];
    public List<DeleteObjectRequest> Deletes { get; } = [];
    public int Aborts;
    public int MaxConcurrentParts;
    private int _concurrentParts;
    /// <summary>Runs before a CopyObject or CopyPart is served.</summary>
    public Action<string>? BeforeCopy { get; set; }
    /// <summary>Runs after a CopyObject committed.</summary>
    public Action<string>? AfterCopy { get; set; }
    /// <summary>Makes a part number fail with a server error.</summary>
    public int? FailPart { get; set; }
    /// <summary>Commits a completion, then answers it with NoSuchUpload, as a retried completion whose first reply was lost.</summary>
    public bool LoseCompletionReply { get; set; }
    /// <summary>Runs after a recursive listing (no delimiter, more than one key asked for).</summary>
    public Action<string>? AfterList { get; set; }
    /// <summary>Runs after a completion committed, before its reply.</summary>
    public Action<string>? AfterComplete { get; set; }
    /// <summary>Makes deletes of keys matching this predicate fail with a server error.</summary>
    public Func<string, bool>? FailDelete { get; set; }

    public static string Md5(byte[] content) => Convert.ToHexStringLower(MD5.HashData(content));

    public FakeObject Put(string key, byte[] content, string? contentType = null)
    {
        var item = new FakeObject { Content = content, Size = content.Length, ETag = Md5(content), ContentType = contentType };
        Objects[key] = item;
        return item;
    }

    /// <summary>An object with a size but no bytes, for copies too large to hold.</summary>
    public FakeObject PutSynthetic(string key, long size)
    {
        var item = new FakeObject { Size = size, ETag = Convert.ToHexStringLower(MD5.HashData(BitConverter.GetBytes(size))) };
        Objects[key] = item;
        return item;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        var request = args.Length > 0 ? args[0] : null;
        return targetMethod.Name switch
        {
            nameof(IAmazonS3.GetObjectMetadataAsync) when request is GetObjectMetadataRequest head => Task.FromResult(Head(head.Key, head.VersionId)),
            nameof(IAmazonS3.GetObjectMetadataAsync) => Task.FromResult(Head((string)args[1]!, null)),
            nameof(IAmazonS3.PutObjectAsync) => PutAsync((PutObjectRequest)request!),
            nameof(IAmazonS3.CopyObjectAsync) => Task.FromResult(Copy((CopyObjectRequest)request!)),
            nameof(IAmazonS3.DeleteObjectAsync) => Task.FromResult(Delete((DeleteObjectRequest)request!)),
            nameof(IAmazonS3.InitiateMultipartUploadAsync) => Task.FromResult(Initiate((InitiateMultipartUploadRequest)request!)),
            nameof(IAmazonS3.CopyPartAsync) => CopyPartAsync((CopyPartRequest)request!),
            nameof(IAmazonS3.UploadPartAsync) => UploadPartAsync((UploadPartRequest)request!),
            nameof(IAmazonS3.CompleteMultipartUploadAsync) => Task.FromResult(Complete((CompleteMultipartUploadRequest)request!)),
            nameof(IAmazonS3.AbortMultipartUploadAsync) => Task.FromResult(Abort((AbortMultipartUploadRequest)request!)),
            nameof(IAmazonS3.GetObjectTaggingAsync) => Task.FromResult(new GetObjectTaggingResponse { Tagging = [.. Find(((GetObjectTaggingRequest)request!).Key).Tags] }),
            nameof(IAmazonS3.ListObjectsV2Async) => Task.FromResult(List((ListObjectsV2Request)request!)),
            nameof(IAmazonS3.GetObjectAsync) => Task.FromResult(Get((GetObjectRequest)request!)),
            nameof(IDisposable.Dispose) => null,
            _ => throw new NotSupportedException($"Unexpected S3 call: {targetMethod.Name}")
        };
    }

    private static AmazonS3Exception Error(HttpStatusCode status, string code) => new($"fake {code}") { StatusCode = status, ErrorCode = code };

    private FakeObject Find(string key) => Objects.TryGetValue(key, out var item) ? item : throw Error(HttpStatusCode.NotFound, "NotFound");

    private static bool Matches(string? condition, string etag) => condition is not null && condition.Trim('"') == etag;

    private GetObjectMetadataResponse Head(string key, string? versionId)
    {
        var item = Find(key);
        if (versionId is not null && versionId != item.VersionId) throw Error(HttpStatusCode.NotFound, "NoSuchVersion");
        var response = new GetObjectMetadataResponse
        {
            ETag = $"\"{item.ETag}\"",
            ContentLength = item.Size,
            VersionId = item.VersionId,
            StorageClass = item.StorageClass is null ? null : new S3StorageClass(item.StorageClass),
            ServerSideEncryptionMethod = item.Encryption is null ? null : new ServerSideEncryptionMethod(item.Encryption),
            ServerSideEncryptionKeyManagementServiceKeyId = item.KmsKeyId,
            ServerSideEncryptionCustomerMethod = item.CustomerEncryption is null ? null : new ServerSideEncryptionCustomerMethod(item.CustomerEncryption),
            TagsCount = item.Tags.Count,
            LastModified = DateTime.UtcNow
        };
        response.Headers.ContentType = item.ContentType;
        response.Headers.CacheControl = item.CacheControl;
        response.Headers.ContentEncoding = item.ContentEncoding;
        response.Headers.ContentDisposition = item.ContentDisposition;
        foreach (var (name, value) in item.Metadata) response.Metadata[name] = value;
        return response;
    }

    private async Task<PutObjectResponse> PutAsync(PutObjectRequest request)
    {
        lock (Puts) Puts.Add(request);
        using var buffer = new MemoryStream();
        if (request.InputStream is not null) await request.InputStream.CopyToAsync(buffer);
        else if (request.ContentBody is not null) buffer.Write(System.Text.Encoding.UTF8.GetBytes(request.ContentBody));
        if (request.IfNoneMatch == "*" && Objects.ContainsKey(request.Key)) throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        if (request.IfMatch is not null && (!Objects.TryGetValue(request.Key, out var current) || !Matches(request.IfMatch, current.ETag)))
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        var item = Put(request.Key, buffer.ToArray(), request.ContentType);
        foreach (var key in request.Metadata.Keys) item.Metadata[key] = request.Metadata[key];
        return new PutObjectResponse { ETag = $"\"{item.ETag}\"", VersionId = item.VersionId };
    }

    private CopyObjectResponse Copy(CopyObjectRequest request)
    {
        lock (Copies) Copies.Add(request);
        BeforeCopy?.Invoke(request.SourceKey);
        var source = Find(request.SourceKey);
        if (request.SourceVersionId is not null && request.SourceVersionId != source.VersionId) throw Error(HttpStatusCode.NotFound, "NoSuchVersion");
        if (request.ETagToMatch is not null && !Matches(request.ETagToMatch, source.ETag)) throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        if (EnforceCopyConditions)
        {
            if (request.IfNoneMatch == "*" && Objects.ContainsKey(request.DestinationKey)) throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            if (request.IfMatch is not null && (!Objects.TryGetValue(request.DestinationKey, out var current) || !Matches(request.IfMatch, current.ETag)))
                throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        }
        var copy = new FakeObject
        {
            Content = source.Content,
            Size = source.Size,
            ETag = source.ETag,
            ContentType = source.ContentType,
            CacheControl = source.CacheControl,
            ContentEncoding = source.ContentEncoding,
            ContentDisposition = source.ContentDisposition,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase),
            Tags = [.. source.Tags],
            // Like S3: the copy takes the storage class and encryption the request names, not the source's.
            StorageClass = request.StorageClass?.Value,
            Encryption = request.ServerSideEncryptionMethod?.Value,
            KmsKeyId = request.ServerSideEncryptionKeyManagementServiceKeyId
        };
        Objects[request.DestinationKey] = copy;
        AfterCopy?.Invoke(request.SourceKey);
        return new CopyObjectResponse { ETag = $"\"{copy.ETag}\"", VersionId = copy.VersionId };
    }

    private DeleteObjectResponse Delete(DeleteObjectRequest request)
    {
        lock (Deletes) Deletes.Add(request);
        if (FailDelete?.Invoke(request.Key) == true) throw Error(HttpStatusCode.InternalServerError, "InternalError");
        if (EnforceDeleteCondition && request.IfMatch is not null &&
            (!Objects.TryGetValue(request.Key, out var current) || !Matches(request.IfMatch, current.ETag)))
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        Objects.TryRemove(request.Key, out _);
        return new DeleteObjectResponse();
    }

    private InitiateMultipartUploadResponse Initiate(InitiateMultipartUploadRequest request)
    {
        lock (Initiated) Initiated.Add(request);
        var id = Guid.NewGuid().ToString("N");
        _uploads[id] = new Upload { Request = request };
        return new InitiateMultipartUploadResponse { UploadId = id };
    }

    private async Task<CopyPartResponse> CopyPartAsync(CopyPartRequest request)
    {
        var running = Interlocked.Increment(ref _concurrentParts);
        lock (CopiedParts)
            MaxConcurrentParts = Math.Max(MaxConcurrentParts, running);
        try
        {
            await Task.Yield();
            await Task.Delay(1);
            CopiedParts.Enqueue(request);
            BeforeCopy?.Invoke(request.SourceKey);
            if (request.PartNumber == FailPart) throw Error(HttpStatusCode.InternalServerError, "InternalError");
            var source = Find(request.SourceKey);
            if (request.ETagToMatch is { Count: > 0 } pins && !pins.Any(pin => Matches(pin, source.ETag)))
                throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
            var upload = _uploads.TryGetValue(request.UploadId, out var found) ? found : throw Error(HttpStatusCode.NotFound, "NoSuchUpload");
            var first = request.FirstByte!.Value;
            var last = request.LastByte!.Value;
            var bytes = source.Content?[(int)first..(int)(last + 1)];
            var etag = bytes is null ? Convert.ToHexStringLower(MD5.HashData(BitConverter.GetBytes(first * 31 + last))) : Md5(bytes);
            upload.Parts[request.PartNumber!.Value] = (bytes, last - first + 1, etag);
            return new CopyPartResponse { ETag = $"\"{etag}\"", PartNumber = request.PartNumber };
        }
        finally { Interlocked.Decrement(ref _concurrentParts); }
    }

    private async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request)
    {
        using var buffer = new MemoryStream();
        await request.InputStream.CopyToAsync(buffer);
        var upload = _uploads[request.UploadId];
        var bytes = buffer.ToArray();
        upload.Parts[request.PartNumber!.Value] = (bytes, bytes.Length, Md5(bytes));
        return new UploadPartResponse { ETag = $"\"{Md5(bytes)}\"", PartNumber = request.PartNumber };
    }

    private CompleteMultipartUploadResponse Complete(CompleteMultipartUploadRequest request)
    {
        lock (Completed) Completed.Add(request);
        if (!_uploads.TryRemove(request.UploadId, out var upload)) throw Error(HttpStatusCode.NotFound, "NoSuchUpload");
        if (request.IfNoneMatch == "*" && Objects.ContainsKey(request.Key)) throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        if (request.IfMatch is not null && (!Objects.TryGetValue(request.Key, out var current) || !Matches(request.IfMatch, current.ETag)))
            throw Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed");
        var parts = request.PartETags.OrderBy(part => part.PartNumber).Select(part => upload.Parts[part.PartNumber!.Value]).ToList();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (var part in parts) md5.AppendData(Convert.FromHexString(part.ETag));
        var init = upload.Request;
        var item = new FakeObject
        {
            Content = parts.All(part => part.Bytes is not null) ? [.. parts.SelectMany(part => part.Bytes!)] : null,
            Size = parts.Sum(part => part.Size),
            ETag = $"{Convert.ToHexStringLower(md5.GetHashAndReset())}-{parts.Count}",
            ContentType = init.ContentType,
            CacheControl = init.Headers.CacheControl,
            ContentEncoding = init.Headers.ContentEncoding,
            ContentDisposition = init.Headers.ContentDisposition,
            StorageClass = init.StorageClass?.Value,
            Encryption = init.ServerSideEncryptionMethod?.Value,
            KmsKeyId = init.ServerSideEncryptionKeyManagementServiceKeyId,
            Tags = init.TagSet is null ? [] : [.. init.TagSet]
        };
        foreach (var key in init.Metadata.Keys) item.Metadata[key] = init.Metadata[key];
        Objects[request.Key] = item;
        AfterComplete?.Invoke(request.Key);
        if (LoseCompletionReply) throw Error(HttpStatusCode.NotFound, "NoSuchUpload");
        return new CompleteMultipartUploadResponse { ETag = $"\"{item.ETag}\"", VersionId = item.VersionId };
    }

    private AbortMultipartUploadResponse Abort(AbortMultipartUploadRequest request)
    {
        Interlocked.Increment(ref Aborts);
        _uploads.TryRemove(request.UploadId, out _);
        return new AbortMultipartUploadResponse();
    }

    private ListObjectsV2Response List(ListObjectsV2Request request)
    {
        var prefix = request.Prefix ?? "";
        var keys = Objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToList();
        var response = new ListObjectsV2Response { S3Objects = [], CommonPrefixes = [] };
        var skip = int.TryParse(request.ContinuationToken, out var start) ? start : 0;
        var taken = 0;
        foreach (var key in keys.Skip(skip))
        {
            if (request.MaxKeys is > 0 and var max && taken == max)
            {
                response.NextContinuationToken = (skip + taken).ToString(System.Globalization.CultureInfo.InvariantCulture);
                response.IsTruncated = true;
                break;
            }
            taken++;
            var rest = key[prefix.Length..];
            if (request.Delimiter == "/" && rest.IndexOf('/') is var slash and >= 0)
            {
                var common = prefix + rest[..(slash + 1)];
                if (!response.CommonPrefixes.Contains(common)) response.CommonPrefixes.Add(common);
                continue;
            }
            var item = Objects[key];
            response.S3Objects.Add(new S3Object { Key = key, Size = item.Size, ETag = $"\"{item.ETag}\"", LastModified = DateTime.UtcNow });
        }
        response.KeyCount = response.S3Objects.Count + response.CommonPrefixes.Count;
        if (request.Delimiter is null && request.MaxKeys is not 1) AfterList?.Invoke(prefix);
        return response;
    }

    private GetObjectResponse Get(GetObjectRequest request)
    {
        var item = Find(request.Key);
        var bytes = item.Content ?? throw new InvalidOperationException("synthetic object");
        if (request.ByteRange is { } range)
            bytes = bytes[(int)range.Start..(int)Math.Min(range.End + 1, bytes.Length)];
        return new GetObjectResponse { ResponseStream = new MemoryStream(bytes), ETag = $"\"{item.ETag}\"", ContentLength = bytes.Length };
    }
}
