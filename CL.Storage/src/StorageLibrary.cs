using System.Collections.ObjectModel;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Registry;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.Storage;

/// <summary>Provider-neutral storage library for CodeLogic applications.</summary>
public sealed class StorageLibrary : ILibrary
{
    private readonly object _stateGate = new();
    private readonly object _registryGate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, BackendEntry> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageServiceProxy> _proxies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageConnectionInfo> _connectionInfos = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<Type, IStorageBackendFactory> _factories =
        new IStorageBackendFactory[] { new LocalStorageBackendFactory() }
            .ToDictionary(factory => factory.ConfigurationType);
    private LibraryContext? _context;
    private StorageConfig? _storageConfig;
    private LocalStorageConfig? _localConfig;
    private LocalStorageConfig? _persistedLocalConfig;
    private readonly Dictionary<string, LocalConnectionConfig?> _runtimeLocalOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Task? _stopTask;
    private LifecycleState _state;
    private bool _enabled;

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

                AddRemoteInfos(infos, s3, StorageProvider.S3);
                AddRemoteInfos(infos, ftp, StorageProvider.Ftp);
                AddRemoteInfos(infos, sftp, StorageProvider.Sftp);
                AddRemoteInfos(infos, webDav, StorageProvider.WebDav);
                AddRemoteInfos(infos, azure, StorageProvider.AzureBlob);
                AddRemoteInfos(infos, gcs, StorageProvider.GoogleCloudStorage);
                AddRemoteInfos(infos, swift, StorageProvider.OpenStackSwift);

