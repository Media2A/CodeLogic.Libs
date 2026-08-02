using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Authentication;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using FluentFTP;

namespace CL.Storage.Providers.Ftp;

/// <summary>Root-scoped storage over FTP, explicit FTPS, or implicit FTPS.</summary>
public sealed class FtpStorageBackend : IStorageBackend
{
    private static readonly StorageCapabilities FtpCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.RelayedCopy |
        StorageFeature.ServerSideMove |
        StorageFeature.RangeReads);

    private readonly Func<AsyncFtpClient> _clientFactory;
    private readonly RemotePathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;

    public FtpStorageBackend(
        string connectionId,
        Func<AsyncFtpClient> clientFactory,
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
    public StorageProvider Provider => StorageProvider.Ftp;
    public string Root => _paths.Root;
    public StorageCapabilities Capabilities => FtpCapabilities;

    public async Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0)
            return Result<StorageItem>.Success(DirectoryItem(string.Empty));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await client.GetObjectInfo(resolved.Value.RemotePath, true, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Get FTP item info")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<bool>.Failure(resolved.Error!);
        if (resolved.Value!.StoragePath.Length == 0) return Result<bool>.Success(true);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var exists = await client.FileExists(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false) ||
                await client.DirectoryExists(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(exists);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<bool>.Failure(Map(error, "Check FTP item existence")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public async Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StoragePage>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.DirectoryExists(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result<StoragePage>.Failure(StorageErrors.NotFound($"FTP directory '{resolved.Value.StoragePath}' was not found."));
            var listing = options.Recursive
                ? await client.GetListing(resolved.Value.RemotePath, FtpListOption.Auto | FtpListOption.Recursive, cancellationToken).ConfigureAwait(false)
                : await client.GetListing(resolved.Value.RemotePath, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
            var items = listing.Select(item =>
                {
                    var relative = _paths.FromRemotePath(item.FullName);
                    return relative is null || relative.Length == 0 ? null : ToItem(relative, item);
                })
                .Where(item => item is not null)
                .Cast<StorageItem>();
            return ProviderPaging.Create(items, options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List FTP directory")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public async Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            await client.CreateDirectory(resolved.Value!.RemotePath, true, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Create FTP directory")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "FTP does not expose portable atomic upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "FTP does not support portable user metadata."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        string? stagingPath = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var parent = RemotePathResolver.Parent(resolved.Value!.RemotePath);
            if (options.CreateParents)
                await client.CreateDirectory(parent, true, cancellationToken).ConfigureAwait(false);
            else if (!await client.DirectoryExists(parent, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The FTP destination parent directory was not found."));
            var existing = await client.GetObjectInfo(resolved.Value.RemotePath, true, cancellationToken).ConfigureAwait(false);
            if (existing?.Type == FtpObjectType.Directory)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP upload destination is a directory."));
            if (existing is not null && !options.Overwrite)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP destination already exists."));

            var staging = await AllocateTemporaryPathAsync(client, parent, "upload", cancellationToken).ConfigureAwait(false);
            if (staging.IsFailure)
                return Result<StorageItem>.Failure(staging.Error!);
            stagingPath = staging.Value!;
            var status = await client.UploadStream(
                source,
                stagingPath,
                FtpRemoteExists.Skip,
                createRemoteDir: false,
                progress: null,
                cancellationToken).ConfigureAwait(false);
            if (status == FtpStatus.Skipped)
                return Result<StorageItem>.Failure(StorageErrors.Conflict("The FTP destination already exists."));
            if (status != FtpStatus.Success)
                return Result<StorageItem>.Failure(StorageErrors.ProviderError("The FTP server did not accept the upload."));
            var committed = await CommitPathAsync(
                client,
                stagingPath,
                resolved.Value.RemotePath,
                FtpObjectType.File,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
            if (committed.IsFailure)
                return Result<StorageItem>.Failure(committed.Error!);
            stagingPath = null;
            var item = await client.GetObjectInfo(resolved.Value.RemotePath, true, cancellationToken).ConfigureAwait(false);
            return item is null
                ? Result<StorageItem>.Success(FileItem(resolved.Value.StoragePath, source.CanSeek ? source.Length : null))
                : Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, item));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Map(error, "Upload FTP file")); }
        finally
        {
            if (client is not null && stagingPath is not null)
                await TryDeletePathAsync(client, stagingPath).ConfigureAwait(false);
            if (client is not null)
                await ReleaseClientAsync(client).ConfigureAwait(false);
        }
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
        if (options.VersionId is not null)
            return Result<Stream>.Failure(StorageErrors.Unsupported(
                "FTP does not support version-specific downloads."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<Stream>.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        var ownershipTransferred = false;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await client.GetObjectInfo(resolved.Value!.RemotePath, true, cancellationToken).ConfigureAwait(false);
            if (item is null) return Result<Stream>.Failure(StorageErrors.NotFound($"FTP file '{resolved.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory) return Result<Stream>.Failure(StorageErrors.Conflict("An FTP directory cannot be downloaded as a file."));
            if (item.Size >= 0 && options.Offset > item.Size)
                return Result<Stream>.Failure(StorageErrors.InvalidPath("The range offset exceeds the FTP file length."));
            Stream stream = await client.OpenRead(
                resolved.Value.RemotePath,
                FtpDataType.Binary,
                options.Offset,
                checkIfFileExists: true,
                cancellationToken).ConfigureAwait(false);
            if (options.Length.HasValue)
                stream = new RangeReadStream(stream, item.Size >= 0
                    ? Math.Min(options.Length.Value, Math.Max(0, item.Size - options.Offset))
                    : options.Length.Value);
            var owned = new AsyncOwnedResourceStream(stream, () => ReleaseClientAsync(client));
            ownershipTransferred = true;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Map(error, "Download FTP file")); }
        finally { if (client is not null && !ownershipTransferred) await ReleaseClientAsync(client).ConfigureAwait(false); }
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
        if (options.Condition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "FTP does not expose portable atomic delete conditions."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result.Failure(resolved.Error!);
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await client.GetObjectInfo(resolved.Value!.RemotePath, true, cancellationToken).ConfigureAwait(false);
            if (item is null) return options.IgnoreMissing
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound($"FTP item '{resolved.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory)
            {
                if (!options.Recursive)
                {
                    var children = await client.GetListing(resolved.Value.RemotePath, FtpListOption.Auto, cancellationToken).ConfigureAwait(false);
                    if (children.Length > 0)
                        return Result.Failure(StorageErrors.Conflict("The FTP directory is not empty."));
                    await client.DeleteDirectory(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await client.DeleteDirectory(resolved.Value.RemotePath, FtpListOption.Recursive, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await client.DeleteFile(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            }
            return Result.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Delete FTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        var info = await GetInfoAsync(source.Value.StoragePath, cancellationToken).ConfigureAwait(false);
        if (info.IsFailure) return Result.Failure(info.Error!);
        if (info.Value!.ItemType == StorageItemType.Directory)
        {
            relationship = StorageTransferPath.ValidateDirectoryDestination(
                source.Value.StoragePath,
                destination.Value.StoragePath);
            if (relationship.IsFailure) return relationship;
            return Result.Failure(StorageErrors.Unsupported("FTP directory copy is not supported."));
        }
        var download = await DownloadAsync(source.Value.StoragePath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result.Failure(download.Error!);
        await using var stream = download.Value!;
        var upload = await UploadAsync(destination.Value.StoragePath, stream, new StorageUploadOptions
        {
            Overwrite = options.Overwrite,
            CreateParents = options.CreateParents
        }, cancellationToken).ConfigureAwait(false);
        return upload.IsSuccess ? Result.Success() : Result.Failure(upload.Error!);
    }

    public async Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var item = await client.GetObjectInfo(source.Value!.RemotePath, true, cancellationToken).ConfigureAwait(false);
            if (item is null) return Result.Failure(StorageErrors.NotFound($"FTP item '{source.Value.StoragePath}' was not found."));
            if (item.Type == FtpObjectType.Directory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value!.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            if (options.CreateParents)
                await client.CreateDirectory(RemotePathResolver.Parent(destination.Value!.RemotePath), true, cancellationToken).ConfigureAwait(false);
            else if (!await client.DirectoryExists(RemotePathResolver.Parent(destination.Value!.RemotePath), cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound("The FTP destination parent directory was not found."));
            return await CommitPathAsync(
                client,
                source.Value.RemotePath,
                destination.Value!.RemotePath,
                item.Type,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Move FTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public async Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await client.DirectoryExists(_paths.RemoteRoot, cancellationToken).ConfigureAwait(false)
                ? Result.Success()
                : Result.Failure(StorageErrors.NotFound("The configured FTP root was not found."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Map(error, "Check FTP health")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class
    {
        if (typeof(TClient) != typeof(AsyncFtpClient))
            return Result<NativeConnectionLease<TClient>>.Failure(StorageErrors.Unsupported($"FTP does not expose native type '{typeof(TClient).FullName}'."));
        AsyncFtpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var typed = (TClient)(object)client;
            client = null;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                typed,
                value => ReleaseClientAsync((AsyncFtpClient)(object)value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<NativeConnectionLease<TClient>>.Failure(Map(error, "Open native FTP connection")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<Result> CommitPathAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath,
        FtpObjectType sourceType,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destination = await client.GetObjectInfo(destinationPath, true, cancellationToken).ConfigureAwait(false);
        if (destination is null)
        {
            var moved = await MovePathAsync(
                client,
                sourcePath,
                destinationPath,
                sourceType,
                cancellationToken).ConfigureAwait(false);
            return moved
                ? Result.Success()
                : Result.Failure(StorageErrors.ProviderError("The FTP server did not commit the item."));
        }
        if (!overwrite)
            return Result.Failure(StorageErrors.Conflict("The FTP destination already exists."));

        var backup = await AllocateTemporaryPathAsync(
            client,
            RemotePathResolver.Parent(destinationPath),
            "backup",
            cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
            return Result.Failure(backup.Error!);
        var backupPath = backup.Value!;
        if (!await MovePathAsync(
            client,
            destinationPath,
            backupPath,
            destination.Type,
            cancellationToken).ConfigureAwait(false))
            return Result.Failure(StorageErrors.ProviderError(
                "The FTP server could not stage the existing destination for replacement."));

        try
        {
            if (!await MovePathAsync(
                client,
                sourcePath,
                destinationPath,
                sourceType,
                cancellationToken).ConfigureAwait(false))
            {
                var restored = await TryRestoreBackupAsync(
                    client,
                    backupPath,
                    destinationPath,
                    destination.Type).ConfigureAwait(false);
                return restored
                    ? Result.Failure(StorageErrors.ProviderError("The FTP server did not commit the replacement."))
                    : Result.Failure(StorageErrors.PartialFailure(
                        "The FTP replacement failed and its previous destination could not be restored.",
                        "destinationState=backup"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
            {
                _ = await DeleteBackupAsync(client, backupPath, destination.Type).ConfigureAwait(false);
                return Result.Success();
            }
            _ = await TryRestoreBackupAsync(client, backupPath, destinationPath, destination.Type).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath, destination.Type).ConfigureAwait(false);
            var restored = await TryRestoreBackupAsync(
                client,
                backupPath,
                destinationPath,
                destination.Type).ConfigureAwait(false);
            return restored
                ? Result.Failure(Map(error, "Commit FTP replacement"))
                : Result.Failure(StorageErrors.PartialFailure(
                    "The FTP replacement failed and its previous destination could not be restored.",
                    "destinationState=backup"));
        }

        return await DeleteBackupAsync(client, backupPath, destination.Type).ConfigureAwait(false);
    }

    private static Task<bool> MovePathAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath,
        FtpObjectType sourceType,
        CancellationToken cancellationToken) =>
        sourceType == FtpObjectType.Directory
            ? client.MoveDirectory(sourcePath, destinationPath, FtpRemoteExists.Skip, cancellationToken)
            : client.MoveFile(sourcePath, destinationPath, FtpRemoteExists.Skip, cancellationToken);

    private static async Task<Result<string>> AllocateTemporaryPathAsync(
        AsyncFtpClient client,
        string parent,
        string purpose,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = parent.TrimEnd('/') + $"/.cl-storage-{purpose}-{Guid.NewGuid():N}.tmp";
            if (await client.GetObjectInfo(candidate, true, cancellationToken).ConfigureAwait(false) is null)
                return Result<string>.Success(candidate);
        }
        return Result<string>.Failure(StorageErrors.Conflict(
            "Unable to allocate a unique FTP staging path."));
    }

    private static async Task<bool> IsCommitCompleteAsync(
        AsyncFtpClient client,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            var source = await client.GetObjectInfo(sourcePath, true, CancellationToken.None).ConfigureAwait(false);
            var destination = await client.GetObjectInfo(destinationPath, true, CancellationToken.None).ConfigureAwait(false);
            return source is null && destination is not null;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreBackupAsync(
        AsyncFtpClient client,
        string backupPath,
        string destinationPath,
        FtpObjectType backupType)
    {
        try
        {
            if (await client.GetObjectInfo(destinationPath, true, CancellationToken.None).ConfigureAwait(false) is not null)
                return false;
            return await MovePathAsync(
                client,
                backupPath,
                destinationPath,
                backupType,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Result> DeleteBackupAsync(
        AsyncFtpClient client,
        string backupPath,
        FtpObjectType backupType)
    {
        try
        {
            if (backupType == FtpObjectType.Directory)
                await client.DeleteDirectory(backupPath, FtpListOption.Recursive, CancellationToken.None).ConfigureAwait(false);
            else
                await client.DeleteFile(backupPath, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch
        {
            return Result.Failure(StorageErrors.PartialFailure(
                "The FTP destination committed, but its temporary backup could not be removed.",
                "destinationState=complete;backupState=present"));
        }
    }

    private static async Task TryDeletePathAsync(AsyncFtpClient client, string path)
    {
        try
        {
            var item = await client.GetObjectInfo(path, true, CancellationToken.None).ConfigureAwait(false);
            if (item is null)
                return;
            if (item.Type == FtpObjectType.Directory)
                await client.DeleteDirectory(path, FtpListOption.Recursive, CancellationToken.None).ConfigureAwait(false);
            else
                await client.DeleteFile(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup must not mask the primary upload result.
        }
    }

    private async Task<AsyncFtpClient> OpenClientAsync(CancellationToken cancellationToken)
    {
        var client = _clientFactory() ?? throw new InvalidOperationException("The FTP client factory returned null.");
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async ValueTask ReleaseClientAsync(AsyncFtpClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.Disconnect(CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        finally { client.Dispose(); }
    }

    private static StorageItem ToItem(string path, FtpListItem item) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = item.Type switch
        {
            FtpObjectType.Directory => StorageItemType.Directory,
            FtpObjectType.Link => StorageItemType.Link,
            _ => StorageItemType.File
        },
        Size = item.Type == FtpObjectType.File && item.Size >= 0 ? item.Size : null,
        LastModified = item.Modified == DateTime.MinValue ? null : new DateTimeOffset(item.Modified.ToUniversalTime())
    };

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static StorageItem FileItem(string path, long? size) => new()
    {
        Path = path,
        Name = NameOf(path),
        ItemType = StorageItemType.File,
        Size = size,
        LastModified = DateTimeOffset.UtcNow
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    private static Error Map(Exception exception, string operation) => exception switch
    {
        AuthenticationException => StorageErrors.Unauthorized($"{operation}: TLS authentication failed."),
        TimeoutException or TaskCanceledException => StorageErrors.Timeout($"{operation}: operation timed out."),
        SocketException => StorageErrors.Unavailable($"{operation}: FTP service is unavailable."),
        _ => StorageErrors.ProviderError($"{operation}: FTP provider failed.")
    };
}
