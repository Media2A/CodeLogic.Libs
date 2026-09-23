using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Sftp;

namespace CL.Storage.Providers.Sftp;

/// <summary>Root-scoped storage over SSH File Transfer Protocol.</summary>
public sealed class SftpStorageBackend : IStorageBackend, IStorageAttributeService, IStorageAppendService, IStorageCommandService, IStorageSpaceService, IStorageDiagnosticsSource, IStorageRestoringReplace
{
    private static readonly StorageCapabilities SftpCapabilities = new(
        StorageFeature.PhysicalDirectories |
        StorageFeature.FileCopy |
        StorageFeature.FileMove |
        StorageFeature.DirectoryMove |
        StorageFeature.RelayedCopy |
        StorageFeature.ServerSideMove |
        // A rename on the server (RNFR/RNTO, SFTP rename, WebDAV MOVE) moves a whole tree in one step,
        // so folder moves no longer fall back to copy-then-delete through the client.
        StorageFeature.AtomicMove |
        StorageFeature.RangeReads |
        StorageFeature.SpaceInfo |
        StorageFeature.Append |
        StorageFeature.Links |
        StorageFeature.Permissions |
        StorageFeature.Ownership |
        StorageFeature.SetTimestamps |
        StorageFeature.CreateLinks);

    private readonly ProviderClientPool<SftpClient> _clients;
    private readonly IPoolHandle<SftpClient> _pool;
    private readonly Action? _sessionOpened;
    private readonly RemotePathResolver _paths;
    private readonly long _maxBufferedDownloadBytes;
    private readonly ProviderRetryPolicy _retry;
    private readonly IStorageConnectionObserver? _observer;

    /// <summary>Initializes a backend that leases SFTP clients from a factory.</summary>
    /// <param name="connectionId">Unique connection ID exposed by the storage registry.</param>
    /// <param name="clientFactory">Factory that creates configured SFTP clients.</param>
    /// <param name="root">Optional remote directory mounted as the connection root.</param>
    /// <param name="maxBufferedDownloadBytes">Maximum size accepted by buffered download helpers.</param>
    /// <param name="session">Session pool limits; defaults when omitted.</param>
    /// <param name="retry">Transient-failure retry policy; defaults when omitted.</param>
    public SftpStorageBackend(
        string connectionId,
        Func<SftpClient> clientFactory,
        string? root = null,
        long maxBufferedDownloadBytes = 67_108_864,
        StorageSessionConfig? session = null,
        StorageRetryConfig? retry = null)
        : this(connectionId, clientFactory, root, maxBufferedDownloadBytes, session, retry, observer: null)
    {
    }

