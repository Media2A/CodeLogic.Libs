using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Local;

/// <summary>Provides storage operations over a local path or mounted UNC root.</summary>
public sealed class LocalStorageBackend : IStorageBackend
{
    public const long DefaultMaxBufferedDownloadBytes = 67_108_864;
    private static readonly StorageCapabilities LocalCapabilities = new(
        Directories: true,
        NativeCopy: true,
        NativeMove: true,
        RangeReads: true,
        Metadata: false,
        ServerPagination: false);

    private readonly LocalPathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;

    public LocalStorageBackend(
        string connectionId,
        LocalConnectionConfig configuration,
        long maxBufferedDownloadBytes = DefaultMaxBufferedDownloadBytes)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        ArgumentNullException.ThrowIfNull(configuration);
        if (maxBufferedDownloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));

        ConnectionId = connectionId;
        _paths = new LocalPathResolver(configuration.RootPath, configuration.FollowLinks);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.Local;
    public string Root => _paths.Root;
    public StorageCapabilities Capabilities => LocalCapabilities;

    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<StorageItem>.Failure(resolved.Error!));
        try
        {
            var item = CreateItem(resolved.Value!.StoragePath, resolved.Value.FullPath);
            return Task.FromResult(item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"Storage item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(item));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.FromException(error, "Get item info")));
        }
    }

    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<bool>.Failure(resolved.Error!));
        try
        {
            _ = File.GetAttributes(resolved.Value!.FullPath);
            return Task.FromResult(Result<bool>.Success(true));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(Result<bool>.Success(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<bool>.Failure(StorageErrors.FromException(error, "Check item existence")));
        }
    }

    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result<StoragePage>.Failure(validation.Error!));
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<StoragePage>.Failure(resolved.Error!));

        try
        {
            if (!Directory.Exists(resolved.Value!.FullPath))
                return Task.FromResult(Result<StoragePage>.Failure(StorageErrors.NotFound($"Directory '{resolved.Value.StoragePath}' was not found.")));

            var items = EnumerateItems(resolved.Value, options.Recursive, cancellationToken)
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ToArray();
            var offsetResult = DecodeContinuationToken(options.ContinuationToken);
            if (offsetResult.IsFailure)
                return Task.FromResult(Result<StoragePage>.Failure(offsetResult.Error!));
            var offset = offsetResult.Value;
            if (offset > items.Length)
                return Task.FromResult(Result<StoragePage>.Failure(StorageErrors.InvalidPath("The continuation token is outside the listing.")));

            var pageItems = items.Skip(offset).Take(options.PageSize).ToArray();
            var nextOffset = offset + pageItems.Length;
            var token = nextOffset < items.Length ? EncodeContinuationToken(nextOffset) : null;
            return Task.FromResult(Result<StoragePage>.Success(new StoragePage(pageItems, token)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result<StoragePage>.Failure(StorageErrors.FromException(error, "List directory")));
        }
    }

    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error!));
        try
        {
            Directory.CreateDirectory(resolved.Value!.FullPath);
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Create directory")));
        }
    }

    public async Task<Result<StorageItem>> UploadAsync(
        string path,
        Stream source,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageItem>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Failure(StorageErrors.InvalidPath("A file path is required for upload."));

        try
        {
            var parent = Path.GetDirectoryName(resolved.Value.FullPath)!;
            if (options.CreateParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The destination parent directory was not found."));

            if (Directory.Exists(resolved.Value.FullPath))
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination is a directory."));

            var mode = options.Overwrite ? FileMode.Create : FileMode.CreateNew;
            await using (var destination = new FileStream(
                resolved.Value.FullPath,
                mode,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 81_920, cancellationToken).ConfigureAwait(false);
            }

            return await GetInfoAsync(resolved.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && File.Exists(resolved.Value.FullPath))
        {
            return Result<StorageItem>.Failure(StorageErrors.Conflict("The upload destination already exists."));
        }
        catch (Exception error)
        {
            return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Upload file"));
        }
    }

    public async Task<Result<StorageItem>> UploadBytesAsync(
        string path,
        byte[] content,
        StorageUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = new MemoryStream(content, writable: false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<Stream>> DownloadAsync(
        string path,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result<Stream>.Failure(validation.Error!));
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result<Stream>.Failure(resolved.Error!));

        FileStream? stream = null;
        try
        {
            if (Directory.Exists(resolved.Value!.FullPath))
                return Task.FromResult(Result<Stream>.Failure(StorageErrors.Conflict("A directory cannot be downloaded as a file.")));
            stream = new FileStream(
                resolved.Value.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (options.Offset > stream.Length)
            {
                stream.Dispose();
                return Task.FromResult(Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the file length.")));
            }
            stream.Position = options.Offset;
            Stream result = options.Length.HasValue
                ? new RangeReadStream(stream, Math.Min(options.Length.Value, stream.Length - options.Offset))
                : stream;
            return Task.FromResult(Result<Stream>.Success(result));
        }
        catch (OperationCanceledException) { stream?.Dispose(); throw; }
        catch (Exception error)
        {
            stream?.Dispose();
            return Task.FromResult(Result<Stream>.Failure(StorageErrors.FromException(error, "Download file")));
        }
    }

    public async Task<Result<byte[]>> DownloadBytesAsync(
        string path,
        StorageDownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<byte[]>.Failure(validation.Error!);
        var limit = options.MaxBufferedBytes ?? _maxBufferedDownloadBytes;

        var info = await GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure)
            return Result<byte[]>.Failure(info.Error!);
        var available = Math.Max(0, info.Value!.Size!.Value - options.Offset);
        var expected = options.Length.HasValue ? Math.Min(options.Length.Value, available) : available;
        if (expected > limit || expected > int.MaxValue)
            return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));

        var download = await DownloadAsync(path, options, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure)
            return Result<byte[]>.Failure(download.Error!);
        await using var source = download.Value!;
        using var destination = new MemoryStream((int)expected);
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length > limit - read)
                return Result<byte[]>.Failure(StorageErrors.TooLarge($"The download exceeds the {limit} byte buffering limit."));
            destination.Write(buffer, 0, read);
        }
        return Result<byte[]>.Success(destination.ToArray());
    }

    public Task<Result> DeleteAsync(
        string path,
        StorageDeleteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Task.FromResult(Result.Failure(validation.Error!));
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure)
            return Task.FromResult(Result.Failure(resolved.Error!));
        if (resolved.Value!.StoragePath.Length == 0)
            return Task.FromResult(Result.Failure(StorageErrors.InvalidPath("The configured root cannot be deleted.")));

        try
        {
            var attributes = File.GetAttributes(resolved.Value.FullPath);
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(resolved.Value.FullPath, options.Recursive);
            else
                File.Delete(resolved.Value.FullPath);
            return Task.FromResult(Result.Success());
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(options.IgnoreMissing
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound($"Storage item '{resolved.Value.StoragePath}' was not found.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Delete item")));
        }
    }

    public Task<Result> CopyAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var endpoints = ResolveTransfer(sourcePath, destinationPath, options);
        if (endpoints.IsFailure)
            return Task.FromResult(Result.Failure(endpoints.Error!));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAttributes = File.GetAttributes(endpoints.Value!.Source.FullPath);
            if ((sourceAttributes & FileAttributes.Directory) != 0)
                return Task.FromResult(Result.Failure(StorageErrors.Unsupported("Native local directory copy is not supported.")));
            if (Directory.Exists(endpoints.Value.Destination.FullPath))
                return Task.FromResult(Result.Failure(StorageErrors.Conflict("The copy destination is a directory.")));
            EnsureTransferParent(endpoints.Value.Destination.FullPath, options.CreateParents);
            File.Copy(endpoints.Value.Source.FullPath, endpoints.Value.Destination.FullPath, options.Overwrite);
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && DestinationExists(endpoints.Value!.Destination.FullPath))
        {
            return Task.FromResult(Result.Failure(StorageErrors.Conflict("The copy destination already exists.")));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Copy item")));
        }
    }

    public Task<Result> MoveAsync(
        string sourcePath,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var endpoints = ResolveTransfer(sourcePath, destinationPath, options);
        if (endpoints.IsFailure)
            return Task.FromResult(Result.Failure(endpoints.Error!));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceAttributes = File.GetAttributes(endpoints.Value!.Source.FullPath);
            if (!options.Overwrite && DestinationExists(endpoints.Value.Destination.FullPath))
                return Task.FromResult(Result.Failure(StorageErrors.Conflict("The move destination already exists.")));
            EnsureTransferParent(endpoints.Value.Destination.FullPath, options.CreateParents);
            if ((sourceAttributes & FileAttributes.Directory) != 0)
            {
                if (DestinationExists(endpoints.Value.Destination.FullPath))
                    return Task.FromResult(Result.Failure(StorageErrors.Conflict("An existing directory cannot be overwritten by native move.")));
                Directory.Move(endpoints.Value.Source.FullPath, endpoints.Value.Destination.FullPath);
            }
            else
            {
                File.Move(endpoints.Value.Source.FullPath, endpoints.Value.Destination.FullPath, options.Overwrite);
            }
            return Task.FromResult(Result.Success());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) when (!options.Overwrite && DestinationExists(endpoints.Value!.Destination.FullPath))
        {
            return Task.FromResult(Result.Failure(StorageErrors.Conflict("The move destination already exists.")));
        }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Move item")));
        }
    }

    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(Root);
            return Task.FromResult((attributes & FileAttributes.Directory) != 0
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable("The configured local root is not a directory.")));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            return Task.FromResult(Result.Failure(StorageErrors.FromException(error, "Check local storage health")));
        }
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default)
        where TClient : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<NativeConnectionLease<TClient>>.Failure(
            StorageErrors.Unsupported("The local provider does not use a native client session.")));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IEnumerable<StorageItem> EnumerateItems(ResolvedLocalPath root, bool recursive, CancellationToken cancellationToken)
    {
        var pending = new Stack<ResolvedLocalPath>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            GetDirectoryIdentity(root.FullPath)
        };
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var fullPath in Directory.EnumerateFileSystemEntries(directory.FullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeName = Path.GetFileName(fullPath);
                var storagePath = directory.StoragePath.Length == 0
                    ? relativeName
                    : directory.StoragePath + "/" + relativeName;
                var attributes = File.GetAttributes(fullPath);
                var item = CreateItem(storagePath, fullPath, attributes)!;
                yield return item;

                if (!recursive || (attributes & FileAttributes.Directory) == 0)
                    continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var resolvedLink = _paths.Resolve(storagePath);
                    if (resolvedLink.IsFailure)
                        continue;
                }
                if (!visited.Add(GetDirectoryIdentity(fullPath)))
                    continue;
                pending.Push(new ResolvedLocalPath(storagePath, fullPath));
            }
        }
    }

    private static string GetDirectoryIdentity(string fullPath)
    {
        var info = new DirectoryInfo(fullPath);
        var target = (info.Attributes & FileAttributes.ReparsePoint) != 0
            ? info.ResolveLinkTarget(returnFinalTarget: true)
            : null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target?.FullName ?? fullPath));
    }

    private static StorageItem? CreateItem(string storagePath, string fullPath, FileAttributes? knownAttributes = null)
    {
        FileAttributes attributes;
        try { attributes = knownAttributes ?? File.GetAttributes(fullPath); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }

        var type = (attributes & FileAttributes.ReparsePoint) != 0
            ? StorageItemType.Link
            : (attributes & FileAttributes.Directory) != 0
                ? StorageItemType.Directory
                : StorageItemType.File;
        var info = type == StorageItemType.Directory ? (FileSystemInfo)new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        return new StorageItem
        {
            Path = storagePath,
            Name = storagePath.Length == 0 ? info.Name : storagePath.Split('/')[^1],
            ItemType = type,
            Size = type == StorageItemType.File ? ((FileInfo)info).Length : null,
            LastModified = new DateTimeOffset(info.LastWriteTimeUtc),
            ContentType = type == StorageItemType.File ? GetContentType(info.Extension) : null,
            ETag = null
        };
    }

    private Result<TransferEndpoints> ResolveTransfer(string sourcePath, string destinationPath, StorageTransferOptions options)
    {
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<TransferEndpoints>.Failure(validation.Error!);
        var source = _paths.Resolve(sourcePath);
        if (source.IsFailure)
            return Result<TransferEndpoints>.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath);
        if (destination.IsFailure)
            return Result<TransferEndpoints>.Failure(destination.Error!);
        if (source.Value!.StoragePath.Length == 0 || destination.Value!.StoragePath.Length == 0)
            return Result<TransferEndpoints>.Failure(StorageErrors.InvalidPath("Transfer paths cannot be the configured root."));
        return Result<TransferEndpoints>.Success(new TransferEndpoints(source.Value, destination.Value));
    }

    private static void EnsureTransferParent(string destinationPath, bool createParents)
    {
        var parent = Path.GetDirectoryName(destinationPath)!;
        if (createParents)
            Directory.CreateDirectory(parent);
        else if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The destination parent directory does not exist.");
    }

    private static bool DestinationExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static string EncodeContinuationToken(int offset) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static Result<int> DecodeContinuationToken(string? token)
    {
        if (token is null)
            return Result<int>.Success(0);
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0
                ? Result<int>.Success(offset)
                : Result<int>.Failure(StorageErrors.InvalidPath("The continuation token is invalid."));
        }
        catch (FormatException)
        {
            return Result<int>.Failure(StorageErrors.InvalidPath("The continuation token is invalid."));
        }
    }

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private sealed record TransferEndpoints(ResolvedLocalPath Source, ResolvedLocalPath Destination);
}
