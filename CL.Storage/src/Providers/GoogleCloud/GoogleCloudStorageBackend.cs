using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace CL.Storage.Providers.GoogleCloud;

/// <summary>Root-scoped storage over Google Cloud Storage.</summary>
public sealed class GoogleCloudStorageBackend : IStorageBackend
{
    private static readonly StorageCapabilities GcsCapabilities = new(true, true, true, true, true, false);
    private readonly StorageClient _client;
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
    public StorageCapabilities Capabilities => GcsCapabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
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
        var listingPath = normalized.Value!;
        var listingPrefix = DirectoryPrefix(listingPath);
        try
        {
            var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
            await foreach (var item in _client.ListObjectsAsync(_bucket, listingPrefix, new ListObjectsOptions { PageSize = 1000 })
                .WithCancellation(cancellationToken).ConfigureAwait(false))
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
            return ProviderPaging.Create(items.Values, options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List Google Cloud objects")); }
    }

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result.Failure(normalized.Error!);
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
        var normalized = NormalizeRequired(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var normalizedPath = normalized.Value!;
        try
        {
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
                IfGenerationMatch = options.Overwrite ? null : 0
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
        var tempPath = Path.GetTempFileName();
        FileStream? stream = null;
        var ownershipTransferred = false;
        try
        {
            stream = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            DownloadObjectOptions? downloadOptions = null;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var end = options.Length.HasValue ? options.Offset + options.Length.Value - 1 : (long?)null;
                downloadOptions = new DownloadObjectOptions { Range = new RangeHeaderValue(options.Offset, end) };
            }
            await _client.DownloadObjectAsync(
                _bucket,
                ToKey(normalized.Value!),
                stream,
                downloadOptions,
                cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            var result = stream;
            ownershipTransferred = true;
            stream = null;
            return Result<Stream>.Success(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download Google Cloud object")); }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            if (!ownershipTransferred && File.Exists(tempPath)) File.Delete(tempPath);
        }
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
                await _client.DeleteObjectAsync(_bucket, ToKey(normalized.Value!), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete Google Cloud object")); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var source = NormalizeRequired(sourcePath);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = NormalizeRequired(destinationPath);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        try
        {
            var info = await GetInfoAsync(source.Value!, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                var sourcePrefix = DirectoryPrefix(source.Value!);
                var destinationPrefix = DirectoryPrefix(destination.Value!);
                await foreach (var item in _client.ListObjectsAsync(_bucket, sourcePrefix)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await CopyObjectAsync(item.Name, destinationPrefix + item.Name[sourcePrefix.Length..], options.Overwrite, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await CopyObjectAsync(ToKey(source.Value!), ToKey(destination.Value!), options.Overwrite, cancellationToken).ConfigureAwait(false);
            }
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
        return await DeleteAsync(sourcePath, new StorageDeleteOptions { Recursive = true }, cancellationToken).ConfigureAwait(false);
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

    private static StorageItem ToItem(string path, GcsObject item) => new()
    {
        Path = path.TrimEnd('/'),
        Name = NameOf(path.TrimEnd('/')),
        ItemType = path.EndsWith('/') ? StorageItemType.Directory : StorageItemType.File,
        Size = path.EndsWith('/') || !item.Size.HasValue ? null : checked((long)item.Size.Value),
        LastModified = item.UpdatedDateTimeOffset,
        ContentType = item.ContentType,
        ETag = item.ETag,
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
                _ => StorageErrors.ProviderError($"{operation}: Google Cloud request failed.", google.Message)
            };
        }
        if (exception is TimeoutException or TaskCanceledException) return StorageErrors.Timeout($"{operation}: operation timed out.");
        return StorageErrors.ProviderError($"{operation}: Google Cloud Storage provider failed.", exception.Message);
    }
}