    internal SftpStorageBackend(
        string connectionId,
        Func<SftpClient> clientFactory,
        string? root,
        long maxBufferedDownloadBytes,
        StorageSessionConfig? session,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer)
        : this(connectionId, new OwnedPool<SftpClient>(CreatePool(clientFactory, session)), root, maxBufferedDownloadBytes, retry, observer)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
    }

    internal SftpStorageBackend(
        string connectionId,
        IPoolHandle<SftpClient> pool,
        string? root,
        long maxBufferedDownloadBytes,
        StorageRetryConfig? retry,
        IStorageConnectionObserver? observer)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) throw new ArgumentException("Connection ID is required.", nameof(connectionId));
        if (maxBufferedDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedDownloadBytes));
        ConnectionId = connectionId;
        _observer = observer;
        _pool = pool;
        _clients = pool.Pool;
        if (observer is not null)
        {
            // Reported per rent, not through the pool's event: a shared pool serves other registrations too.
            _sessionOpened = () => observer.SessionOpened(connectionId, StorageProvider.Sftp);
        }
        _retry = new ProviderRetryPolicy(retry, connectionId, StorageProvider.Sftp, observer);
        _paths = new RemotePathResolver(root);
        _maxBufferedDownloadBytes = maxBufferedDownloadBytes;
    }

    internal ProviderPoolStats PoolStats => _clients.Stats;

    /// <summary>Creates the session pool for a client factory; shared pools are created once per settings.</summary>
    internal static ProviderClientPool<SftpClient> CreatePool(Func<SftpClient> clientFactory, StorageSessionConfig? session) =>
        new(
            clientFactory,
            SftpHostKeyTracker.ConnectAsync,
            static client => client.IsConnected,
            DestroyClientAsync,
            ProviderPoolOptions.From(session),
            ProbeAsync);

    /// <summary>Identifies the listing snapshots continuation tokens refer to; settings-based so tokens outlive a registration.</summary>
    internal string? ListingScope { get; init; }

    /// <summary>Records the host key each session is offered; set by the factory.</summary>
    internal ServerIdentityRecorder? Identity { get; init; }

    StorageServerIdentity? IStorageDiagnosticsSource.PresentedIdentity => Identity?.Last;

    StorageSessionPoolStats? IStorageDiagnosticsSource.PoolStats => StorageEndpoints.ToPublic(_clients.Stats);

    async Task<Result<StorageServerDetails>> IStorageDiagnosticsSource.GetServerDetailsAsync(CancellationToken cancellationToken)
    {
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var info = client.ConnectionInfo;
            var negotiated = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) negotiated[key] = value;
            }
            Add("kex", info.CurrentKeyExchangeAlgorithm);
            Add("hostKey", info.CurrentHostKeyAlgorithm);
            Add("cipher", info.CurrentServerEncryption);
            Add("mac", info.CurrentServerHmacAlgorithm);
            Add("compression", info.CurrentServerCompressionAlgorithm);
            Add("sftp", client.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Result<StorageServerDetails>.Success(new StorageServerDetails(
                string.IsNullOrWhiteSpace(info.ServerVersion) ? null : info.ServerVersion,
                SoftwareOf(info.ServerVersion),
                [],
                negotiated));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageServerDetails>.Failure(Fail(client, error, "Read SFTP server details")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>Reads the software part of an SSH version string, such as <c>OpenSSH_9.6</c> from <c>SSH-2.0-OpenSSH_9.6 Ubuntu</c>.</summary>
    internal static string? SoftwareOf(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) return null;
        var parts = serverVersion.Split('-', 3);
        if (parts.Length < 3) return null;
        var software = parts[2].Split(' ', 2)[0];
        return software.Length == 0 ? null : software;
    }

    /// <inheritdoc />
    public string ConnectionId { get; }
    /// <inheritdoc />
    public StorageProvider Provider => StorageProvider.Sftp;
    /// <inheritdoc />
    public string Root => _paths.Root;
    /// <inheritdoc />
    public StorageCapabilities Capabilities => CommandClientFactory is not null
        ? new StorageCapabilities(SftpCapabilities.Features | StorageFeature.RawCommands, SftpCapabilities.Limits)
        : SftpCapabilities;

    /// <summary>Creates SSH shell sessions for raw commands; set only when the connection enables <c>AllowRawCommands</c>.</summary>
    internal Func<SshClient>? CommandClientFactory { get; init; }

    /// <inheritdoc />
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Get SFTP item info", RetryKind.Idempotent, (_, token) => GetInfoCoreAsync(path, token), cancellationToken);

    private async Task<Result<StorageItem>> GetInfoCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Get SFTP item info")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check SFTP item existence", RetryKind.Idempotent, (_, token) => ExistsCoreAsync(path, token), cancellationToken);

    private async Task<Result<bool>> ExistsCoreAsync(string path, CancellationToken cancellationToken)
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
        catch (Exception error) { return Result<bool>.Failure(Fail(client, error, "Check SFTP item existence")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("List SFTP directory", RetryKind.Idempotent, (_, token) => ListCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<StoragePage>> ListCoreAsync(string path, StorageListOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageListOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StoragePage>.Failure(validation.Error!);
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StoragePage>.Failure(resolved.Error!);
        var target = resolved.Value!;
        try
        {
            // The walk runs only when no snapshot is cached for this listing pass, so pages after
            // the first cost neither an SSH handshake nor a directory traversal.
            return await ProviderPaging.CreateAsync(
                ProviderPaging.Scope(ListingScope ?? ConnectionId, target.StoragePath, options.Recursive),
                options,
                async token =>
                {
                    SftpClient? listClient = null;
                    try
                    {
                        listClient = await OpenClientAsync(token).ConfigureAwait(false);
                        if (!await listClient.ExistsAsync(target.RemotePath, token).ConfigureAwait(false))
                            return Result<IEnumerable<StorageItem>>.Failure(
                                StorageErrors.NotFound($"SFTP directory '{target.StoragePath}' was not found."));
                        var rootAttributes = await listClient.GetAttributesAsync(target.RemotePath, token).ConfigureAwait(false);
                        if (!rootAttributes.IsDirectory)
                            return Result<IEnumerable<StorageItem>>.Failure(
                                StorageErrors.Conflict("The SFTP listing path is not a directory."));
                        var items = await CollectListingAsync(listClient, target.RemotePath, options.Recursive, token).ConfigureAwait(false);
                        return Result<IEnumerable<StorageItem>>.Success(items);
                    }
                    catch (Exception error) when (Observe(listClient, error, "List SFTP directory")) { throw; }
                    finally { if (listClient is not null) await ReleaseClientAsync(listClient).ConfigureAwait(false); }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StoragePage>.Failure(Map(error, "List SFTP directory")); }
    }

    /// <inheritdoc />
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Create SFTP directory", RetryKind.Idempotent, (_, token) => CreateDirectoryCoreAsync(path, token), cancellationToken);

    private async Task<Result> CreateDirectoryCoreAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception error) { return Result.Failure(Fail(client, error, "Create SFTP directory")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>Uploads are staged and renamed into place, so replaying a seekable source cannot leave a partial file.</remarks>
    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (StorageTransferPipeline.Applies(this, options))
            return StorageTransferPipeline.UploadAsync(this, path, source, options, cancellationToken);
        return _retry.ExecuteUploadAsync("Upload SFTP file", source, token => UploadCoreAsync(path, source, options, token), cancellationToken);
    }

    private async Task<Result<StorageItem>> UploadCoreAsync(string path, Stream source, StorageUploadOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new StorageUploadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        if (options.Condition is { IsEmpty: false })
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "SFTP does not expose portable atomic upload conditions."));
        if (options.Metadata.Count > 0)
            return Result<StorageItem>.Failure(StorageErrors.Unsupported(
                "SFTP does not support portable user metadata."));
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            return await UploadWithClientAsync(client, resolved.Value!, source, options.Overwrite, options.CreateParents, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Upload SFTP file")); }
        finally
        {
            if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Uploads through a staging file in the destination folder and renames it into place, on a session the caller
    /// holds (so a copy reads its source and writes the destination on one session).
    /// </summary>
    private async Task<Result<StorageItem>> UploadWithClientAsync(
        SftpClient client,
        ResolvedRemotePath resolved,
        Stream source,
        bool overwrite,
        bool createParents,
        CancellationToken cancellationToken)
    {
        string? stagingPath = null;
        try
        {
            var parent = RemotePathResolver.Parent(resolved.RemotePath);
            if (createParents)
                await EnsureDirectoryAsync(client, parent, cancellationToken).ConfigureAwait(false);
            else if (!await client.ExistsAsync(parent, cancellationToken).ConfigureAwait(false))
                return Result<StorageItem>.Failure(StorageErrors.NotFound("The SFTP destination parent directory was not found."));
            if (await client.ExistsAsync(resolved.RemotePath, cancellationToken).ConfigureAwait(false))
            {
                var existing = await client.GetAttributesAsync(resolved.RemotePath, cancellationToken).ConfigureAwait(false);
                if (existing.IsDirectory)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("The SFTP upload destination is a directory."));
                if (!overwrite)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("The SFTP destination already exists."));
            }
            var staging = await AllocateTemporaryPathAsync(client, parent, "upload", cancellationToken).ConfigureAwait(false);
            if (staging.IsFailure)
                return Result<StorageItem>.Failure(staging.Error!);
            stagingPath = staging.Value!;
            await client.UploadFileAsync(source, stagingPath, cancellationToken).ConfigureAwait(false);
            var committed = await CommitPathAsync(
                client,
                stagingPath,
                resolved.RemotePath,
                overwrite,
                cancellationToken).ConfigureAwait(false);
            if (committed.IsFailure)
            {
                // Committed, but the replaced file's backup stayed: the upload succeeded.
                if (StorageErrorInfo.DestinationCommitted(committed.Error)) stagingPath = null;
                return Result<StorageItem>.Failure(committed.Error!);
            }
            stagingPath = null;
            var attributes = await client.GetAttributesAsync(resolved.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.StoragePath, attributes));
        }
        finally
        {
            if (stagingPath is not null)
                await TryDeletePathAsync(client, stagingPath).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = new MemoryStream(content, writable: false);
        return await UploadAsync(path, source, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
        await StorageTransferPipeline.MeterAsync(this, path, await DownloadUnmeteredAsync(path, options, cancellationToken).ConfigureAwait(false), options, cancellationToken).ConfigureAwait(false);

    private Task<Result<Stream>> DownloadUnmeteredAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync("Download SFTP file", RetryKind.Idempotent, (_, token) => DownloadCoreAsync(path, options, token), cancellationToken);

    private async Task<Result<Stream>> DownloadCoreAsync(string path, StorageDownloadOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDownloadOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return Result<Stream>.Failure(validation.Error!);
        if (options.VersionId is not null)
            return Result<Stream>.Failure(StorageErrors.Unsupported(
                "SFTP does not support version-specific downloads."));
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
            var owned = new AsyncOwnedResourceStream(stream, () => ReleaseClientAsync(client), () => MarkFaulted(client, "Download SFTP file"));
            ownershipTransferred = true;
            return Result<Stream>.Success(owned);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<Stream>.Failure(Fail(client, error, "Download SFTP file")); }
        finally { if (client is not null && !ownershipTransferred) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Delete SFTP item", RetryKind.NonIdempotent, (_, token) => DeleteCoreAsync(path, options, token), cancellationToken);

    private async Task<Result> DeleteCoreAsync(string path, StorageDeleteOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageDeleteOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (options.Condition is { IsEmpty: false })
            return Result.Failure(StorageErrors.Unsupported(
                "SFTP does not expose portable atomic delete conditions."));
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
        catch (Exception error) { return Result.Failure(Fail(client, error, "Delete SFTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Relayed through the client on one session, which reads the source and writes the destination, so a
    /// connection limited to one session can still copy. SFTP has no ETags or versions: source pinning and
    /// destination conditions are unsupported.
    /// </remarks>
    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (Unpinnable(options) is { } unsupported) return unsupported;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(source.Value.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound($"SFTP item '{source.Value.StoragePath}' was not found."));
            var attributes = await client.GetAttributesAsync(source.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (attributes.IsDirectory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value.StoragePath);
                if (relationship.IsFailure) return relationship;
                return Result.Failure(StorageErrors.Unsupported("SFTP directory copy is not supported."));
            }
            await using var stream = await client.OpenAsync(source.Value.RemotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            var upload = await UploadWithClientAsync(client, destination.Value, stream, options.Overwrite, options.CreateParents, cancellationToken).ConfigureAwait(false);
            return upload.IsSuccess ? Result.Success() : Result.Failure(upload.Error!);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Copy SFTP file")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>SFTP items have no ETag or version, so a copy or move cannot be pinned to one or conditioned on one.</summary>
    private static Result? Unpinnable(StorageTransferOptions options) =>
        options.ExpectedSourceETag is not null || options.SourceVersionId is not null || options.DestinationCondition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.Unsupported("SFTP cannot pin a copy or move to a source version or keep a destination condition."))
            : (Result?)null;

    /// <inheritdoc />
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Move SFTP item", RetryKind.NonIdempotent, (_, token) => MoveCoreAsync(sourcePath, destinationPath, options, token), cancellationToken);

    private async Task<Result> MoveCoreAsync(string sourcePath, string destinationPath, StorageTransferOptions? options, CancellationToken cancellationToken)
    {
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure) return validation;
        if (Unpinnable(options) is { } unsupported) return unsupported;
        var source = _paths.Resolve(sourcePath, requireNonRoot: true);
        if (source.IsFailure) return Result.Failure(source.Error!);
        var destination = _paths.Resolve(destinationPath, requireNonRoot: true);
        if (destination.IsFailure) return Result.Failure(destination.Error!);
        var relationship = StorageTransferPath.ValidateDistinct(
            source.Value!.StoragePath,
            destination.Value!.StoragePath);
        if (relationship.IsFailure) return relationship;
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            if (!await client.ExistsAsync(source.Value!.RemotePath, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound($"SFTP item '{source.Value.StoragePath}' was not found."));
            var sourceAttributes = await client.GetAttributesAsync(source.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            if (sourceAttributes.IsDirectory)
            {
                relationship = StorageTransferPath.ValidateDirectoryDestination(
                    source.Value.StoragePath,
                    destination.Value!.StoragePath);
                if (relationship.IsFailure) return relationship;
            }
            var parent = RemotePathResolver.Parent(destination.Value!.RemotePath);
            if (options.CreateParents)
                await EnsureDirectoryAsync(client, parent, cancellationToken).ConfigureAwait(false);
            else if (!await client.ExistsAsync(parent, cancellationToken).ConfigureAwait(false))
                return Result.Failure(StorageErrors.NotFound("The SFTP destination parent directory was not found."));
            return await CommitPathAsync(
                client,
                source.Value.RemotePath,
                destination.Value.RemotePath,
                options.Overwrite,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result.Failure(Fail(client, error, "Move SFTP item")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Check SFTP health", RetryKind.Idempotent, (_, token) => CheckHealthCoreAsync(token), cancellationToken);

    private async Task<Result> CheckHealthCoreAsync(CancellationToken cancellationToken)
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
        catch (Exception error) { return Result.Failure(Fail(client, error, "Check SFTP health")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class
    {
        client = null;
        return false;
    }

    /// <inheritdoc />
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
                value => _clients.DiscardAsync((SftpClient)(object)value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<NativeConnectionLease<TClient>>.Failure(Fail(client, error, "Open native SFTP connection")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        ChangeAttributesAsync(path, "Set SFTP permissions", attributes =>
        {
            attributes.SetPermissions((short)UnixPermissions.ToOctalDigits(unixMode & 0x1FF));
            attributes.IsUIDBitSet = (unixMode & 0x800) != 0;
            attributes.IsGroupIDBitSet = (unixMode & 0x400) != 0;
            attributes.IsStickyBitSet = (unixMode & 0x200) != 0;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        ChangeAttributesAsync(path, "Set SFTP owner", attributes =>
        {
            if (ownerId is { } owner) attributes.UserId = checked((int)owner);
            if (groupId is { } group) attributes.GroupId = checked((int)group);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default) =>
        ChangeAttributesAsync(path, "Set SFTP timestamps", attributes =>
        {
            if (lastModified is { } modified) attributes.LastWriteTimeUtc = modified.UtcDateTime;
            if (lastAccessed is { } accessed) attributes.LastAccessTimeUtc = accessed.UtcDateTime;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync("Create SFTP link", RetryKind.NonIdempotent, async (_, token) =>
        {
            var link = _paths.Resolve(linkPath, requireNonRoot: true);
            if (link.IsFailure) return Result.Failure(link.Error!);
            var target = _paths.Resolve(targetPath);
            if (target.IsFailure) return Result.Failure(target.Error!);
            SftpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                if (await client.ExistsAsync(link.Value!.RemotePath, token).ConfigureAwait(false))
                    return Result.Failure(StorageErrors.Conflict("The SFTP link path already exists."));
                // SSH.NET swaps the arguments for OpenSSH's reversed SSH_FXP_SYMLINK order itself.
                client.SymbolicLink(target.Value!.RemotePath, link.Value.RemotePath);
                return Result.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result.Failure(Fail(client, error, "Create SFTP link")); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>SSH.NET does not expose SFTP <c>readlink</c>, so link targets cannot be read over SFTP.</remarks>
    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Unsupported(
            "Reading SFTP link targets is not supported by the SSH library.")));

    /// <inheritdoc />
    /// <remarks>Not retried: a lost reply cannot tell how many bytes were appended.</remarks>
    public async Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var resolved = _paths.Resolve(path, requireNonRoot: true);
        if (resolved.IsFailure) return Result<StorageItem>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            // The server's append flag (SSH_FXF_APPEND, which also creates a missing file): every write lands at the
            // end the server sees, so concurrent appenders do not overwrite each other as a client-side seek would.
            await using (var target = await client.OpenAsync(resolved.Value!.RemotePath, FileMode.Append, FileAccess.Write, cancellationToken).ConfigureAwait(false))
            {
                await source.CopyToAsync(target, 65_536, cancellationToken).ConfigureAwait(false);
            }
            var attributes = await client.GetAttributesAsync(resolved.Value.RemotePath, cancellationToken).ConfigureAwait(false);
            return Result<StorageItem>.Success(ToItem(resolved.Value.StoragePath, attributes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageItem>.Failure(Fail(client, error, "Append SFTP file")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs in an SSH shell session as the connection's account, starting in its home directory rather
    /// than <c>Root</c>. Servers that only allow SFTP (<c>ForceCommand internal-sftp</c>) refuse it.
    /// </remarks>
    public async Task<Result<StorageCommandResult>> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (CommandClientFactory is null)
            return Result<StorageCommandResult>.Failure(StorageErrors.Unsupported("Raw commands are disabled for this connection (AllowRawCommands)."));
        if (string.IsNullOrWhiteSpace(command))
            return Result<StorageCommandResult>.Failure(StorageErrors.InvalidContent("A command is required."));
        SshClient? client = null;
        try
        {
            client = CommandClientFactory();
            await SftpHostKeyTracker.ConnectAsync(client, cancellationToken).ConfigureAwait(false);
            using var run = client.CreateCommand(command);
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            var exit = run.ExitStatus ?? -1;
            return Result<StorageCommandResult>.Success(new StorageCommandResult(
                exit == 0,
                exit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                run.Result ?? string.Empty,
                run.Error ?? string.Empty));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { return Result<StorageCommandResult>.Failure(Map(error, "Execute SSH command")); }
        finally
        {
            if (client is not null)
            {
                try { if (client.IsConnected) client.Disconnect(); }
                catch { /* Best effort. */ }
                client.Dispose();
                SftpJumpTunnel.Close(client);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Uses the <c>statvfs@openssh.com</c> extension, which OpenSSH servers provide.</remarks>
    public async Task<Result<StorageSpaceInfo>> GetSpaceAsync(string path = "", CancellationToken cancellationToken = default)
    {
        var resolved = _paths.Resolve(path);
        if (resolved.IsFailure) return Result<StorageSpaceInfo>.Failure(resolved.Error!);
        SftpClient? client = null;
        try
        {
            client = await OpenClientAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.GetStatusAsync(resolved.Value!.RemotePath, cancellationToken).ConfigureAwait(false);
            var block = (long)status.BlockSize;
            return Result<StorageSpaceInfo>.Success(new StorageSpaceInfo(
                (long)status.TotalBlocks * block,
                (long)status.AvailableBlocks * block,
                ((long)status.TotalBlocks - (long)status.FreeBlocks) * block));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SshException error) when (error.Message.Contains("statvfs", StringComparison.OrdinalIgnoreCase))
        {
            // SSH.NET reports a server without the extension as a plain SshException naming it.
            return Result<StorageSpaceInfo>.Failure(StorageErrors.Unsupported("The SFTP server does not report free space (statvfs@openssh.com)."));
        }
        catch (Exception error) { return Result<StorageSpaceInfo>.Failure(Fail(client, error, "Get SFTP free space")); }
        finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
    }

    /// <summary>Reads an item's attributes, applies a change, and writes back only what changed.</summary>
    private Task<Result> ChangeAttributesAsync(string path, string operation, Action<SftpFileAttributes> change, CancellationToken cancellationToken) =>
        _retry.ExecuteAsync(operation, RetryKind.Idempotent, async (_, token) =>
        {
            var resolved = _paths.Resolve(path);
            if (resolved.IsFailure) return Result.Failure(resolved.Error!);
            SftpClient? client = null;
            try
            {
                client = await OpenClientAsync(token).ConfigureAwait(false);
                var attributes = await client.GetAttributesAsync(resolved.Value!.RemotePath, token).ConfigureAwait(false);
                change(attributes);
                client.SetAttributes(resolved.Value.RemotePath, attributes);
                return Result.Success();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { return Result.Failure(Fail(client, error, operation)); }
            finally { if (client is not null) await ReleaseClientAsync(client).ConfigureAwait(false); }
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _pool.ReleaseAsync();
    }

    private Task<SftpClient> OpenClientAsync(CancellationToken cancellationToken) =>
        _clients.RentAsync(cancellationToken, _sessionOpened);

    /// <summary>Returns a client to the pool so the next operation reuses its SSH session.</summary>
    private ValueTask ReleaseClientAsync(SftpClient client) => _clients.ReturnAsync(client);

    /// <summary>Stats the working directory so a session the server silently dropped is detected before reuse.</summary>
    private static async Task<bool> ProbeAsync(SftpClient client, CancellationToken cancellationToken)
    {
        await client.GetAttributesAsync(".", cancellationToken).ConfigureAwait(false);
        return client.IsConnected;
    }

    private static ValueTask DestroyClientAsync(SftpClient client)
    {
        try { if (client.IsConnected) client.Disconnect(); }
        catch { }
        finally
        {
            client.Dispose();
            SftpJumpTunnel.Close(client);
        }
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

    /// <summary>
    /// Renames <paramref name="sourcePath"/> onto <paramref name="destinationPath"/>. An existing file destination
    /// (with <paramref name="overwrite"/>) is first renamed to a backup, restored if the rename fails, and removed
    /// after it succeeds; an existing directory is never replaced. A backup that cannot be removed after the
    /// commit is reported with <c>destinationState=complete</c> and <c>leftBehind</c>.
    /// </summary>
    private async Task<Result> CommitPathAsync(
        SftpClient client,
        string sourcePath,
        string destinationPath,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        if (!await client.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false))
        {
            await client.RenameFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        if (!overwrite)
            return Result.Failure(StorageErrors.Conflict("The SFTP destination already exists."));

        var destinationAttributes = await client.GetAttributesAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        // Replacing a directory would delete everything in it (the backup is removed recursively): refuse, as a
        // local move does; the caller can merge through a copy instead.
        if (destinationAttributes.IsDirectory)
            return Result.Failure(StorageErrors.Conflict("The SFTP destination is an existing directory, which is not replaced."));
        var backup = await AllocateTemporaryPathAsync(
            client,
            RemotePathResolver.Parent(destinationPath),
            "backup",
            cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
            return Result.Failure(backup.Error!);
        var backupPath = backup.Value!;
        await client.RenameFileAsync(destinationPath, backupPath, cancellationToken).ConfigureAwait(false);

        try
        {
            await client.RenameFileAsync(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            _ = await TryRestoreBackupAsync(client, backupPath, destinationPath).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            if (await IsCommitCompleteAsync(client, sourcePath, destinationPath).ConfigureAwait(false))
                return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
            var restored = await TryRestoreBackupAsync(client, backupPath, destinationPath).ConfigureAwait(false);
            return restored
                ? Result.Failure(Map(error, "Commit SFTP replacement"))
                : Result.Failure(StorageErrors.PartialFailure(
                    "The SFTP replacement failed and its previous destination could not be restored.",
                    $"{StorageErrorInfo.DestinationStateKey}=backup;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}"));
        }

        return await DeleteBackupAsync(client, backupPath).ConfigureAwait(false);
    }

    private static async Task<Result<string>> AllocateTemporaryPathAsync(
        SftpClient client,
        string parent,
        string purpose,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = parent.TrimEnd('/') + $"/.cl-storage-{purpose}-{Guid.NewGuid():N}.tmp";
            if (!await client.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false))
                return Result<string>.Success(candidate);
        }
        return Result<string>.Failure(StorageErrors.Conflict(
            "Unable to allocate a unique SFTP staging path."));
    }

    private static async Task<bool> IsCommitCompleteAsync(
        SftpClient client,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            var sourceExists = await client.ExistsAsync(sourcePath, CancellationToken.None).ConfigureAwait(false);
            var destinationExists = await client.ExistsAsync(destinationPath, CancellationToken.None).ConfigureAwait(false);
            return !sourceExists && destinationExists;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryRestoreBackupAsync(
        SftpClient client,
        string backupPath,
        string destinationPath)
    {
        try
        {
            if (!await client.ExistsAsync(backupPath, CancellationToken.None).ConfigureAwait(false) ||
                await client.ExistsAsync(destinationPath, CancellationToken.None).ConfigureAwait(false))
                return false;
            await client.RenameFileAsync(backupPath, destinationPath, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs before the backup of a replaced file is removed, so a test can make the removal fail.</summary>
    internal Func<SftpClient, string, Task>? BeforeBackupDelete { get; init; }

    /// <summary>Removes the backup of a replaced file once the replacement committed.</summary>
    private async Task<Result> DeleteBackupAsync(SftpClient client, string backupPath)
    {
        try
        {
            if (BeforeBackupDelete is { } hook) await hook(client, backupPath).ConfigureAwait(false);
            await client.DeleteFileAsync(backupPath, CancellationToken.None).ConfigureAwait(false);
            return Result.Success();
        }
        catch
        {
            return Result.Failure(StorageErrors.PartialFailure(
                "The SFTP destination committed, but its temporary backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;backupState=present;{StorageErrorInfo.LeftBehindKey}={_paths.FromRemotePath(backupPath) ?? backupPath}"));
        }
    }

    private static async Task TryDeletePathAsync(SftpClient client, string path)
    {
        try
        {
            if (!await client.ExistsAsync(path, CancellationToken.None).ConfigureAwait(false))
                return;
            var attributes = await client.GetAttributesAsync(path, CancellationToken.None).ConfigureAwait(false);
            await DeleteRemoteAsync(
                client,
                path,
                attributes.IsDirectory,
                recursive: true,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup must not mask the primary upload result.
        }
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

    /// <summary>Maps a failure and retires the session when the failure left it unusable.</summary>
    private Error Fail(SftpClient? client, Exception exception, string operation)
    {
        var error = Map(exception, operation);
        if (client is not null && IsSessionFault(error))
            MarkFaulted(client, operation, error);
        return error;
    }

    /// <summary>Exception filter that retires a faulted session without catching the exception.</summary>
    private bool Observe(SftpClient? client, Exception exception, string operation)
    {
        if (client is not null && exception is not OperationCanceledException)
            _ = Fail(client, exception, operation);
        return false;
    }

    private void MarkFaulted(SftpClient client, string operation, Error? error = null)
    {
        _clients.MarkFaulted(client);
        try { _observer?.SessionFaulted(ConnectionId, StorageProvider.Sftp, operation, error ?? StorageErrors.ConnectionLost($"{operation}: the transfer was interrupted.")); }
        catch { /* Observers must never change the operation's outcome. */ }
    }

    /// <summary>Failures after which the session's protocol state is unknown and it must not be reused.</summary>
    private static bool IsSessionFault(Error error) =>
        StorageErrorInfo.IsConnectionFault(error) || error.Code == StorageErrors.TimeoutCode;

    private static StorageItem ToItem(string path, ISftpFile item) => ToItem(path, item.Attributes);

    private static StorageItem ToItem(string path, SftpFileAttributes attributes)
    {
        var name = NameOf(path);
        return new StorageItem
        {
            Path = path,
            Name = name,
            ItemType = attributes.IsSymbolicLink ? StorageItemType.Link : attributes.IsDirectory ? StorageItemType.Directory : StorageItemType.File,
            Size = attributes.IsRegularFile ? attributes.Size : null,
            LastModified = new DateTimeOffset(attributes.LastWriteTimeUtc),
            LastAccessed = new DateTimeOffset(attributes.LastAccessTimeUtc),
            UnixMode = ModeOf(attributes),
            OwnerId = attributes.UserId,
            GroupId = attributes.GroupId,
            IsHidden = name.StartsWith('.')
        };
    }

    /// <summary>Rebuilds the numeric mode from the flags SSH.NET exposes.</summary>
    internal static int ModeOf(SftpFileAttributes attributes)
    {
        var mode = 0;
        if (attributes.IsUIDBitSet) mode |= 0x800;
        if (attributes.IsGroupIDBitSet) mode |= 0x400;
        if (attributes.IsStickyBitSet) mode |= 0x200;
        if (attributes.OwnerCanRead) mode |= 0x100;
        if (attributes.OwnerCanWrite) mode |= 0x80;
        if (attributes.OwnerCanExecute) mode |= 0x40;
        if (attributes.GroupCanRead) mode |= 0x20;
        if (attributes.GroupCanWrite) mode |= 0x10;
        if (attributes.GroupCanExecute) mode |= 0x8;
        if (attributes.OthersCanRead) mode |= 0x4;
        if (attributes.OthersCanWrite) mode |= 0x2;
        if (attributes.OthersCanExecute) mode |= 0x1;
        return mode;
    }

    private static StorageItem DirectoryItem(string path) => new()
    {
        Path = path,
        Name = path.Length == 0 ? string.Empty : NameOf(path),
        ItemType = StorageItemType.Directory
    };

    private static string NameOf(string path) => path.Split('/')[^1];

    internal static Error Map(Exception exception, string operation)
    {
        switch (exception)
        {
            case SftpHostKeyRejectedException rejected:
                return StorageErrors.HostKeyRejected(
                    $"{operation}: the SSH host key was not trusted.",
                    $"presentedFingerprint={rejected.Fingerprint}");
            case SftpPathNotFoundException:
                return StorageErrors.NotFound($"{operation}: item was not found.");
            case SftpPermissionDeniedException:
                return StorageErrors.PermissionDenied($"{operation}: access was denied.");
            case SshAuthenticationException:
                return StorageErrors.AuthenticationFailed($"{operation}: the SSH server rejected the credentials.");
            case SshOperationTimeoutException:
                return StorageErrors.Timeout($"{operation}: operation timed out.");
            case ProxyException:
                return StorageErrors.ConnectionFailed($"{operation}: could not connect through the proxy.");
            case SftpException sftp:
                return MapStatus(sftp, operation);
            case SshConnectionException connection:
                return MapDisconnect(connection, operation);
        }
        if (ProviderErrorMapper.FromTransport(exception, operation, "SFTP") is { } transport)
            return transport;
        if (exception is SshException && IsQuotaMessage(exception.Message))
            return StorageErrors.QuotaExceeded($"{operation}: the SFTP server has insufficient storage.");
        if (exception is ObjectDisposedException)
            return StorageErrors.ConnectionLost($"{operation}: the SFTP connection was lost.");
        return StorageErrors.ProviderError($"{operation}: SFTP provider failed.", ProviderErrorMapper.ExceptionDetails(exception));
    }

    private static Error MapStatus(SftpException sftp, string operation)
    {
        var details = $"{StorageErrorInfo.SftpStatusKey}={sftp.StatusCode}";
        return sftp.StatusCode switch
        {
            StatusCode.NoSuchFile => StorageErrors.NotFound($"{operation}: item was not found.", details),
            StatusCode.PermissionDenied => StorageErrors.PermissionDenied($"{operation}: access was denied.", details),
            StatusCode.NoConnection or StatusCode.ConnectionLost => StorageErrors.ConnectionLost($"{operation}: the SFTP connection was lost.", details),
            StatusCode.OperationUnsupported => StorageErrors.Unsupported($"{operation}: the SFTP server does not support this operation.", details),
            StatusCode.Failure when IsQuotaMessage(sftp.Message) => StorageErrors.QuotaExceeded($"{operation}: the SFTP server has insufficient storage.", details),
            _ => StorageErrors.ProviderError($"{operation}: SFTP request failed.", details)
        };
    }

    private static Error MapDisconnect(SshConnectionException connection, string operation)
    {
        var details = $"disconnectReason={connection.DisconnectReason}";
        return connection.DisconnectReason switch
        {
            DisconnectReason.HostKeyNotVerifiable => StorageErrors.HostKeyRejected($"{operation}: the SSH host key was not trusted.", details),
            DisconnectReason.TooManyConnections => StorageErrors.ServerBusy($"{operation}: the SSH server has too many sessions.", details),
            DisconnectReason.NoMoreAuthenticationMethodsAvailable or DisconnectReason.IllegalUserName or DisconnectReason.AuthenticationCanceledByUser
                => StorageErrors.AuthenticationFailed($"{operation}: the SSH server rejected the credentials.", details),
            DisconnectReason.HostNotAllowedToConnect or DisconnectReason.ProtocolVersionNotSupported
                => StorageErrors.ConnectionFailed($"{operation}: the SSH server refused the connection.", details),
            DisconnectReason.KeyExchangeFailed
                => StorageErrors.ConnectionFailed($"{operation}: SSH key exchange failed; check host key and algorithm settings.", details),
            DisconnectReason.ServiceNotAvailable => StorageErrors.Unavailable($"{operation}: the SFTP subsystem is unavailable.", details),
            _ => StorageErrors.ConnectionLost($"{operation}: the SFTP connection was lost.", details)
        };
    }

    private static bool IsQuotaMessage(string? message) =>
        message is not null &&
        (message.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("no space", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("disk full", StringComparison.OrdinalIgnoreCase));
}
