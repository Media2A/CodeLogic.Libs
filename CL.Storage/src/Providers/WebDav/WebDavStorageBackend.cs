using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;

namespace CL.Storage.Providers.WebDav;

/// <summary>Root-scoped storage over a WebDAV endpoint.</summary>
public sealed class WebDavStorageBackend : IStorageBackend
{
    private static readonly StorageCapabilities WebDavCapabilities = new(
        Directories: true,
        NativeCopy: true,
        NativeMove: true,
        RangeReads: true,
        Metadata: true,
        ServerPagination: false);

    private readonly IClient _client;
    private readonly RemotePathResolver _paths;
    private readonly string _basePath;
    private readonly bool _ownsClient;
    private readonly long _maxBufferedDownloadBytes;
    private int _disposed;

    public WebDavStorageBackend(
        string connectionId,
        IClient client,
        string? root = null,
        string? basePath = null,
        bool ownsClient = false,
        long maxBufferedDownloadBytes = 67_108_864)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(client);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _client = client;
        _paths = new RemotePathResolver(root);
        _basePath = NormalizeBasePath(basePath);
        _ownsClient = ownsClient;
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.WebDav;
    public string Root => _paths.Root;
    public StorageCapabilities Capabilities => WebDavCapabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        try
        {
            var item = await FindItemAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"WebDAV item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get WebDAV item info")); }
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
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StoragePage>.Failure(resolved.Error!);
        try
        {
            var items = await CollectListingAsync(resolved.Value!, options.Recursive, cancellationToken).ConfigureAwait(false);
            return ProviderPaging.Create(items, options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WebDAVException error) when (IsNotFound(error))
        {
            return Result<StoragePage>.Failure(StorageErrors.NotFound($"WebDAV directory '{resolved.Value!.StoragePath}' was not found."));
        }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List WebDAV directory")); }
    }

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        try
        {
            await EnsureDirectoryAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create WebDAV directory")); }
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        try
        {
            if (!options.Overwrite)
            {
                var existing = await GetInfoAsync(resolved.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
                if (existing.IsSuccess) return Result<StorageItem>.Failure(StorageErrors.Conflict("The WebDAV destination already exists."));
                if (existing.Error?.Code != StorageErrors.NotFoundCode) return Result<StorageItem>.Failure(existing.Error!);
            }
            var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
            if (options.CreateParents)
                await EnsureDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
            else
            {
                var parentStorage = _paths.FromRemotePath(parent) ?? string.Empty;
                var parentInfo = await GetInfoAsync(parentStorage, cancellationToken).ConfigureAwait(false);
                if (parentInfo.IsFailure) return Result<StorageItem>.Failure(parentInfo.Error!);
            }
            var success = await _client.Upload(
                EnsureTrailingSlash(parent),
                source,
                NameOf(resolved.Value.StoragePath),
                lockToken: null,
                cancellationToken).ConfigureAwait(false);
            if (!success) return Result<StorageItem>.Failure(StorageErrors.ProviderError("The WebDAV server did not accept the upload."));
            return await GetInfoAsync(resolved.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload WebDAV file")); }
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
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<Stream>.Failure(resolved.Error!);
        try
        {
            var info = await GetInfoAsync(resolved.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result<Stream>.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
                return Result<Stream>.Failure(StorageErrors.Conflict("A WebDAV directory cannot be downloaded as a file."));
            if (info.Value.Size.HasValue && options.Offset > info.Value.Size.Value)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the WebDAV file length."));
            Stream stream;
            if (options.Offset > 0 || options.Length.HasValue)
            {
                var available = info.Value.Size.HasValue ? info.Value.Size.Value - options.Offset : long.MaxValue;
                var length = options.Length.HasValue ? Math.Min(options.Length.Value, available) : available;
                if (length == long.MaxValue)
                {
                    stream = await _client.Download(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
                    await SkipAsync(stream, options.Offset, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (length == 0)
                        return Result<Stream>.Success(new MemoryStream([], writable: false));
                    stream = await _client.DownloadPartial(
                        resolved.Value.RemotePath,
                        options.Offset,
                        options.Offset + length - 1,
                        cancellationToken).ConfigureAwait(false);
                }
                if (options.Length.HasValue) stream = new RangeReadStream(stream, length);
            }
            else
            {
                stream = await _client.Download(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            }
            return Result<Stream>.Success(stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download WebDAV file")); }
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
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        try
        {
            var info = await GetInfoAsync(resolved.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return options.IgnoreMissing && info.Error?.Code == StorageErrors.NotFoundCode
                ? Result.Success()
                : Result.Failure(info.Error!);
            if (info.Value!.ItemType == StorageItemType.Directory)
            {
                if (!options.Recursive)
                {
                    var children = await _client.List(resolved.Value.RemotePath, depth: 1, cancellationToken).ConfigureAwait(false);
                    if (children.Any(item => FromHref(item.Href) is { } child && child != resolved.Value.StoragePath))
                        return Result.Failure(StorageErrors.Conflict("The WebDAV directory is not empty."));
                }
                await _client.DeleteFolder(resolved.Value.RemotePath, lockToken: null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _client.DeleteFile(resolved.Value.RemotePath, lockToken: null, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete WebDAV item")); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        try
        {
            var info = await GetInfoAsync(source.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (options.CreateParents) await EnsureDirectoryAsync(RemotePathResolver.Parent(destination.Value!.RemotePath), cancellationToken).ConfigureAwait(false);
            var success = info.Value!.ItemType == StorageItemType.Directory
                ? await _client.CopyFolder(source.Value.RemotePath, destination.Value!.RemotePath, options.Overwrite, null, cancellationToken).ConfigureAwait(false)
                : await _client.CopyFile(source.Value.RemotePath, destination.Value!.RemotePath, options.Overwrite, null, cancellationToken).ConfigureAwait(false);
            return success ? Result.Success() : Result.Failure(options.Overwrite
                ? StorageErrors.ProviderError("The WebDAV server did not copy the item.")
                : StorageErrors.Conflict("The WebDAV destination already exists."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Copy WebDAV item")); }
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        try
        {
            var info = await GetInfoAsync(source.Value!.StoragePath, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure) return Result.Failure(info.Error!);
            if (options.CreateParents) await EnsureDirectoryAsync(RemotePathResolver.Parent(destination.Value!.RemotePath), cancellationToken).ConfigureAwait(false);
            var success = info.Value!.ItemType == StorageItemType.Directory
                ? await _client.MoveFolder(source.Value.RemotePath, destination.Value!.RemotePath, options.Overwrite, null, null, cancellationToken).ConfigureAwait(false)
                : await _client.MoveFile(source.Value.RemotePath, destination.Value!.RemotePath, options.Overwrite, null, null, cancellationToken).ConfigureAwait(false);
            return success ? Result.Success() : Result.Failure(options.Overwrite
                ? StorageErrors.ProviderError("The WebDAV server did not move the item.")
                : StorageErrors.Conflict("The WebDAV destination already exists."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Move WebDAV item")); }
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var options = await _client.GetServerOptions(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false);
            return options.IsWebDavServer
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable("The endpoint did not identify itself as a WebDAV server."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check WebDAV health")); }
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
            return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"WebDAV does not expose native type '{typeof(TClient).FullName}'.")));
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Success(
            new NativeConnectionLease<TClient>(typed, _ => ValueTask.CompletedTask)));
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient && Interlocked.Exchange(ref _disposed, 1) == 0) _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<Item?> FindItemAsync(ResolvedRemotePath resolved, CancellationToken cancellationToken)
    {
        var parent = RemotePathResolver.Parent(resolved.RemotePath);
        var items = await _client.List(parent, depth: 1, cancellationToken).ConfigureAwait(false);
        return items.FirstOrDefault(item => string.Equals(FromHref(item.Href), resolved.StoragePath, StringComparison.Ordinal));
    }

    private async Task<List<StorageItem>> CollectListingAsync(ResolvedRemotePath root, bool recursive, CancellationToken cancellationToken)
    {
        var result = new List<StorageItem>();
        var pending = new Queue<string>();
        pending.Enqueue(root.RemotePath);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            var items = await _client.List(directory, depth: 1, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                var relative = FromHref(item.Href);
                if (relative is null || relative.Length == 0 || relative == _paths.FromRemotePath(directory)) continue;
                result.Add(ToItem(relative, item));
                if (recursive && item.IsCollection) pending.Enqueue(ToRemotePath(relative));
            }
        }
        return result.GroupBy(item => item.Path, StringComparer.Ordinal).Select(group => group.First()).ToList();
    }

    private async Task EnsureDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        var current = string.Empty;
        foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            var storagePath = _paths.FromRemotePath(current);
            if (storagePath is null) continue;
            var exists = await ExistsAsync(storagePath, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) throw new WebDAVException(exists.Error!.Message);
            if (exists.Value) continue;
            var parent = RemotePathResolver.Parent(current);
            var created = await _client.CreateDir(EnsureTrailingSlash(parent), segment, cancellationToken).ConfigureAwait(false);
            if (!created) throw new WebDAVException($"Could not create WebDAV directory '{current}'.");
        }
    }

    private string? FromHref(string href)
    {
        var value = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
        value = Uri.UnescapeDataString(value).Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        if (_basePath != "/" && value.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            value = "/" + value[_basePath.Length..].TrimStart('/');
        return _paths.FromRemotePath(value.TrimEnd('/'));
    }

    private string ToRemotePath(string storagePath) => _paths.Resolve(storagePath).Value!.RemotePath;

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81_920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count -= read;
        }
    }

    private static StorageItem ToItem(string path, Item item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = item.IsCollection ? StorageItemType.Directory : StorageItemType.File,
        Size = item.IsCollection ? null : item.ContentLength,
        LastModified = item.LastModified.HasValue ? new DateTimeOffset(item.LastModified.Value.ToUniversalTime()) : null,
        ContentType = item.ContentType,
        ETag = item.Etag,
        Metadata = item.FoundProperties?.ToDictionary(pair => $"{pair.Key.Namespace}{pair.Key.LocalName}", pair => pair.Value, StringComparer.Ordinal) ??
            new Dictionary<string, string>()
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NormalizeBasePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path.Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        if (!value.EndsWith('/')) value += "/";
        return value;
    }

    private static string EnsureTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";
    private static string NameOf(string path) => path.Split('/')[^1];
    private static bool IsNotFound(WebDAVException error) => error.ErrorCode == 404;

    private static Error Map(Exception exception, string operation)
    {
        if (exception is WebDAVException webDav)
        {
            return webDav.ErrorCode switch
            {
                401 or 403 => StorageErrors.Unauthorized($"{operation}: access was denied."),
                404 => StorageErrors.NotFound($"{operation}: item was not found."),
                408 or 504 => StorageErrors.Timeout($"{operation}: operation timed out."),
                409 or 412 or 423 => StorageErrors.Conflict($"{operation}: WebDAV conflict."),
                >= 500 => StorageErrors.Unavailable($"{operation}: WebDAV service is unavailable."),
                _ => StorageErrors.ProviderError($"{operation}: WebDAV request failed.", webDav.Message)
            };
        }
        if (exception is TimeoutException or TaskCanceledException)
            return StorageErrors.Timeout($"{operation}: operation timed out.");
        if (exception is HttpRequestException)
            return StorageErrors.Unavailable($"{operation}: WebDAV service is unavailable.");
        return StorageErrors.ProviderError($"{operation}: WebDAV provider failed.", exception.Message);
    }
}