                if (!builtEntries.ContainsKey(storage.DefaultConnection))
                    throw new InvalidOperationException(
                        $"The configured default storage connection '{storage.DefaultConnection}' is not available from an enabled provider factory.");
            }

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
            lock (_stateGate)
                _state = LifecycleState.Initialized;
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
            _context = null;
            _storageConfig = null;
            _localConfig = null;
            _persistedLocalConfig = null;
            _runtimeLocalOverrides.Clear();
            _enabled = false;
            lock (_stateGate)
                _state = LifecycleState.Stopped;
        }
    }

    public async Task<HealthStatus> HealthCheckAsync()
    {
        var state = GetLifecycleState();
        if (state is not (LifecycleState.Initialized or LifecycleState.Started))
            return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");

        var targets = new List<HealthTarget>();
        int timeoutSeconds;
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
            state = GetLifecycleState();
            if (state is not (LifecycleState.Initialized or LifecycleState.Started))
                return HealthStatus.Unhealthy($"Storage library is {DescribeState(state)}");
            if (!_enabled)
                return HealthStatus.Healthy("Storage library is disabled");

            var config = _storageConfig;
            if (config is null)
                return HealthStatus.Unhealthy("Storage library is stopping");
            timeoutSeconds = config.HealthCheckTimeoutSeconds;

            lock (_registryGate)
            {
                if (!_registry.ContainsKey(config.DefaultConnection))
                    return HealthStatus.Unhealthy($"Default storage connection '{config.DefaultConnection}' is unavailable");
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

        HealthProbe[] probes;
        try
        {
            probes = await Task.WhenAll(targets.Select(target => ProbeHealthAsync(target, timeoutSeconds))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var target in targets)
                target.Lease.Dispose();
        }

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

    /// <summary>Adds or replaces a built-in typed connection. Task 2 supports local connections.</summary>
    public async Task<Result> AddOrUpdateConnectionAsync<TConfig>(
        string id,
        TConfig config,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(config);
        EnsureOperational();
        cancellationToken.ThrowIfCancellationRequested();
        if (config is not LocalConnectionConfig localConnection)
            return Result.Failure(StorageErrors.Unsupported(
                $"Connection configuration type '{typeof(TConfig).Name}' does not have a provider factory in this version."));

        var validation = localConnection.Validate();
        if (!validation.IsValid)
            return Result.Failure(StorageErrors.ProviderError(
                $"Local storage connection '{id}' is invalid: {string.Join("; ", validation.Errors)}"));

        BackendEntry? replacement = null;
        if (localConnection.Enabled)
        {
            try
            {
                replacement = new BackendEntry(
                    _factories[typeof(LocalConnectionConfig)].Create(
                        id,
                        localConnection,
                        _storageConfig!.MaxBufferedDownloadBytes),
                    ownsBackend: true);
                var health = await replacement.Backend.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                if (health.IsFailure)
                {
                    await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
                    return Result.Failure(health.Error!);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                if (replacement is not null)
                    await replacement.RetireAndDisposeAsync().ConfigureAwait(false);
                return Result.Failure(StorageErrors.ProviderError($"Could not build local storage connection '{id}'.", error.Message));
            }
        }

        BackendEntry? previous = null;
        var gateEntered = false;
        try
        {
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            EnsureOperational();

            var conflictingSection = FindConfiguredNonLocalSection(id);
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
                    await _context!.Configuration.SaveAsync(candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    _context?.Logger.Error($"Failed to persist local storage connection '{id}'.", error);
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

    /// <summary>Removes an effective connection and, for local connections, optionally persists the removal.</summary>
    public async Task<Result> RemoveConnectionAsync(
        string id,
        bool persist = true,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(id);
        EnsureOperational();
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
            exists = exists ||
                TryFindKey(_localConfig!.Connections, id, out _) ||
                TryFindKey(_persistedLocalConfig!.Connections, id, out _);
            if (!exists)
                return Result.Failure(StorageErrors.NotFound($"Storage connection '{id}' is not registered."));

            var effectiveLocal = TryFindKey(_localConfig!.Connections, id, out _);
            var persistedLocal = TryFindKey(_persistedLocalConfig!.Connections, id, out var persistedKey);
            if (persist && (effectiveLocal || persistedLocal))
            {
                var candidate = CloneLocalConfig(_persistedLocalConfig);
                if (persistedKey is not null)
                    candidate.Connections.Remove(persistedKey);
                try
                {
                    await _context!.Configuration.SaveAsync(candidate).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    _context?.Logger.Error($"Failed to persist removal of local storage connection '{id}'.", error);
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
    {
        ValidateConnectionId(id);
        ArgumentNullException.ThrowIfNull(backend);
        EnsureOperational();
        if (!string.Equals(id, backend.ConnectionId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(StorageErrors.Conflict(
                $"Backend connection ID '{backend.ConnectionId}' does not match registry ID '{id}'."));

        var health = CheckRegistrationHealth(id, backend);
        if (health.IsFailure)
            return health;

        BackendEntry? previous = null;
        _mutationGate.Wait();
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

        previous?.RetireAndDisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        return Result.Success();
    }

    private Result CheckRegistrationHealth(string id, IStorageBackend backend)
    {
        var timeoutSeconds = _storageConfig!.HealthCheckTimeoutSeconds;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var result = backend.CheckHealthAsync(timeout.Token)
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds))
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            return result.IsSuccess
                ? Result.Success()
                : Result.Failure(StorageErrors.Unavailable(
                    $"Storage backend '{id}' failed its registration health check.",
                    result.Error?.Code ?? StorageErrors.ProviderErrorCode));
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
            _context?.Logger.Error($"Storage backend '{id}' threw during its registration health check.", error);
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

    internal Task PublishWrittenAsync(IStorageBackend backend, string path) => PublishEventAsync(
        new StorageItemWrittenEvent(backend.ConnectionId, backend.Provider, path, DateTimeOffset.UtcNow));

    internal Task PublishDeletedAsync(IStorageBackend backend, string path) => PublishEventAsync(
        new StorageItemDeletedEvent(backend.ConnectionId, backend.Provider, path, DateTimeOffset.UtcNow));

    internal Task PublishCopiedAsync(IStorageBackend backend, string sourcePath, string destinationPath) => PublishEventAsync(
        new StorageItemCopiedEvent(backend.ConnectionId, backend.Provider, sourcePath, destinationPath, DateTimeOffset.UtcNow));

    internal Task PublishMovedAsync(IStorageBackend backend, string sourcePath, string destinationPath) => PublishEventAsync(
        new StorageItemMovedEvent(backend.ConnectionId, backend.Provider, sourcePath, destinationPath, DateTimeOffset.UtcNow));

    private async Task PublishEventAsync<TEvent>(TEvent @event) where TEvent : IEvent
    {
        try
        {
            await _context!.Events.PublishAsync(@event).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _context?.Logger.Error($"Storage event publication failed for '{typeof(TEvent).Name}'.", error);
        }
    }

    private static async Task<HealthProbe> ProbeHealthAsync(HealthTarget target, int timeoutSeconds)
    {
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                var result = await target.Lease.Backend.CheckHealthAsync(timeout.Token)
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
        }
    }

    private LifecycleState GetLifecycleState()
    {
        lock (_stateGate)
            return _state;
    }

    private string GetDefaultConnectionId()
    {
        EnsureOperational();
        return _storageConfig!.DefaultConnection;
    }

    private void EnsureOperational()
    {
        LifecycleState state;
        lock (_stateGate)
            state = _state;
        if (state is LifecycleState.Initialized or LifecycleState.Started)
            return;
        throw new InvalidOperationException($"Storage library is {DescribeState(state)}; public access requires an initialized library.");
    }

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

    private static void AddRemoteInfos(
        IDictionary<string, StorageConnectionInfo> infos,
        ProviderStorageConfigBase config,
        StorageProvider provider)
    {
        foreach (var (id, connection) in config.Connections)
            infos.Add(id, new StorageConnectionInfo(id, provider, connection.Root, connection.Enabled));
    }

    private static void ValidateConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A storage connection ID is required.", nameof(connectionId));
    }

    private string? FindConfiguredNonLocalSection(string id)
    {
        var configuration = _context!.Configuration;
        if (Contains(configuration.Get<S3StorageConfig>(), id)) return "storage.s3";
        if (Contains(configuration.Get<FtpStorageConfig>(), id)) return "storage.ftp";
        if (Contains(configuration.Get<SftpStorageConfig>(), id)) return "storage.sftp";
        if (Contains(configuration.Get<WebDavStorageConfig>(), id)) return "storage.webdav";
        if (Contains(configuration.Get<AzureStorageConfig>(), id)) return "storage.azure";
        if (Contains(configuration.Get<GoogleCloudStorageConfig>(), id)) return "storage.gcs";
        if (Contains(configuration.Get<SwiftStorageConfig>(), id)) return "storage.swift";
        return null;

        static bool Contains(ProviderStorageConfigBase providerConfig, string connectionId) =>
            providerConfig.Connections.Keys.Any(candidate =>
                string.Equals(candidate, connectionId, StringComparison.OrdinalIgnoreCase));
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
        FollowLinks = source.FollowLinks,
        TimeoutSeconds = source.TimeoutSeconds
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
