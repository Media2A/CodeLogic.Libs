using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace CL.Storage.Providers.Sftp;

/// <summary>Root-scoped storage over SSH File Transfer Protocol.</summary>
public sealed class SftpStorageBackend : IStorageBackend
{
    private static readonly StorageCapabilities SftpCapabilities = new(
        Directories: true,
        NativeCopy: false,
        NativeMove: true,
        RangeReads: true,
        Metadata: false,
        ServerPagination: false);

    private readonly Func<SftpClient> _clientFactory;
    private readonly RemotePathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;

    public SftpStorageBackend(
        string connectionId,
        Func<SftpClient> clientFactory,
        string? root = null,
        long maxBufferedDownloadBytes = 67_108_864)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(clientFactory);
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _clientFactory = clientFactory;
        _paths = new RemotePathResolver(root);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.Sftp;
    public string Root => _paths.Root;
    public StorageCapabilities Capabilities => SftpCapabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound($"SFTP item '{resolved.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, attributes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get SFTP item info")); }
        finally { client?.Dispose(); }
    }

    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<bool>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<bool>.Failure(Map(error, "Check SFTP item existence")); }
        finally { client?.Dispose(); }
    }

    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StoragePage>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result<StoragePage>.Failure(StorageErrors.NotFound($"SFTP directory '{resolved.Value.StoragePath}' was not found."));
            var rootAttributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (!rootAttributes.IsDirectory)
                return Result<StoragePage>.Failure(StorageErrors.Conflict("The SFTP listing path is not a directory."));
            var items = await CollectListingAsync(client, resolved.Value.RemotePath, options.Recursive, cancellationToken).ConfigureAwait(false);
            return ProviderPaging.Create(items, options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List SFTP directory")); }
        finally { client?.Dispose(); }
    }

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            await EnsureDirectoryAsync(client, resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create SFTP directory")); }
        finally { client?.Dispose(); }
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
            if (options.CreateParents)
                await EnsureDirectoryAsync(client, parent, cancellationToken).ConfigureAwait(false);
            else if (!await client.ExistsAsync(parent, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The SFTP destination parent directory was not found."));
            if (await client.ExistsAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false))
            {
                var existing = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
                if (existing.IsDirectory)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("The SFTP upload destination is a directory."));
                if (!options.Overwrite)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("The SFTP destination already exists."));
            }
            await client.UploadFileAsync(source, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, attributes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload SFTP file")); }
        finally { client?.Dispose(); }
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
        SftpClient? client = null;
        var ownershipTransferred = false;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result<Stream>.Failure(StorageErrors.NotFound($"SFTP file '{resolved.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (attributes.IsDirectory)
                return Result<Stream>.Failure(StorageErrors.Conflict("An SFTP directory cannot be downloaded as a file."));
            if (options.Offset > attributes.Size)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the SFTP file length."));
            Stream stream = client.OpenRead(resolved.Value.RemotePath);
            stream.Position = options.Offset;
            if (options.Length.HasValue)
                stream = new RangeReadStream(stream, Math.Min(options.Length.Value, attributes.Size - options.Offset));
            var owned = new AsyncOwnedResourceStream(stream, () => ReleaseClientAsync(client));
            ownershipTransferred = true;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download SFTP file")); }
        finally { if (client is not null && !ownershipTransferred) client.Dispose(); }
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
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return options.IgnoreMissing
                    ? Result.Success()
                    : Result.Failure(StorageErrors.NotFound($"SFTP item '{resolved.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (attributes.IsDirectory && !options.Recursive && await HasChildrenAsync(client, resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.Conflict("The SFTP directory is not empty."));
            await DeleteRemoteAsync(client, resolved.Value.RemotePath, attributes.IsDirectory, options.Recursive, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete SFTP item")); }
        finally { client?.Dispose(); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var info = await GetInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure) return Result.Failure(info.Error!);
        if (info.Value!.ItemType == StorageItemType.Directory)
            return Result.Failure(StorageErrors.Unsupported("SFTP directory copy is not supported."));
        var download = await DownloadAsync(sourcePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result.Failure(download.Error!);
        await using var stream = download.Value!;
        var upload = await UploadAsync(destinationPath, stream, new StorageUploadOptions
        {
            Overwrite = options.Overwrite,
            CreateParents = options.CreateParents
        }, cancellationToken).ConfigureAwait(false);
        return upload.IsSuccess ? Result.Success() : Result.Failure(upload.Error!);
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(source.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound($"SFTP item '{source.Value.StoragePath}' was not found."));
            var parent = RemotePathResolver.Parent(destination.Value!.RemotePath);
            if (options.CreateParents)
                await EnsureDirectoryAsync(client, parent, cancellationToken).ConfigureAwait(false);
            else if (!await client.ExistsAsync(parent, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound("The SFTP destination parent directory was not found."));
            if (await client.ExistsAsync(destination.Value.RemotePath, cancellationToken).ConfigureAwait(false))
            {
                if (!options.Overwrite)
                    return Result.Failure(StorageErrors.Conflict("The SFTP destination already exists."));
                var attributes = await client.GetAttributesAsync(destination.Value.RemotePath, cancellationToken).ConfigureAwait(false);
                await DeleteRemoteAsync(client, destination.Value.RemotePath, attributes.IsDirectory, recursive: true, cancellationToken).ConfigureAwait(false);
            }
            await client.RenameFileAsync(source.Value.RemotePath, destination.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Move SFTP item")); }
        finally { client?.Dispose(); }
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await client.ExistsAsync(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false)
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The configured SFTP root was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check SFTP health")); }
        finally { client?.Dispose(); }
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        if (typeof(TClient) != typeof(SftpClient))
            return Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"SFTP does not expose native type '{typeof(TClient).FullName}'."));
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var typed = (TClient)(object)client;
            client = null;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                typed,
                value => ReleaseClientAsync((SftpClient)(object)value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<NativeConnectionLease<TClient>>.Failure(Map(error, "Open native SFTP connection")); }
        finally { client?.Dispose(); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<SftpClient> OpenClientAsync(CancellationToken cancellationToken)
    {
        var client = _clientFactory() ?? throw new InvalidOperationException("The SFTP client factory returned null.");
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static ValueTask ReleaseClientAsync(SftpClient client)
    {
        try { if (client.IsConnected) client.Disconnect(); }
        catch { }
        finally { client.Dispose(); }
        return ValueTask.CompletedTask;
    }

    private async Task<List<StorageItem>> CollectListingAsync(SftpClient client, string remoteRoot, bool recursive, CancellationToken cancellationToken)
    {
        var items = new List<StorageItem>();
        var pending = new Queue<string>();
        pending.Enqueue(remoteRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            await foreach (var item in client.ListDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                if (item.Name is "." or "..") continue;
                var relative = _paths.FromRemotePath(item.FullName);
                if (relative is null || relative.Length == 0) continue;
                items.Add(ToItem(relative, item));
                if (recursive && item.IsDirectory && !item.IsSymbolicLink)
                    pending.Enqueue(item.FullName);
            }
        }
        return items;
    }

    private static async Task EnsureDirectoryAsync(SftpClient client, string remotePath, CancellationToken cancellationToken)
    {
        var current = string.Empty;
        foreach (var segment in remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!await client.ExistsAsync(current, cancellationToken).ConfigureAwait(false))
                await client.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> HasChildrenAsync(SftpClient client, string remotePath, CancellationToken cancellationToken)
    {
        await foreach (var item in client.ListDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
            if (item.Name is not "." and not "..") return true;
        return false;
    }

    private static async Task DeleteRemoteAsync(SftpClient client, string remotePath, bool directory, bool recursive, CancellationToken cancellationToken)
    {
        if (!directory)
        {
            await client.DeleteFileAsync(remotePath, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (recursive)
        {
            var children = new List<ISftpFile>();
            await foreach (var item in client.ListDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false))
                if (item.Name is not "." and not "..") children.Add(item);
            foreach (var child in children)
                await DeleteRemoteAsync(client, child.FullName, child.IsDirectory && !child.IsSymbolicLink, recursive: true, cancellationToken).ConfigureAwait(false);
        }
        await client.DeleteDirectoryAsync(remotePath, cancellationToken).ConfigureAwait(false);
    }

    private static StorageItem ToItem(string path, SftpFileAttributes attributes) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = attributes.IsSymbolicLink ? StorageItemType.Link : attributes.IsDirectory ? StorageItemType.Directory : StorageItemType.File,
        Size = attributes.IsRegularFile ? attributes.Size : null,
        LastModified = new DateTimeOffset(attributes.LastWriteTimeUtc)
    };

    private static StorageItem ToItem(string path, ISftpFile item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = item.IsSymbolicLink ? StorageItemType.Link : item.IsDirectory ? StorageItemType.Directory : StorageItemType.File,
        Size = item.IsRegularFile ? item.Length : null,
        LastModified = new DateTimeOffset(item.LastWriteTimeUtc)
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error Map(Exception exception, string operation) => exception switch
    {
        SftpPathNotFoundException => StorageErrors.NotFound($"{operation}: item was not found."),
        SftpPermissionDeniedException or SshAuthenticationException => StorageErrors.Unauthorized($"{operation}: access was denied."),
        SshOperationTimeoutException or TimeoutException or TaskCanceledException => StorageErrors.Timeout($"{operation}: operation timed out."),
        SshConnectionException => StorageErrors.Unavailable($"{operation}: SFTP service is unavailable."),
        _ => StorageErrors.ProviderError($"{operation}: SFTP provider failed.", exception.Message)
    };
}
