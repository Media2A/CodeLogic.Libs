using System.Collections.ObjectModel;
using System.Text.Json;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.Swift;
using CL.Storage.Providers.WebDav;
using CL.Storage.Registry;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

/// <summary>Provider-neutral storage library for CodeLogic applications.</summary>
public sealed class StorageLibrary : ILibrary, IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly object _registryGate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, BackendEntry> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageServiceProxy> _proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageConnectionInfo> _connectionInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<Type, IStorageBackendFactory> _factories;
    private readonly Action? _defaultConnectionSnapshotCaptured;
    private LibraryContext? _context;
    private StorageConfig? _storageConfig;
    private LocalStorageConfig? _localConfig;
    private LocalStorageConfig? _persistedLocalConfig;
    private readonly Dictionary<string, LocalConnectionConfig?> _runtimeLocalOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Task? _stopTask;
    private LifecycleState _state;
    private bool _enabled;

    public StorageLibrary() : this([
        new LocalStorageBackendFactory(),
        new S3StorageBackendFactory(),
        new FtpStorageBackendFactory(),
        new SftpStorageBackendFactory(),
        new WebDavStorageBackendFactory(),
        new AzureBlobStorageBackendFactory(),
        new GoogleCloudStorageBackendFactory(),
        new SwiftStorageBackendFactory()
    ], null)
    { }

    internal StorageLibrary(IEnumerable<IStorageBackendFactory> factories) : this(factories, null) { }

    internal StorageLibrary(
        IEnumerable<IStorageBackendFactory> factories,
        Action? defaultConnectionSnapshotCaptured)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories.ToDictionary(factory => factory.ConfigurationType);
        _defaultConnectionSnapshotCaptured = defaultConnectionSnapshotCaptured;
        if (_factories.Count == 0)
            throw new ArgumentException("At least one storage backend factory is required.", nameof(factories));
    }

    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.Storage",
        Name = "Storage Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "Provider-neutral mounted storage connections",
        Author = "Media2A",
        Tags = ["storage", "filesystem", "cloud"]
    };

    /// <summary>Returns the service for the configured default connection.</summary>
    public IStorageService DefaultStorage => GetStorage(GetDefaultConnectionId());

    public Task OnConfigureAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Created)
                throw new InvalidOperationException("Storage library configuration can only run once.");
            _context = context;
            _state = LifecycleState.Configured;
        }

        context.Configuration.Register<StorageConfig>("storage");
        context.Configuration.Register<LocalStorageConfig>("storage.local");
        context.Configuration.Register<S3StorageConfig>("storage.s3");
        context.Configuration.Register<FtpStorageConfig>("storage.ftp");
        context.Configuration.Register<SftpStorageConfig>("storage.sftp");
        context.Configuration.Register<WebDavStorageConfig>("storage.webdav");
        context.Configuration.Register<AzureStorageConfig>("storage.azure");
        context.Configuration.Register<GoogleCloudStorageConfig>("storage.gcs");
        context.Configuration.Register<SwiftStorageConfig>("storage.swift");
        return Task.CompletedTask;
    }

    public async Task OnInitializeAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Configured)
                throw new InvalidOperationException("Storage library must be configured before it is initialized.");
            if (!ReferenceEquals(_context, context))
                throw new InvalidOperationException("Storage library lifecycle phases must use the same LibraryContext.");
        }

        var storage = context.Configuration.Get<StorageConfig>();
        var local = context.Configuration.Get<LocalStorageConfig>();
        var s3 = context.Configuration.Get<S3StorageConfig>();
        var ftp = context.Configuration.Get<FtpStorageConfig>();
        var sftp = context.Configuration.Get<SftpStorageConfig>();
        var webDav = context.Configuration.Get<WebDavStorageConfig>();
        var azure = context.Configuration.Get<AzureStorageConfig>();
        var gcs = context.Configuration.Get<GoogleCloudStorageConfig>();
        var swift = context.Configuration.Get<SwiftStorageConfig>();

        EnsureValid("storage", storage.Validate());
        EnsureValid("storage.local", local.Validate());
        EnsureValid("storage.s3", s3.Validate());
        EnsureValid("storage.ftp", ftp.Validate());
        EnsureValid("storage.sftp", sftp.Validate());
        EnsureValid("storage.webdav", webDav.Validate());
        EnsureValid("storage.azure", azure.Validate());
        EnsureValid("storage.gcs", gcs.Validate());
        EnsureValid("storage.swift", swift.Validate());

        ValidateGlobalIds(local, s3, ftp, sftp, webDav, azure, gcs, swift);

        var builtEntries = new Dictionary<string, BackendEntry>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, StorageConnectionInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (storage.Enabled)
            {
                foreach (var (id, configuration) in local.Connections)
                {
                    infos.Add(id, new StorageConnectionInfo(id, StorageProvider.Local, configuration.RootPath, configuration.Enabled));
                    if (!configuration.Enabled)
                        continue;
                    var backend = _factories[typeof(LocalConnectionConfig)].Create(
                        id,
                        configuration,
                        storage.MaxBufferedDownloadBytes);
                    builtEntries.Add(id, new BackendEntry(backend, ownsBackend: true));
                }

                AddProviderConnections(builtEntries, infos, s3, StorageProvider.S3, storage.MaxBufferedDownloadBytes);
                AddProviderConnections(builtEntries, infos, ftp, StorageProvider.Ftp, storage.MaxBufferedDownloadBytes);
                AddProviderConnections(builtEntries, infos, sftp, StorageProvider.Sftp, storage.MaxBufferedDownloadBytes);
                AddProviderConnections(builtEntries, infos, webDav, StorageProvider.WebDav, storage.MaxBufferedDownloadBytes);
                AddProviderConnections(builtEntries, infos, azure, StorageProvider.AzureBlob, storage.MaxBufferedDownloadBytes);
                AddProviderConnections(builtEntries, infos, gcs, StorageProvider.GoogleCloudStorage, storage.MaxBufferedDownloadBytes);
                AddProviderConnections(builtEntries, infos, swift, StorageProvider.OpenStackSwift, storage.MaxBufferedDownloadBytes);

                if (!builtEntries.ContainsKey(storage.DefaultConnection))
                    throw new InvalidOperationException(
                        $"The configured default storage connection '{storage.DefaultConnection}' is not available from an enabled provider factory.");
            }

            lock (_stateGate)
            {
                if (_state != LifecycleState.Configured || !ReferenceEquals(_context, context))
                    throw new InvalidOperationException("Storage library stopped before initialization completed.");

                lock (_registryGate)
                {
                    foreach (var pair in builtEntries)
                        _registry.Add(pair.Key, pair.Value);
                    foreach (var pair in infos)
                        _connectionInfos.Add(pair.Key, pair.Value);
                }

                _storageConfig = storage;
                _localConfig = local;
                _persistedLocalConfig = CloneLocalConfig(local);
                _runtimeLocalOverrides.Clear();
                _enabled = storage.Enabled;
                _state = LifecycleState.Initialized;
            }
        }
        catch
        {
            await Task.WhenAll(builtEntries.Values.Select(entry => entry.RetireAndDisposeAsync())).ConfigureAwait(false);
            throw;
        }
    }

    public Task OnStartAsync(LibraryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_stateGate)
        {
            if (_state != LifecycleState.Initialized)
                throw new InvalidOperationException("Storage library must be initialized before it is started.");
            if (!ReferenceEquals(_context, context))
                throw new InvalidOperationException("Storage library lifecycle phases must use the same LibraryContext.");
            _state = LifecycleState.Started;
        }
        return Task.CompletedTask;
    }

    public Task OnStopAsync()
    {
        lock (_stateGate)
        {
            if (_state is LifecycleState.Stopped or LifecycleState.Disposed)
                return Task.CompletedTask;
            if (_stopTask is not null)
                return _stopTask;
            _state = LifecycleState.Stopping;
            _stopTask = StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        BackendEntry[] entries = [];
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
            gateEntered = true;
            try
            {
                lock (_registryGate)
                {
                    entries = _registry.Values.ToArray();
                    _registry.Clear();
                    _connectionInfos.Clear();
                }
            }
            finally
            {
                _mutationGate.Release();
                gateEntered = false;
            }

            var disposals = entries
                .Select(entry => (entry.Backend.ConnectionId, Task: entry.RetireAndDisposeAsync()))
                .ToArray();
            try
            {
                await Task.WhenAll(disposals.Select(disposal => disposal.Task)).ConfigureAwait(false);
            }
            catch
            {
                var failedIds = new List<string>();
                foreach (var disposal in disposals.Where(disposal => disposal.Task.IsFaulted))
                {
                    failedIds.Add(disposal.ConnectionId);
                    _context?.Logger.Error(
                        $"Storage backend '{disposal.ConnectionId}' failed during disposal.",
                        disposal.Task.Exception?.GetBaseException());
                }
                throw new InvalidOperationException(
                    $"Failed to dispose storage backend(s): {string.Join(", ", failedIds)}.");
            }
        }
        finally
        {
            if (gateEntered)
                _mutationGate.Release();
            lock (_stateGate)
            {
                _context = null;
                _storageConfig = null;
                _localConfig = null;
                _persistedLocalConfig = null;
                _runtimeLocalOverrides.Clear();
                _enabled = false;
                _state = LifecycleState.Stopped;
            }
        }
    }

    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!TryCaptureRuntimeSnapshot(out _, out var state))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var targets = new List<HealthTarget>();
        int timeoutSeconds;
        ILogger logger;
        try
        {
            await _mutationGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return HealthStatus.Unhealthy("Storage library is disposed");
        }

        try
        {
            if (!TryCaptureRuntimeSnapshot(out var runtime, out state))
                return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");
            if (!runtime.Enabled)
                return HealthStatus.Healthy("Storage library is disabled");

            timeoutSeconds = runtime.HealthCheckTimeoutSeconds;
            logger = runtime.Logger;

            lock (_registryGate)
            {
                if (!_registry.ContainsKey(runtime.DefaultConnection))
                    return HealthStatus.Unhealthy($"Default storage connection '{runtime.DefaultConnection}' is unavailable");
                foreach (var (id, entry) in _registry)
                {
                    if (!entry.TryAcquire(out var lease))
                    {
                        foreach (var target in targets)
                            target.Lease.Dispose();
                        targets.Clear();
                        return HealthStatus.Unhealthy($"Storage connection '{id}' is retiring");
                    }
                    targets.Add(new HealthTarget(id, lease!));
                }
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        var probes = await Task.WhenAll(
            targets.Select(target => ProbeHealthAsync(target, timeoutSeconds, logger))).ConfigureAwait(false);

        state = GetLifecycleState();
        if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var failed = probes.Where(probe => !probe.Healthy).ToArray();
        if (failed.Length == 0)
            return HealthStatus.Healthy($"All {probes.Length} storage connection(s) are healthy");

        var failedIds = string.Join(", ", failed.Select(probe => probe.Id));
        return new HealthStatus
        {
            Status = failed.Length == probes.Length ? HealthStatusLevel.Unhealthy : HealthStatusLevel.Degraded,
            Message = failed.Length == probes.Length
                ? $"All storage connections failed: {failedIds}"
                : $"Storage connections unavailable: {failedIds}",
            Data = new Dictionary<string, object>
            {
                ["failedConnections"] = failed.ToDictionary(
                    probe => probe.Id,
                    probe => (object)probe.Detail,
                    StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    /// <summary>Returns a stable service proxy for a named effective connection.</summary>
    public IStorageService GetStorage(string connectionId = "Default")
    {
        ValidateConnectionId(connectionId);
        EnsureOperational();
        lock (_registryGate)
        {
            if (!_registry.TryGetValue(connectionId, out var entry))
                throw new KeyNotFoundException($"Storage connection '{connectionId}' is not registered or enabled.");
            var effectiveId = entry.Backend.ConnectionId;
            if (!_proxies.TryGetValue(effectiveId, out var proxy))
            {
                proxy = new StorageServiceProxy(this, effectiveId);
                _proxies.Add(effectiveId, proxy);
            }
            return proxy;
        }
    }

    /// <summary>Attempts to return a stable service proxy without throwing for an unknown or disabled connection.</summary>
    public bool TryGetStorage(
        string connectionId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStorageService? storage)
    {
        ValidateConnectionId(connectionId);
        EnsureOperational();
        lock (_registryGate)
        {
            if (!_registry.TryGetValue(connectionId, out var entry))
            {
                storage = null;
                return false;
            }
            var effectiveId = entry.Backend.ConnectionId;
            if (!_proxies.TryGetValue(effectiveId, out var proxy))
            {
                proxy = new StorageServiceProxy(this, effectiveId);
                _proxies.Add(effectiveId, proxy);
            }
            storage = proxy;
            return true;
        }
    }

    /// <summary>Checks one connection using the configured health timeout.</summary>
    public async Task<Result<HealthStatus>> CheckConnectionHealthAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        var runtime = CaptureRuntimeSnapshot();
        using var lease = AcquireOperation(connectionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var health = await lease.Backend.CheckHealthAsync(linked.Token).ConfigureAwait(false);
            if (health.IsFailure)
                return Result<HealthStatus>.Failure(health.Error!);
            return Result<HealthStatus>.Success(HealthStatus.Healthy(
                $"Storage connection '{lease.Backend.ConnectionId}' is healthy"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{lease.Backend.ConnectionId}' timed out during health check."));
        }
        catch (TimeoutException)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{lease.Backend.ConnectionId}' timed out during health check."));
        }
        catch (Exception)
        {
            return Result<HealthStatus>.Failure(StorageErrors.ProviderError(
                $"Storage connection '{lease.Backend.ConnectionId}' failed its health check."));
        }
    }

    /// <summary>
    /// Copies a file or directory between mounted connections. Cross-connection transfers use a
    /// bounded streaming relay and a unique destination staging object before final commit.
    /// </summary>
    public Task<Result> CopyAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(
            sourceConnectionId,
            sourcePath,
            destinationConnectionId,
            destinationPath,
            options,
            move: false,
            cancellationToken);

    /// <summary>
    /// Uploads a complete local directory through the bounded, rollback-safe transfer coordinator.
    /// Local links/reparse points are rejected rather than followed.
    /// </summary>
    public async Task<Result<StorageDirectoryTransferReport>> UploadDirectoryAsync(
        string sourceDirectoryPath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(destinationConnectionId);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(validation.Error!);
        if (string.IsNullOrWhiteSpace(sourceDirectoryPath))
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                "A local source directory path is required."));
        var normalizedDestination = StoragePath.Normalize(destinationPath);
        if (normalizedDestination.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(normalizedDestination.Error!);

        string localRoot;
        try
        {
            localRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectoryPath));
            if (!Directory.Exists(localRoot))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.NotFound(
                    "The local upload source directory was not found."));
        }
        catch (Exception error)
        {
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.FromException(
                error,
                "Resolve local upload directory"));
        }

        await using var source = new LocalStorageBackend(
            ".cl-storage-directory-upload",
            new LocalConnectionConfig { RootPath = localRoot, FollowLinks = false },
            _storageConfig?.MaxBufferedDownloadBytes ?? LocalStorageBackend.DefaultMaxBufferedDownloadBytes);
        BackendEntry.BackendOperationLease? destinationLease = null;
        StorageEventPublisher? publisher = null;
        StorageDirectoryTransferReport? report = null;
        string? destinationId = null;
        StorageProvider destinationProvider = default;
        try
        {
            destinationLease = AcquireOperation(destinationConnectionId);
            destinationId = destinationLease.Backend.ConnectionId;
            destinationProvider = destinationLease.Backend.Provider;
            var copied = await StorageTransferCoordinator.CopyAsync(
                source,
                string.Empty,
                destinationLease.Backend,
                normalizedDestination.Value!,
                options,
                cancellationToken).ConfigureAwait(false);
            if (copied.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(copied.Error!);
            report = new StorageDirectoryTransferReport(
                copied.Value!.Files,
                copied.Value.Directories,
                copied.Value.Bytes);
            publisher = CaptureEventPublisher();
        }
        finally
        {
            destinationLease?.Dispose();
        }

        await publisher!.PublishAsync(new StorageDirectoryUploadedEvent(
            destinationId!,
            destinationProvider,
            normalizedDestination.Value!,
            report!.Files,
            report.Directories,
            report.Bytes,
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return Result<StorageDirectoryTransferReport>.Success(report);
    }

    /// <summary>
    /// Downloads a complete storage directory into a caller-selected local directory with per-file
    /// atomic replacement and rollback of changes made by a failed transfer.
    /// </summary>
    public async Task<Result<StorageDirectoryTransferReport>> DownloadDirectoryAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationDirectoryPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(sourceConnectionId);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var validation = options.Validate();
        if (validation.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(validation.Error!);
        var normalizedSource = StoragePath.Normalize(sourcePath);
        if (normalizedSource.IsFailure)
            return Result<StorageDirectoryTransferReport>.Failure(normalizedSource.Error!);
        if (string.IsNullOrWhiteSpace(destinationDirectoryPath))
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                "A local destination directory path is required."));

        string parent;
        string localDestinationName;
        try
        {
            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectoryPath));
            if (File.Exists(destination))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.Conflict(
                    "The local directory destination is an existing file."));
            parent = Path.GetDirectoryName(destination) ?? string.Empty;
            localDestinationName = Path.GetFileName(destination);
            if (parent.Length == 0 || localDestinationName.Length == 0)
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.InvalidPath(
                    "The local destination must identify a named directory below a parent directory."));
            if (options.CreateParents)
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.NotFound(
                    "The local destination parent directory was not found."));
        }
        catch (Exception error)
        {
            return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.FromException(
                error,
                "Resolve local download directory"));
        }

        await using var destinationBackend = new LocalStorageBackend(
            ".cl-storage-directory-download",
            new LocalConnectionConfig { RootPath = parent, FollowLinks = false },
            _storageConfig?.MaxBufferedDownloadBytes ?? LocalStorageBackend.DefaultMaxBufferedDownloadBytes);
        BackendEntry.BackendOperationLease? sourceLease = null;
        StorageEventPublisher? publisher = null;
        StorageDirectoryTransferReport? report = null;
        string? sourceId = null;
        StorageProvider sourceProvider = default;
        try
        {
            sourceLease = AcquireOperation(sourceConnectionId);
            sourceId = sourceLease.Backend.ConnectionId;
            sourceProvider = sourceLease.Backend.Provider;
            var sourceInfo = await sourceLease.Backend.GetInfoAsync(
                normalizedSource.Value!,
                cancellationToken).ConfigureAwait(false);
            if (sourceInfo.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(sourceInfo.Error!);
            if (sourceInfo.Value!.ItemType != StorageItemType.Directory)
                return Result<StorageDirectoryTransferReport>.Failure(StorageErrors.Conflict(
                    "DownloadDirectoryAsync requires a storage directory source."));

            var copied = await StorageTransferCoordinator.CopyAsync(
                sourceLease.Backend,
                normalizedSource.Value!,
                destinationBackend,
                localDestinationName,
                options,
                cancellationToken).ConfigureAwait(false);
            if (copied.IsFailure)
                return Result<StorageDirectoryTransferReport>.Failure(copied.Error!);
            report = new StorageDirectoryTransferReport(
                copied.Value!.Files,
                copied.Value.Directories,
                copied.Value.Bytes);
            publisher = CaptureEventPublisher();
        }
        finally
        {
            sourceLease?.Dispose();
        }

        await publisher!.PublishAsync(new StorageDirectoryDownloadedEvent(
            sourceId!,
            sourceProvider,
            normalizedSource.Value!,
            report!.Files,
            report.Directories,
            report.Bytes,
            DateTimeOffset.UtcNow)).ConfigureAwait(false);
        return Result<StorageDirectoryTransferReport>.Success(report);
    }

    /// <summary>
    /// Moves a file or directory between mounted connections. The source is deleted only after the
    /// complete destination tree has committed successfully.
    /// </summary>
    public Task<Result> MoveAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TransferAsync(
            sourceConnectionId,
            sourcePath,
            destinationConnectionId,
            destinationPath,
            options,
            move: true,
            cancellationToken);

    private async Task<Result> TransferAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        StorageTransferOptions? options,
        bool move,
        CancellationToken cancellationToken)
    {
        ValidateConnectionId(sourceConnectionId);
        ValidateConnectionId(destinationConnectionId);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageTransferOptions();
        var optionsValidation = options.Validate();
        if (optionsValidation.IsFailure)
            return optionsValidation;

        var normalizedSource = NormalizeTransferPath(sourcePath, "source");
        if (normalizedSource.IsFailure)
            return Result.Failure(normalizedSource.Error!);
        var normalizedDestination = NormalizeTransferPath(destinationPath, "destination");
        if (normalizedDestination.IsFailure)
            return Result.Failure(normalizedDestination.Error!);

        BackendEntry.BackendOperationLease? sourceLease = null;
        BackendEntry.BackendOperationLease? destinationLease = null;
        StorageEventPublisher? publisher = null;
        StorageTransferSummary? summary = null;
        Result result = Result.Success();
        bool sameBackend = false;
        string? effectiveSourceId = null;
        string? effectiveDestinationId = null;
        StorageProvider sourceProvider = default;
        StorageProvider destinationProvider = default;
        try
        {
            sourceLease = AcquireOperation(sourceConnectionId);
            destinationLease = AcquireOperation(destinationConnectionId);
            var sourceBackend = sourceLease.Backend;
            var destinationBackend = destinationLease.Backend;
            sameBackend = ReferenceEquals(sourceBackend, destinationBackend);
            effectiveSourceId = sourceBackend.ConnectionId;
            effectiveDestinationId = destinationBackend.ConnectionId;
            sourceProvider = sourceBackend.Provider;
            destinationProvider = destinationBackend.Provider;
            var usedNativeOperation = false;

            if (sameBackend)
            {
                var relationship = StorageTransferPath.ValidateDistinct(
                    normalizedSource.Value!,
                    normalizedDestination.Value!);
                if (relationship.IsFailure)
                    return relationship;

                var sourceInfo = await sourceBackend.GetInfoAsync(
                    normalizedSource.Value!,
                    cancellationToken).ConfigureAwait(false);
                if (sourceInfo.IsFailure)
                    return Result.Failure(sourceInfo.Error!);
                if (sourceInfo.Value!.ItemType == StorageItemType.Directory)
                {
                    relationship = StorageTransferPath.ValidateDirectoryDestination(
                        normalizedSource.Value!,
                        normalizedDestination.Value!);
                    if (relationship.IsFailure)
                        return relationship;
                }

                var requiredFeatures = (move, sourceInfo.Value.ItemType) switch
                {
                    (false, StorageItemType.File) => StorageFeature.FileCopy | StorageFeature.ServerSideCopy,
                    // Directory copy implementations commonly loop over provider objects and can
                    // leave a partial tree. The coordinator tracks and rolls back every new item.
                    (false, StorageItemType.Directory) => StorageFeature.None,
                    (true, StorageItemType.File) => StorageFeature.FileMove | StorageFeature.ServerSideMove,
                    (true, StorageItemType.Directory) => StorageFeature.DirectoryMove |
                        StorageFeature.ServerSideMove |
                        StorageFeature.AtomicMove,
                    _ => StorageFeature.None
                };
                var supportsNativeOperation = requiredFeatures != StorageFeature.None &&
                    sourceBackend.Capabilities.Supports(requiredFeatures);
                if (supportsNativeOperation)
                {
                    result = move
                        ? await sourceBackend.MoveAsync(
                            normalizedSource.Value!,
                            normalizedDestination.Value!,
                            options,
                            cancellationToken).ConfigureAwait(false)
                        : await sourceBackend.CopyAsync(
                            normalizedSource.Value!,
                            normalizedDestination.Value!,
                            options,
                            cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccess)
                        publisher = CaptureEventPublisher();
                    usedNativeOperation = true;
                }
            }

            if (!usedNativeOperation)
            {
                var copied = await StorageTransferCoordinator.CopyAsync(
                    sourceBackend,
                    normalizedSource.Value!,
                    destinationBackend,
                    normalizedDestination.Value!,
                    options,
                    cancellationToken).ConfigureAwait(false);
                if (copied.IsFailure)
                    return Result.Failure(copied.Error!);
                summary = copied.Value!;

                if (move)
                {
                    var deleted = await sourceBackend.DeleteAsync(
                        normalizedSource.Value!,
                        new StorageDeleteOptions
                        {
                            Recursive = summary.SourceType == StorageItemType.Directory,
                            IgnoreMissing = false
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (deleted.IsFailure)
                        return Result.Failure(StorageErrors.PartialFailure(
                            "The destination completed, but the source could not be deleted.",
                            $"sourceDeleteError={deleted.Error!.Code};destinationState=complete"));
                }

                result = Result.Success();
                publisher = CaptureEventPublisher();
            }
        }
        finally
        {
            destinationLease?.Dispose();
            sourceLease?.Dispose();
        }

        return await PublishTransferResultAsync(
            result,
            publisher,
            sameBackend,
            move,
            effectiveSourceId!,
            sourceProvider,
            normalizedSource.Value!,
            effectiveDestinationId!,
            destinationProvider,
            normalizedDestination.Value!,
            summary).ConfigureAwait(false);
    }

    private static Result<string> NormalizeTransferPath(string path, string role)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return normalized;
        return normalized.Value!.Length == 0
            ? Result<string>.Failure(StorageErrors.InvalidPath(
                $"A non-root transfer {role} path is required."))
            : normalized;
    }

    private static async Task<Result> PublishTransferResultAsync(
        Result result,
        StorageEventPublisher? publisher,
        bool sameBackend,
        bool move,
        string sourceConnectionId,
        StorageProvider sourceProvider,
        string sourcePath,
        string destinationConnectionId,
        StorageProvider destinationProvider,
        string destinationPath,
        StorageTransferSummary? summary)
    {
        if (result.IsFailure || publisher is null)
            return result;

        if (sameBackend)
        {
            if (move)
            {
                await publisher.PublishAsync(new StorageItemMovedEvent(
                    sourceConnectionId,
                    sourceProvider,
                    sourcePath,
                    destinationPath,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
            else
            {
                await publisher.PublishAsync(new StorageItemCopiedEvent(
                    sourceConnectionId,
                    sourceProvider,
                    sourcePath,
                    destinationPath,
                    DateTimeOffset.UtcNow)).ConfigureAwait(false);
            }
        }
        else if (move)
        {
            await publisher.PublishAsync(new StorageCrossConnectionMoveCompletedEvent(
                sourceConnectionId,
                sourceProvider,
                sourcePath,
                destinationConnectionId,
                destinationProvider,
                destinationPath,
                summary!.Files,
                summary.Directories,
                summary.Bytes,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        else
        {
            await publisher.PublishAsync(new StorageCrossConnectionCopyCompletedEvent(
                sourceConnectionId,
                sourceProvider,
                sourcePath,
                destinationConnectionId,
                destinationProvider,
                destinationPath,
                summary!.Files,
                summary.Directories,
                summary.Bytes,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>Returns an immutable snapshot containing sanitized connection information.</summary>
    public IReadOnlyList<StorageConnectionInfo> GetConnections()
    {
        EnsureOperational();
        lock (_registryGate)
        {
            var snapshot = _connectionInfos.Values
                .OrderBy(connection => connection.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ReadOnlyCollection<StorageConnectionInfo>(snapshot);
        }
    }

    /// <summary>
    /// Returns a reusable native client owned by the effective backend. A client already returned by this
    /// method becomes invalid when its connection is replaced or removed and must not be disposed by callers.
    /// </summary>
    public TClient GetNativeClient<TClient>(string connectionId = "Default") where TClient : class
    {
        ValidateConnectionId(connectionId);
        using var lease = AcquireOperation(connectionId);
        if (lease.Backend.TryGetNativeClient<TClient>(out var client))
            return client;
        throw new InvalidOperationException(
            $"Storage connection '{connectionId}' does not expose a reusable native client of type '{typeof(TClient).FullName}'.");
    }

    /// <summary>Opens a provider-native session whose lease retains the backend until disposal.</summary>
    public async Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(
        string connectionId = "Default",
        CancellationToken cancellationToken = default)
        where TClient : class
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        var entryLease = AcquireOperation(connectionId);
        try
        {
            var result = await entryLease.Backend.OpenNativeConnectionAsync<TClient>(cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                entryLease.Dispose();
                return Result<NativeConnectionLease<TClient>>.Failure(result.Error!);
            }

            var backendLease = result.Value!;
            return Result<NativeConnectionLease<TClient>>.Success(new NativeConnectionLease<TClient>(
                backendLease.Client,
                async _ =>
                {
                    try { await backendLease.DisposeAsync().ConfigureAwait(false); }
                    finally { entryLease.Dispose(); }
                }));
        }
        catch
        {
            entryLease.Dispose();
            throw;
        }
    }

    /// <summary>Adds or replaces a built-in typed connection and optionally persists its JSON section.</summary>
    public async Task<Result> AddOrUpdateConnectionAsync<TConfig>(
        string id,
        TConfig config,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(config);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        if (config is not LocalConnectionConfig localConnection)
        {
            if (config is StorageConnectionConfigBase providerConnection)
                return await AddOrUpdateProviderConnectionAsync(
                    id,
                    providerConnection,
                    persist,
                    runtime,
                    cancellationToken).ConfigureAwait(false);
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{typeof(TConfig).Name}' does not have a provider factory in this version."));
        }

        var validation = localConnection.Validate();
        if (!validation.IsValid)
            return Result.Failure(StorageErrors.ProviderError(
                $"Local storage connection '{id}' is invalid: {string.Join("; ", validation.Errors)}"));

        BackendEntry? replacement = null;
        if (localConnection.Enabled)
        {
            var built = await BuildHealthyLocalReplacementAsync(
                id,
                localConnection,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (built.IsFailure)
                return Result.Failure(built.Error!);
            replacement = built.Value!;
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredNonLocalSection(id, runtime.Context);
            if (conflictingSection is not null)
                return Result.Failure(StorageErrors.Conflict(
                    $"Storage connection ID '{id}' is already configured in '{conflictingSection}'. IDs are case-insensitive."));

            if (persist)
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig!);
                RemoveKey(candidate.Connections, id);
                candidate.Connections[id] = Clone(localConnection);
                try
                {
                    await runtime.Context.Configuration.SaveAsync(candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist local storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist local storage connection '{id}'."));
                }
                _persistedLocalConfig = CloneLocalConfig(candidate);
                _runtimeLocalOverrides.Remove(id);
                ApplyRuntimeLocalOverrides(candidate);
                _localConfig = candidate;
            }
            else
            {
                _runtimeLocalOverrides[id] = Clone(localConnection);
                SetLocalConnection(_localConfig!, id, localConnection);
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous))
                    _registry.Remove(id);
                if (replacement is not null)
                    _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(id, StorageProvider.Local, localConnection.RootPath, localConnection.Enabled);
            }
            replacement = null;
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            if (gateEntered)
                _mutationGate.Release();
            if (replacement is not null)
                await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result> AddOrUpdateProviderConnectionAsync(
        string id,
        StorageConnectionConfigBase connection,
        bool persist,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        var descriptor = DescribeProviderConnection(connection.GetType());
        if (descriptor is null || !_factories.ContainsKey(connection.GetType()))
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{connection.GetType().Name}' does not have a provider factory."));

        var errors = connection.GetValidationErrors().ToArray();
        if (errors.Length > 0)
            return Result.Failure(StorageErrors.ProviderError(
                $"{descriptor.Value.Provider} storage connection '{id}' is invalid: {string.Join("; ", errors)}"));

        var effectiveConnection = CloneProviderConnection(connection);
        BackendEntry? replacement = null;
        if (effectiveConnection.Enabled)
        {
            var built = await BuildHealthyProviderReplacementAsync(
                id,
                effectiveConnection,
                descriptor.Value,
                runtime,
                cancellationToken).ConfigureAwait(false);
            if (built.IsFailure) return Result.Failure(built.Error!);
            replacement = built.Value!;
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredSection(
                id,
                runtime.Context,
                exceptConnectionType: effectiveConnection.GetType());
            if (conflictingSection is not null)
                return Result.Failure(StorageErrors.Conflict(
                    $"Storage connection ID '{id}' is already configured in '{conflictingSection}'. IDs are case-insensitive."));
            lock (_registryGate)
            {
                if (_connectionInfos.TryGetValue(id, out var existing) && existing.Provider != descriptor.Value.Provider)
                    return Result.Failure(StorageErrors.Conflict(
                        $"Storage connection ID '{id}' is already registered for provider '{existing.Provider}'."));
            }

            if (persist)
            {
                var current = GetProviderConfig(runtime.Context, effectiveConnection.GetType());
                var candidate = current.DeepClone();
                candidate.SetConnection(id, effectiveConnection);
                try
                {
                    await SaveProviderConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist {descriptor.Value.Provider} storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist {descriptor.Value.Provider} storage connection '{id}'."));
                }
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous)) _registry.Remove(id);
                if (replacement is not null) _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(
                    id,
                    descriptor.Value.Provider,
                    effectiveConnection.MountRoot,
                    effectiveConnection.Enabled);
            }
            replacement = null;
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            if (gateEntered) _mutationGate.Release();
            if (replacement is not null)
                await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result<BackendEntry>> BuildHealthyProviderReplacementAsync(
        string id,
        StorageConnectionConfigBase configuration,
        ProviderDescriptor descriptor,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        BackendEntry? replacement = null;
        Task<Result>? probeTask = null;
        var deferCleanup = false;
        try
        {
            replacement = new BackendEntry(
                _factories[configuration.GetType()].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes),
                ownsBackend: true);
            var timeoutDuration = TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds);
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Result health;
            try
            {
                probeTask = replacement.Backend.CheckHealthAsync(linked.Token);
                health = await probeTask.WaitAsync(timeoutDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                deferCleanup = true;
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            if (health.IsFailure) return Result<BackendEntry>.Failure(health.Error!);
            var result = replacement;
            replacement = null;
            return Result<BackendEntry>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result<BackendEntry>.Failure(StorageErrors.ProviderError(
                $"Could not build {descriptor.Provider} storage connection '{id}'."));
        }
        finally
        {
            if (replacement is not null)
            {
                var cleanup = ObserveProbeAndDisposeReplacementAsync(id, replacement, probeTask, runtime.Logger);
                if (deferCleanup || probeTask is { IsCompleted: false }) _ = cleanup;
                else await cleanup.ConfigureAwait(false);
            }
        }
    }

    private async Task<Result<BackendEntry>> BuildHealthyLocalReplacementAsync(
        string id,
        LocalConnectionConfig configuration,
        RuntimeSnapshot runtime,
        CancellationToken cancellationToken)
    {
        BackendEntry? replacement = null;
        Task<Result>? probeTask = null;
        var deferCleanup = false;
        var logger = runtime.Logger;
        try
        {
            replacement = new BackendEntry(
                _factories[typeof(LocalConnectionConfig)].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes),
                ownsBackend: true);

            var timeoutDuration = TimeSpan.FromSeconds(runtime.HealthCheckTimeoutSeconds);
            using var timeout = new CancellationTokenSource(timeoutDuration);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            Result health;
            try
            {
                probeTask = replacement.Backend.CheckHealthAsync(linked.Token);
                health = await probeTask
                    .WaitAsync(timeoutDuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                deferCleanup = true;
                throw;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                deferCleanup = true;
                return Result<BackendEntry>.Failure(StorageErrors.Timeout(
                    $"Storage connection '{id}' timed out during replacement health check."));
            }

            if (health.IsFailure)
                return Result<BackendEntry>.Failure(health.Error!);

            var result = replacement;
            replacement = null;
            return Result<BackendEntry>.Success(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return Result<BackendEntry>.Failure(StorageErrors.ProviderError(
                $"Could not build local storage connection '{id}'."));
        }
        finally
        {
            if (replacement is not null)
            {
                var cleanup = ObserveProbeAndDisposeReplacementAsync(id, replacement, probeTask, logger);
                if (deferCleanup || probeTask is { IsCompleted: false })
                    _ = cleanup;
                else
                    await cleanup.ConfigureAwait(false);
            }
        }
    }

    /// <summary>Removes an effective connection and, for local connections, optionally persists the removal.</summary>
    public async Task<Result> RemoveConnectionAsync(
        string id,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        BackendEntry? removed = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            bool exists;
            StorageConnectionInfo? info;
            lock (_registryGate)
                exists = _registry.ContainsKey(id) || _connectionInfos.TryGetValue(id, out info);
            var providerMatch = FindProviderConfigContaining(id, runtime.Context);
            exists = exists ||
                TryFindKey(_localConfig!.Connections, id, out _) ||
                TryFindKey(_persistedLocalConfig!.Connections, id, out _) ||
                providerMatch is not null;
            if (!exists)
                return Result.Failure(StorageErrors.NotFound($"Storage connection '{id}' is not registered."));

            var effectiveLocal = TryFindKey(_localConfig!.Connections, id, out _);
            var persistedLocal = TryFindKey(_persistedLocalConfig!.Connections, id, out var persistedKey);
            if (providerMatch is not null)
            {
                if (persist)
                {
                    var candidate = providerMatch.Config.DeepClone();
                    candidate.RemoveConnection(id);
                    try
                    {
                        await SaveProviderConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        runtime.Logger.Error(
                            $"Failed to persist removal of {providerMatch.Descriptor.Provider} storage connection '{id}'.",
                            error);
                        return Result.Failure(StorageErrors.ProviderError(
                            $"Could not persist removal of {providerMatch.Descriptor.Provider} storage connection '{id}'."));
                    }
                }
            }
            else if (persist && (effectiveLocal || persistedLocal))
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig);
                if (persistedKey is not null)
                    candidate.Connections.Remove(persistedKey);
                try
                {
                    await runtime.Context.Configuration.SaveAsync(candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    runtime.Logger.Error($"Failed to persist removal of local storage connection '{id}'.", error);
                    return Result.Failure(StorageErrors.ProviderError(
                        $"Could not persist removal of local storage connection '{id}'."));
                }
                _persistedLocalConfig = CloneLocalConfig(candidate);
                _runtimeLocalOverrides.Remove(id);
                ApplyRuntimeLocalOverrides(candidate);
                _localConfig = candidate;
            }
            else if (!persist && effectiveLocal)
            {
                _runtimeLocalOverrides[id] = null;
                RemoveKey(_localConfig.Connections, id);
            }

            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out removed))
                    _registry.Remove(id);
                _connectionInfos.Remove(id);
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (removed is not null)
            await removed.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Registers a prebuilt custom backend for this runtime only.</summary>
    public Result RegisterBackend(string id, IStorageBackend backend, bool ownsBackend = true)
        => RegisterBackendAsync(id, backend, ownsBackend).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// Registers a prebuilt custom backend after an asynchronous health check. Ownership transfers only
    /// when registration succeeds; replacement disposal completes before this method returns.
    /// </summary>
    public async Task<Result> RegisterBackendAsync(
        string id,
        IStorageBackend backend,
        bool ownsBackend = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(backend);
        var runtime = CaptureRuntimeSnapshot();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(id, backend.ConnectionId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(StorageErrors.Conflict(
                $"Backend connection ID '{backend.ConnectionId}' does not match registry ID '{id}'."));

        var health = await CheckRegistrationHealthAsync(
            id,
            backend,
            runtime.HealthCheckTimeoutSeconds,
            runtime.Logger,
            cancellationToken).ConfigureAwait(false);
        if (health.IsFailure)
            return health;

        BackendEntry? previous = null;
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOperational();
            lock (_registryGate)
            {
                if (_registry.TryGetValue(id, out previous) && ReferenceEquals(previous.Backend, backend))
                    return Result.Success();
                var replacement = new BackendEntry(backend, ownsBackend);
                _registry[id] = replacement;
                _connectionInfos[id] = new StorageConnectionInfo(
                    backend.ConnectionId,
                    backend.Provider,
                    backend.Root,
                    Enabled: true);
            }
        }
        finally
        {
            _mutationGate.Release();
        }

        if (previous is not null)
            await previous.RetireAndDisposeAsync().ConfigureAwait(false);
        return Result.Success();
    }

    private static async Task<Result> CheckRegistrationHealthAsync(
        string id,
        IStorageBackend backend,
        int timeoutSeconds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var result = await backend.CheckHealthAsync(linked.Token)
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
            return result.IsSuccess
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable(
                    $"Storage backend '{id}' failed its registration health check.",
                    result.Error?.Code ?? StorageErrors.ProviderErrorCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result.Failure(StorageErrors.Timeout(
                $"Storage backend '{id}' timed out during its registration health check."));
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            return Result.Failure(StorageErrors.Timeout(
                $"Storage backend '{id}' timed out during its registration health check."));
        }
        catch (Exception error)
        {
            logger.Error($"Storage backend '{id}' threw during its registration health check.", error);
            return Result.Failure(StorageErrors.ProviderError(
                $"Storage backend '{id}' failed its registration health check."));
        }
    }

    public void Dispose()
    {
        LifecycleState state;
        lock (_stateGate)
            state = _state;
        if (state == LifecycleState.Disposed)
            return;
        OnStopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        lock (_stateGate)
            _state = LifecycleState.Disposed;
    }

    public async ValueTask DisposeAsync()
    {
        LifecycleState state;
        lock (_stateGate)
            state = _state;
        if (state == LifecycleState.Disposed)
            return;
        try
        {
            await OnStopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_stateGate)
                _state = LifecycleState.Disposed;
            GC.SuppressFinalize(this);
        }
    }

    internal BackendEntry.BackendOperationLease AcquireOperation(string connectionId)
    {
        while (true)
        {
            EnsureOperational();
            BackendEntry entry;
            lock (_registryGate)
            {
                if (!_registry.TryGetValue(connectionId, out entry!))
                    throw new KeyNotFoundException($"Storage connection '{connectionId}' is not registered or enabled.");
            }
            if (entry.TryAcquire(out var lease))
                return lease!;
        }
    }

    internal StorageEventPublisher CaptureEventPublisher()
    {
        LibraryContext context;
        lock (_stateGate)
            context = _context ?? throw new InvalidOperationException("Storage library context is unavailable.");
        return new StorageEventPublisher(context.Events, context.Logger);
    }

    private static async Task ObserveProbeAndDisposeReplacementAsync(
        string id,
        BackendEntry replacement,
        Task<Result>? probeTask,
        ILogger logger)
    {
        if (probeTask is not null)
        {
            try { await probeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                TryLog(logger, $"Unpublished storage replacement '{id}' health probe failed after the caller stopped waiting.", error);
            }
        }

        try { await replacement.RetireAndDisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            TryLog(logger, $"Failed to dispose unpublished storage replacement '{id}'.", error);
        }
    }

    private static async Task<HealthProbe> ProbeHealthAsync(
        HealthTarget target,
        int timeoutSeconds,
        ILogger logger)
    {
        Task<Result>? probeTask = null;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                probeTask = target.Lease.Backend.CheckHealthAsync(timeout.Token);
                var result = await probeTask
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds))
                    .ConfigureAwait(false);
                return result.IsSuccess
                    ? new HealthProbe(target.Id, true, string.Empty)
                    : new HealthProbe(target.Id, false, result.Error?.Code ?? "storage.provider_error");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return new HealthProbe(target.Id, false, "storage.timeout");
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                return new HealthProbe(target.Id, false, "storage.timeout");
            }
            catch (Exception)
            {
                return new HealthProbe(target.Id, false, "storage.provider_error");
            }
            finally
            {
                if (probeTask is null)
                {
                    target.Lease.Dispose();
                }
                else
                {
                    var release = ObserveProbeAndReleaseHealthLeaseAsync(target, probeTask, logger);
                    if (probeTask.IsCompleted)
                        await release.ConfigureAwait(false);
                    else
                        _ = release;
                }
            }
        }
    }

    private static async Task ObserveProbeAndReleaseHealthLeaseAsync(
        HealthTarget target,
        Task<Result> probeTask,
        ILogger logger)
    {
        try { await probeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            TryLog(logger, $"Storage connection '{target.Id}' health probe failed after the caller stopped waiting.", error);
        }
        finally
        {
            target.Lease.Dispose();
        }
    }

    private static void TryLog(ILogger logger, string message, Exception error)
    {
        try { logger.Error(message, error); }
        catch { }
    }

    private LifecycleState GetLifecycleState()
    {
        lock (_stateGate)
            return _state;
    }

    private string GetDefaultConnectionId()
    {
        var runtime = CaptureRuntimeSnapshot();
        _defaultConnectionSnapshotCaptured?.Invoke();
        return runtime.DefaultConnection;
    }

    private RuntimeSnapshot CaptureRuntimeSnapshot()
    {
        if (TryCaptureRuntimeSnapshot(out var runtime, out var state))
            return runtime;
        throw new InvalidOperationException($"Storage library is {DescribeState(state)}; public access requires an initialized library.");
    }

    private bool TryCaptureRuntimeSnapshot(out RuntimeSnapshot runtime, out LifecycleState state)
    {
        lock (_stateGate)
        {
            state = _state;
            if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            {
                runtime = default;
                return false;
            }

            var context = _context ?? throw new InvalidOperationException(
                "Storage library runtime context is unavailable while the library is initialized.");
            var storageConfig = _storageConfig ?? throw new InvalidOperationException(
                "Storage library runtime configuration is unavailable while the library is initialized.");
            runtime = new RuntimeSnapshot(
                context,
                storageConfig,
                context.Logger,
                _enabled,
                storageConfig.DefaultConnection,
                storageConfig.MaxBufferedDownloadBytes,
                storageConfig.HealthCheckTimeoutSeconds);
            return true;
        }
    }

    private void EnsureOperational() => _ = CaptureRuntimeSnapshot();

    private static string DescribeState(LifecycleState state) => state switch
    {
        LifecycleState.Created => "not initialized",
        LifecycleState.Configured => "configured but not initialized",
        LifecycleState.Stopping => "stopping",
        LifecycleState.Stopped => "stopped",
        LifecycleState.Disposed => "disposed",
        _ => state.ToString().ToLowerInvariant()
    };

    private static void EnsureValid(string section, CodeLogic.Core.Configuration.ConfigValidationResult validation)
    {
        if (!validation.IsValid)
            throw new InvalidOperationException($"Storage configuration section '{section}' is invalid: {string.Join("; ", validation.Errors)}");
    }

    private static StorageConnectionConfigBase CloneProviderConnection(StorageConnectionConfigBase source) =>
        (StorageConnectionConfigBase)JsonSerializer.Deserialize(
            JsonSerializer.Serialize(source, source.GetType()),
            source.GetType())!;

    private static ProviderDescriptor? DescribeProviderConnection(Type connectionType)
    {
        if (connectionType == typeof(S3ConnectionConfig))
            return new ProviderDescriptor(StorageProvider.S3, "storage.s3");
        if (connectionType == typeof(FtpConnectionConfig))
            return new ProviderDescriptor(StorageProvider.Ftp, "storage.ftp");
        if (connectionType == typeof(SftpConnectionConfig))
            return new ProviderDescriptor(StorageProvider.Sftp, "storage.sftp");
        if (connectionType == typeof(WebDavConnectionConfig))
            return new ProviderDescriptor(StorageProvider.WebDav, "storage.webdav");
        if (connectionType == typeof(AzureBlobConnectionConfig))
            return new ProviderDescriptor(StorageProvider.AzureBlob, "storage.azure");
        if (connectionType == typeof(GoogleCloudConnectionConfig))
            return new ProviderDescriptor(StorageProvider.GoogleCloudStorage, "storage.gcs");
        if (connectionType == typeof(SwiftConnectionConfig))
            return new ProviderDescriptor(StorageProvider.OpenStackSwift, "storage.swift");
        return null;
    }

    private static ProviderStorageConfigBase GetProviderConfig(LibraryContext context, Type connectionType)
    {
        var configuration = context.Configuration;
        if (connectionType == typeof(S3ConnectionConfig)) return configuration.Get<S3StorageConfig>();
        if (connectionType == typeof(FtpConnectionConfig)) return configuration.Get<FtpStorageConfig>();
        if (connectionType == typeof(SftpConnectionConfig)) return configuration.Get<SftpStorageConfig>();
        if (connectionType == typeof(WebDavConnectionConfig)) return configuration.Get<WebDavStorageConfig>();
        if (connectionType == typeof(AzureBlobConnectionConfig)) return configuration.Get<AzureStorageConfig>();
        if (connectionType == typeof(GoogleCloudConnectionConfig)) return configuration.Get<GoogleCloudStorageConfig>();
        if (connectionType == typeof(SwiftConnectionConfig)) return configuration.Get<SwiftStorageConfig>();
        throw new NotSupportedException($"Provider connection type '{connectionType.FullName}' is not supported.");
    }

    private static Task SaveProviderConfigAsync(LibraryContext context, ProviderStorageConfigBase config) => config switch
    {
        S3StorageConfig value => context.Configuration.SaveAsync(value),
        FtpStorageConfig value => context.Configuration.SaveAsync(value),
        SftpStorageConfig value => context.Configuration.SaveAsync(value),
        WebDavStorageConfig value => context.Configuration.SaveAsync(value),
        AzureStorageConfig value => context.Configuration.SaveAsync(value),
        GoogleCloudStorageConfig value => context.Configuration.SaveAsync(value),
        SwiftStorageConfig value => context.Configuration.SaveAsync(value),
        _ => throw new NotSupportedException($"Provider configuration type '{config.GetType().FullName}' is not supported.")
    };

    private static string? FindConfiguredSection(
        string id,
        LibraryContext context,
        Type? exceptConnectionType = null)
    {
        var configuration = context.Configuration;
        if (exceptConnectionType != typeof(LocalConnectionConfig) &&
            TryFindKey(configuration.Get<LocalStorageConfig>().Connections, id, out _))
            return "storage.local";
        if (exceptConnectionType != typeof(S3ConnectionConfig) && configuration.Get<S3StorageConfig>().ContainsConnection(id))
            return "storage.s3";
        if (exceptConnectionType != typeof(FtpConnectionConfig) && configuration.Get<FtpStorageConfig>().ContainsConnection(id))
            return "storage.ftp";
        if (exceptConnectionType != typeof(SftpConnectionConfig) && configuration.Get<SftpStorageConfig>().ContainsConnection(id))
            return "storage.sftp";
        if (exceptConnectionType != typeof(WebDavConnectionConfig) && configuration.Get<WebDavStorageConfig>().ContainsConnection(id))
            return "storage.webdav";
        if (exceptConnectionType != typeof(AzureBlobConnectionConfig) && configuration.Get<AzureStorageConfig>().ContainsConnection(id))
            return "storage.azure";
        if (exceptConnectionType != typeof(GoogleCloudConnectionConfig) && configuration.Get<GoogleCloudStorageConfig>().ContainsConnection(id))
            return "storage.gcs";
        if (exceptConnectionType != typeof(SwiftConnectionConfig) && configuration.Get<SwiftStorageConfig>().ContainsConnection(id))
            return "storage.swift";
        return null;
    }

    private static ProviderConfigMatch? FindProviderConfigContaining(string id, LibraryContext context)
    {
        var configuration = context.Configuration;
        ProviderStorageConfigBase[] configs =
        [
            configuration.Get<S3StorageConfig>(),
            configuration.Get<FtpStorageConfig>(),
            configuration.Get<SftpStorageConfig>(),
            configuration.Get<WebDavStorageConfig>(),
            configuration.Get<AzureStorageConfig>(),
            configuration.Get<GoogleCloudStorageConfig>(),
            configuration.Get<SwiftStorageConfig>()
        ];
        foreach (var config in configs)
        {
            if (!config.ContainsConnection(id)) continue;
            var connectionType = config.EnumerateConnections().First().Value.GetType();
            return new ProviderConfigMatch(config, DescribeProviderConnection(connectionType)!.Value);
        }
        return null;
    }

    private static void ValidateGlobalIds(
        LocalStorageConfig local,
        S3StorageConfig s3,
        FtpStorageConfig ftp,
        SftpStorageConfig sftp,
        WebDavStorageConfig webDav,
        AzureStorageConfig azure,
        GoogleCloudStorageConfig gcs,
        SwiftStorageConfig swift)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(local.Connections.Keys, "storage.local");
        Add(s3.Connections.Keys, "storage.s3");
        Add(ftp.Connections.Keys, "storage.ftp");
        Add(sftp.Connections.Keys, "storage.sftp");
        Add(webDav.Connections.Keys, "storage.webdav");
        Add(azure.Connections.Keys, "storage.azure");
        Add(gcs.Connections.Keys, "storage.gcs");
        Add(swift.Connections.Keys, "storage.swift");
        return;

        void Add(IEnumerable<string> connectionIds, string section)
        {
            foreach (var id in connectionIds)
            {
                if (ids.TryGetValue(id, out var existing))
                    throw new InvalidOperationException(
                        $"Duplicate storage connection ID '{id}' appears in '{existing}' and '{section}'. IDs are case-insensitive.");
                ids.Add(id, section);
            }
        }
    }

    private void AddProviderConnections<TConnection>(
        IDictionary<string, BackendEntry> entries,
        IDictionary<string, StorageConnectionInfo> infos,
        ProviderStorageConfigBase<TConnection> config,
        StorageProvider provider,
        long maxBufferedDownloadBytes)
        where TConnection : StorageConnectionConfigBase
    {
        _factories.TryGetValue(typeof(TConnection), out var factory);
        foreach (var (id, connection) in config.Connections)
        {
            infos.Add(id, new StorageConnectionInfo(id, provider, connection.MountRoot, connection.Enabled));
            if (!connection.Enabled)
                continue;
            if (factory is null)
                throw new InvalidOperationException($"The {provider} provider factory is not registered.");
            entries.Add(id, new BackendEntry(
                factory.Create(id, connection, maxBufferedDownloadBytes),
                ownsBackend: true));
        }
    }

    private static void ValidateConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A storage connection ID is required.", nameof(connectionId));
    }

    private static string? FindConfiguredNonLocalSection(string id, LibraryContext context)
    {
        var configuration = context.Configuration;
        if (Contains(configuration.Get<S3StorageConfig>(), id)) return "storage.s3";
        if (Contains(configuration.Get<FtpStorageConfig>(), id)) return "storage.ftp";
        if (Contains(configuration.Get<SftpStorageConfig>(), id)) return "storage.sftp";
        if (Contains(configuration.Get<WebDavStorageConfig>(), id)) return "storage.webdav";
        if (Contains(configuration.Get<AzureStorageConfig>(), id)) return "storage.azure";
        if (Contains(configuration.Get<GoogleCloudStorageConfig>(), id)) return "storage.gcs";
        if (Contains(configuration.Get<SwiftStorageConfig>(), id)) return "storage.swift";
        return null;

        static bool Contains(ProviderStorageConfigBase providerConfig, string connectionId) =>
            providerConfig.ContainsConnection(connectionId);
    }

    private static LocalStorageConfig CloneLocalConfig(LocalStorageConfig source)
    {
        var clone = new LocalStorageConfig();
        foreach (var (id, connection) in source.Connections)
            clone.Connections[id] = Clone(connection);
        return clone;
    }

    private static LocalConnectionConfig Clone(LocalConnectionConfig source) => new()
    {
        Enabled = source.Enabled,
        RootPath = source.RootPath,
        FollowLinks = source.FollowLinks
    };

    private void ApplyRuntimeLocalOverrides(LocalStorageConfig target)
    {
        foreach (var (id, connection) in _runtimeLocalOverrides)
        {
            RemoveKey(target.Connections, id);
            if (connection is not null)
                target.Connections[id] = Clone(connection);
        }
    }

    private static void SetLocalConnection(LocalStorageConfig target, string id, LocalConnectionConfig connection)
    {
        RemoveKey(target.Connections, id);
        target.Connections[id] = Clone(connection);
    }

    private static void RemoveKey<T>(IDictionary<string, T> dictionary, string id)
    {
        if (TryFindKey(dictionary, id, out var key))
            dictionary.Remove(key!);
    }

    private static bool TryFindKey<T>(IDictionary<string, T> dictionary, string id, out string? key)
    {
        key = dictionary.Keys.FirstOrDefault(candidate => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase));
        return key is not null;
    }

    private sealed record HealthProbe(string Id, bool Healthy, string Detail);
    private sealed record HealthTarget(string Id, BackendEntry.BackendOperationLease Lease);
    private readonly record struct ProviderDescriptor(StorageProvider Provider, string Section);
    private sealed record ProviderConfigMatch(ProviderStorageConfigBase Config, ProviderDescriptor Descriptor);
    private readonly record struct RuntimeSnapshot(
        LibraryContext Context,
        StorageConfig StorageConfig,
        ILogger Logger,
        bool Enabled,
        string DefaultConnection,
        long MaxBufferedDownloadBytes,
        int HealthCheckTimeoutSeconds);

    private enum LifecycleState
    {
        Created,
        Configured,
        Initialized,
        Started,
        Stopping,
        Stopped,
        Disposed
    }
}
