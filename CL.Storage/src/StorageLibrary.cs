using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
using CodeLogic.Core.Configuration;
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
    private readonly StorageConnectionObserver _connectionObserver;
    private readonly bool _runtimeOnly;
    private readonly StorageConfig? _runtimeSettings;
    private readonly ConcurrentDictionary<Type, object> _memoryConfig = new();
    private readonly ConcurrentDictionary<string, StorageConnectionHealth> _health = new(StringComparer.OrdinalIgnoreCase);
    private LibraryContext? _context;
    private StorageConfig? _storageConfig;
    private TransferLimits _libraryLimits = TransferLimits.None;
    private LocalStorageConfig? _localConfig;
    private LocalStorageConfig? _persistedLocalConfig;
    private readonly Dictionary<string, LocalConnectionConfig?> _runtimeLocalOverrides = new(StringComparer.OrdinalIgnoreCase);
    private Task? _stopTask;
    private LifecycleState _state;
    private bool _enabled;

    /// <summary>Initializes the storage library with every built-in provider factory.</summary>
    public StorageLibrary() : this(new StorageLibraryOptions())
    {
    }

    /// <summary>Initializes the storage library with every built-in provider factory and the given options.</summary>
    /// <param name="options">Library options, such as <see cref="StorageLibraryOptions.RuntimeOnly"/>.</param>
    public StorageLibrary(StorageLibraryOptions options) : this([
        new LocalStorageBackendFactory(),
        new S3StorageBackendFactory(),
        new FtpStorageBackendFactory(),
        new SftpStorageBackendFactory(),
        new WebDavStorageBackendFactory(),
        new AzureBlobStorageBackendFactory(),
        new GoogleCloudStorageBackendFactory(),
        new SwiftStorageBackendFactory()
    ], null, options)
    { }

    internal StorageLibrary(IEnumerable<IStorageBackendFactory> factories) : this(factories, null) { }

    internal StorageLibrary(
        IEnumerable<IStorageBackendFactory> factories,
        Action? defaultConnectionSnapshotCaptured,
        StorageLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _runtimeOnly = options?.RuntimeOnly ?? false;
        _runtimeSettings = options?.Settings;
        _factories = factories.ToDictionary(factory => factory.ConfigurationType);
        _defaultConnectionSnapshotCaptured = defaultConnectionSnapshotCaptured;
        _connectionObserver = new StorageConnectionObserver(TryCaptureEventPublisher, TryCaptureLogger);
        if (_factories.Count == 0)
            throw new ArgumentException("At least one storage backend factory is required.", nameof(factories));
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

        // Runtime-only libraries never touch configuration files; sections live in memory instead.
        if (_runtimeOnly)
            return Task.CompletedTask;
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

    /// <inheritdoc />
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

        var storage = ReadConfig<StorageConfig>(context);
        var local = ReadConfig<LocalStorageConfig>(context);
        var s3 = ReadConfig<S3StorageConfig>(context);
        var ftp = ReadConfig<FtpStorageConfig>(context);
        var sftp = ReadConfig<SftpStorageConfig>(context);
        var webDav = ReadConfig<WebDavStorageConfig>(context);
        var azure = ReadConfig<AzureStorageConfig>(context);
        var gcs = ReadConfig<GoogleCloudStorageConfig>(context);
        var swift = ReadConfig<SwiftStorageConfig>(context);

        EnsureValid("storage", storage.Validate());
        EnsureValid("storage.local", local.Validate());
        EnsureValid("storage.s3", s3.Validate());
        EnsureValid("storage.ftp", ftp.Validate());
        EnsureValid("storage.sftp", sftp.Validate());
        EnsureValid("storage.webdav", webDav.Validate());
        EnsureValid("storage.azure", azure.Validate());
        EnsureValid("storage.gcs", gcs.Validate());
        EnsureValid("storage.swift", swift.Validate());
        var libraryLimits = StorageTransferPipeline.LibraryLimits(storage.MaxTotalUploadBytesPerSecond, storage.MaxTotalDownloadBytesPerSecond);

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
                    var backend = WithLimits(_factories[typeof(LocalConnectionConfig)].Create(
                        id,
                        configuration,
                        storage.MaxBufferedDownloadBytes), configuration, libraryLimits);
                    builtEntries.Add(id, new BackendEntry(backend, ownsBackend: true));
                }

                AddProviderConnections(builtEntries, infos, s3, StorageProvider.S3, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, ftp, StorageProvider.Ftp, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, sftp, StorageProvider.Sftp, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, webDav, StorageProvider.WebDav, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, azure, StorageProvider.AzureBlob, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, gcs, StorageProvider.GoogleCloudStorage, storage.MaxBufferedDownloadBytes, libraryLimits);
                AddProviderConnections(builtEntries, infos, swift, StorageProvider.OpenStackSwift, storage.MaxBufferedDownloadBytes, libraryLimits);

                if (!_runtimeOnly && !builtEntries.ContainsKey(storage.DefaultConnection))
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
                _libraryLimits = libraryLimits;
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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
                if (!_runtimeOnly && !_registry.ContainsKey(runtime.DefaultConnection))
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
        foreach (var probe in probes)
            RecordHealth(probe.Id, probe.Provider, probe.Healthy, probe.Healthy ? null : probe.Detail, probe.Latency);

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
        var started = Stopwatch.GetTimestamp();
        var result = await CheckConnectionHealthCoreAsync(lease.Backend, linked.Token, timeout, cancellationToken).ConfigureAwait(false);
        RecordHealth(lease.Backend.ConnectionId, lease.Backend.Provider, result.IsSuccess, result.Error?.Code, Stopwatch.GetElapsedTime(started));
        return result;
    }

    private static async Task<Result<HealthStatus>> CheckConnectionHealthCoreAsync(
        IStorageBackend backend,
        CancellationToken probeToken,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var health = await backend.CheckHealthAsync(probeToken).ConfigureAwait(false);
            if (health.IsFailure)
                return Result<HealthStatus>.Failure(health.Error!);
            return Result<HealthStatus>.Success(HealthStatus.Healthy(
                $"Storage connection '{backend.ConnectionId}' is healthy"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{backend.ConnectionId}' timed out during health check."));
        }
        catch (TimeoutException)
        {
            return Result<HealthStatus>.Failure(StorageErrors.Timeout(
                $"Storage connection '{backend.ConnectionId}' timed out during health check."));
        }
        catch (Exception)
        {
            return Result<HealthStatus>.Failure(StorageErrors.ProviderError(
                $"Storage connection '{backend.ConnectionId}' failed its health check."));
        }
    }

    /// <summary>Stores a health result and publishes <see cref="StorageConnectionHealthChangedEvent"/> when the state flips.</summary>
    private void RecordHealth(string id, StorageProvider provider, bool healthy, string? errorCode, TimeSpan latency)
    {
        var current = new StorageConnectionHealth(healthy, healthy ? null : errorCode, latency, DateTimeOffset.UtcNow);
        StorageConnectionHealth? previous = null;
        _health.AddOrUpdate(id, current, (_, existing) =>
        {
            previous = existing;
            return current;
        });
        if (previous?.Healthy != healthy)
            _connectionObserver.HealthChanged(id, provider, previous, current);
    }

    /// <summary>
    /// Describes a registered connection and the server behind it: address, transport security, the
    /// certificate or host key presented, negotiated algorithms, server software and features, session
    /// pool counters, and the last health check. Opens a session when the provider needs one to answer.
    /// </summary>
    /// <param name="connectionId">Connection to describe.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The diagnostics, or the error from opening a session.</returns>
    public async Task<Result<StorageConnectionDiagnostics>> GetConnectionDiagnosticsAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionId(connectionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = AcquireOperation(connectionId);
        StorageConnectionInfo? info;
        lock (_registryGate)
            _connectionInfos.TryGetValue(lease.Backend.ConnectionId, out info);
        _health.TryGetValue(lease.Backend.ConnectionId, out var health);
        return await DiagnoseAsync(lease.Backend, info?.Host, info?.Port, info?.Security ?? StorageTransportSecurity.Unknown, health, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Result<StorageConnectionDiagnostics>> DiagnoseAsync(
        IStorageBackend backend,
        string? host,
        int? port,
        StorageTransportSecurity security,
        StorageConnectionHealth? health,
        CancellationToken cancellationToken)
    {
        var diagnostics = new StorageConnectionDiagnostics
        {
            ConnectionId = backend.ConnectionId,
            Provider = backend.Provider,
            Host = host,
            Port = port,
            Security = security,
            LastHealth = health
        };
        if (backend is not IStorageDiagnosticsSource source)
            return Result<StorageConnectionDiagnostics>.Success(diagnostics);
        var details = await source.GetServerDetailsAsync(cancellationToken).ConfigureAwait(false);
        if (details.IsFailure)
            return Result<StorageConnectionDiagnostics>.Failure(details.Error!);
        return Result<StorageConnectionDiagnostics>.Success(diagnostics with
        {
            ServerSystem = details.Value!.System,
            ServerSoftware = details.Value.Software,
            ServerFeatures = details.Value.Features,
            Negotiated = details.Value.Negotiated,
            ServerIdentity = source.PresentedIdentity,
            Pool = source.PoolStats
        });
    }

    /// <summary>
    /// Tests a connection configuration without saving or registering it: validates it, connects and
    /// authenticates, lists the root, and reads server details. Works before the library is initialized.
    /// </summary>
    /// <remarks>
    /// When the server's certificate or host key is rejected, <see cref="StorageConnectionTestReport.ServerIdentity"/>
    /// still carries what it presented, so a setup screen can ask "the server presented this fingerprint; trust it?".
    /// </remarks>
    /// <param name="connection">Provider connection settings, such as <see cref="SftpConnectionConfig"/>.</param>
    /// <param name="cancellationToken">Token used to cancel the test.</param>
    /// <returns>The steps that ran and what they found.</returns>
    public Task<StorageConnectionTestReport> TestConnectionAsync(
        StorageConnectionConfigBase connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var (host, port, security) = StorageEndpoints.Describe(connection);
        return TestConnectionCoreAsync(
            connection.GetValidationErrors().ToArray(),
            connection.GetType(),
            () => _factories[connection.GetType()].Create("connection-test", CloneProviderConnection(connection), DefaultTestBufferBytes),
            host,
            port,
            security,
            cancellationToken);
    }

    /// <summary>Tests local folder settings without saving or registering them.</summary>
    /// <param name="connection">Local folder settings.</param>
    /// <param name="cancellationToken">Token used to cancel the test.</param>
    /// <returns>The steps that ran and what they found.</returns>
    public Task<StorageConnectionTestReport> TestConnectionAsync(
        LocalConnectionConfig connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return TestConnectionCoreAsync(
            [.. connection.Validate().Errors],
            typeof(LocalConnectionConfig),
            () => _factories[typeof(LocalConnectionConfig)].Create("connection-test", connection, DefaultTestBufferBytes),
            null,
            null,
            StorageTransportSecurity.Unknown,
            cancellationToken);
    }

    private const long DefaultTestBufferBytes = 1_048_576;

    private async Task<StorageConnectionTestReport> TestConnectionCoreAsync(
        string[] validationErrors,
        Type configurationType,
        Func<IStorageBackend> create,
        string? host,
        int? port,
        StorageTransportSecurity security,
        CancellationToken cancellationToken)
    {
        var steps = new List<StorageConnectionTestStep>();
        StorageConnectionTestReport Fail(Error error, IStorageBackend? backend) => new(
            false, steps, null, (backend as IStorageDiagnosticsSource)?.PresentedIdentity, error);

        var started = Stopwatch.GetTimestamp();
        if (validationErrors.Length > 0 || !_factories.ContainsKey(configurationType))
        {
            var invalid = validationErrors.Length > 0
                ? StorageErrors.InvalidContent($"The connection settings are invalid: {string.Join("; ", validationErrors)}")
                : StorageErrors.Unsupported($"Connection configuration type '{configurationType.Name}' does not have a provider factory.");
            steps.Add(new StorageConnectionTestStep("validate", false, Stopwatch.GetElapsedTime(started), invalid));
            return Fail(invalid, null);
        }

        IStorageBackend backend;
        try
        {
            backend = create();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var invalid = StorageErrors.InvalidContent($"The connection settings could not be applied: {error.Message}");
            steps.Add(new StorageConnectionTestStep("validate", false, Stopwatch.GetElapsedTime(started), invalid));
            return Fail(invalid, null);
        }
        steps.Add(new StorageConnectionTestStep("validate", true, Stopwatch.GetElapsedTime(started), null));

        await using (backend.ConfigureAwait(false))
        {
            async Task<Result<T>> Step<T>(string name, Func<Task<Result<T>>> run)
            {
                var stepStarted = Stopwatch.GetTimestamp();
                Result<T> result;
                try
                {
                    result = await run().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    result = Result<T>.Failure(ProviderErrorMapper.FromTransport(error, $"Connection test '{name}'", "server")
                        ?? StorageErrors.ProviderError($"Connection test step '{name}' failed: {error.Message}"));
                }
                steps.Add(new StorageConnectionTestStep(name, result.IsSuccess, Stopwatch.GetElapsedTime(stepStarted), result.Error));
                return result;
            }

            var connected = await Step("connect", async () =>
            {
                var health = await backend.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                return health.IsSuccess ? Result<bool>.Success(true) : Result<bool>.Failure(health.Error!);
            }).ConfigureAwait(false);
            if (connected.IsFailure) return Fail(connected.Error!, backend);

            var listed = await Step("list", () => backend.ListAsync(string.Empty, new StorageListOptions { PageSize = 1 }, cancellationToken))
                .ConfigureAwait(false);
            if (listed.IsFailure) return Fail(listed.Error!, backend);

            var described = await Step("details", () => DiagnoseAsync(backend, host, port, security, null, cancellationToken))
                .ConfigureAwait(false);
            // Server details are informational: a server that refuses OPTIONS is still usable.
            var source = backend as IStorageDiagnosticsSource;
            return new StorageConnectionTestReport(
                true,
                steps,
                described.IsSuccess ? described.Value : null,
                source?.PresentedIdentity,
                null);
        }
    }

    /// <summary>
    /// Copies a file or directory between mounted connections. Cross-connection transfers use a
    /// bounded streaming relay and a unique destination staging object before final commit.
    /// </summary>
    /// <returns>
    /// What happened: committed or skipped (and why), the digest and new destination identity, and, when it
    /// did not finish, exactly what state it left and a token to resume it.
    /// </returns>
    public Task<StorageTransferReport> CopyAsync(
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
                copied.Value.Bytes,
                copied.Value.SkippedFiles);
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
                copied.Value.Bytes,
                copied.Value.SkippedFiles);
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
    /// complete destination tree has committed successfully, and a single file only while it is still the
    /// version that was copied.
    /// </summary>
    /// <returns>What happened, including whether the source was deleted.</returns>
    public Task<StorageTransferReport> MoveAsync(
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

    private async Task<StorageTransferReport> TransferAsync(
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
        var report = new StorageTransferReport { Outcome = StorageTransferOutcome.Failed, SourcePath = sourcePath, DestinationPath = destinationPath };
        StorageTransferReport Fail(Error error) => report with { Outcome = StorageTransferOutcome.Failed, Error = error };

        var optionsValidation = options.Validate();
        if (optionsValidation.IsFailure)
            return Fail(optionsValidation.Error!);

        var normalizedSource = NormalizeTransferPath(sourcePath, "source");
        if (normalizedSource.IsFailure)
            return Fail(normalizedSource.Error!);
        var normalizedDestination = NormalizeTransferPath(destinationPath, "destination");
        if (normalizedDestination.IsFailure)
            return Fail(normalizedDestination.Error!);
        report = report with { SourcePath = normalizedSource.Value!, DestinationPath = normalizedDestination.Value! };

        var destinationTarget = normalizedDestination.Value!;
        BackendEntry.BackendOperationLease? sourceLease = null;
        BackendEntry.BackendOperationLease? destinationLease = null;
        StorageEventPublisher? publisher = null;
        StorageTransferSummary? summary = null;
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
            var perFileDecisions = false;

            // Conditional conflict policies decide per file. A single file is decided here, so the
            // native operation can still run; a directory must relay so every file is decided on its own.
            // Resume is decided by the coordinator, which owns the resumable staging.
            if (StorageConflictResolver.IsConditional(options.ConflictPolicy) && options.ConflictPolicy != StorageConflictPolicy.Resume)
            {
                var info = await sourceBackend.GetInfoAsync(normalizedSource.Value!, cancellationToken).ConfigureAwait(false);
                if (info.IsFailure)
                    return Fail(info.Error!);
                if (info.Value!.ItemType == StorageItemType.File)
                {
                    var decision = await StorageConflictResolver.ResolveAsync(
                        destinationBackend,
                        destinationTarget,
                        options.ConflictPolicy,
                        options.Overwrite,
                        info.Value.Size,
                        info.Value.LastModified,
                        cancellationToken).ConfigureAwait(false);
                    if (decision.IsFailure)
                        return Fail(decision.Error!);
                    if (decision.Value.Skip)
                    {
                        return report with
                        {
                            Outcome = StorageTransferOutcome.Skipped,
                            SourceType = StorageItemType.File,
                            SkippedFiles = 1,
                            SkipReason = StorageConflictResolver.SkipReasonFor(options.ConflictPolicy!.Value),
                            DestinationETag = decision.Value.Existing?.ETag,
                            DestinationVersionId = decision.Value.Existing?.VersionId,
                            SourceDeleted = move ? false : null
                        };
                    }
                    destinationTarget = decision.Value.Path;
                    options = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
                }
                else
                {
                    perFileDecisions = true;
                }
            }

            // Guarantees (conditions, pinning, verification, resume) need the staged relay, not a native call.
            if (sameBackend && !perFileDecisions && !options.RequiresGuarantees)
            {
                var relationship = StorageTransferPath.ValidateDistinct(
                    normalizedSource.Value!,
                    destinationTarget);
                if (relationship.IsFailure)
                    return Fail(relationship.Error!);

                var sourceInfo = await sourceBackend.GetInfoAsync(
                    normalizedSource.Value!,
                    cancellationToken).ConfigureAwait(false);
                if (sourceInfo.IsFailure)
                    return Fail(sourceInfo.Error!);
                if (sourceInfo.Value!.ItemType == StorageItemType.Directory)
                {
                    relationship = StorageTransferPath.ValidateDirectoryDestination(
                        normalizedSource.Value!,
                        destinationTarget);
                    if (relationship.IsFailure)
                        return Fail(relationship.Error!);
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
                    if (options.PhaseChanged is { } committing)
                        await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
                    // A server-side operation moves no bytes through the client: report its start and end.
                    var total = sourceInfo.Value.ItemType == StorageItemType.File ? sourceInfo.Value.Size : null;
                    options.Progress?.Report(new StorageTransferProgress(0, total, false, ItemPath: normalizedSource.Value));
                    var native = move
                        ? await sourceBackend.MoveAsync(normalizedSource.Value!, destinationTarget, options, cancellationToken).ConfigureAwait(false)
                        : await sourceBackend.CopyAsync(normalizedSource.Value!, destinationTarget, options, cancellationToken).ConfigureAwait(false);
                    if (native.IsFailure)
                        return Fail(native.Error!);
                    options.Progress?.Report(new StorageTransferProgress(total ?? 0, total ?? 0, true, ItemPath: normalizedSource.Value));
                    var committed = sourceInfo.Value.ItemType == StorageItemType.File
                        ? await destinationBackend.GetInfoAsync(destinationTarget, cancellationToken).ConfigureAwait(false)
                        : Result<StorageItem>.Failure(StorageErrors.NotFound("not a file"));
                    report = report with
                    {
                        Outcome = StorageTransferOutcome.Completed,
                        SourceType = sourceInfo.Value.ItemType,
                        WrittenPath = destinationTarget,
                        Files = sourceInfo.Value.ItemType == StorageItemType.File ? 1 : 0,
                        Directories = sourceInfo.Value.ItemType == StorageItemType.Directory ? 1 : 0,
                        Bytes = sourceInfo.Value.Size ?? 0,
                        DestinationCommitted = true,
                        SourceDeleted = move ? true : null,
                        DestinationETag = committed.IsSuccess ? committed.Value!.ETag : null,
                        DestinationVersionId = committed.IsSuccess ? committed.Value!.VersionId : null,
                        ConditionEnforcement = !options.Overwrite
                            ? sourceBackend.Capabilities.Supports(StorageFeature.ConditionalCreate) ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.CheckedBeforeCommit
                            : StorageConditionEnforcement.None
                    };
                    publisher = CaptureEventPublisher();
                    usedNativeOperation = true;
                }
            }

            if (!usedNativeOperation)
            {
                var state = new TransferState();
                var copied = await StorageTransferCoordinator.CopyAsync(
                    sourceBackend,
                    normalizedSource.Value!,
                    destinationBackend,
                    destinationTarget,
                    options,
                    cancellationToken,
                    state).ConfigureAwait(false);
                if (copied.IsFailure)
                    return FailedTransferReport(report, copied.Error!, state, move);
                summary = copied.Value!;
                report = SummaryReport(report, summary, move);
                // A resumed move whose destination already holds the content (for example after a crash between
                // commit and source deletion) still has to delete its source; any other skip leaves it.
                var alreadyMoved = move && summary.SkipReason == StorageSkipReason.AlreadyComplete;
                if (summary.SkippedFiles > 0 && summary.Files == 0 && summary.SourceType == StorageItemType.File && !alreadyMoved)
                    return report with { Outcome = StorageTransferOutcome.Skipped, SourceDeleted = move ? false : null };

                if (move && options.PhaseChanged is { } deleting)
                    await deleting(Queue.StorageTransferPhase.DeletingSource, cancellationToken).ConfigureAwait(false);
                if (move && summary.SourceType == StorageItemType.Directory && LeavesSourcesBehind(options, summary))
                {
                    var removed = await DeleteTransferredSourcesAsync(sourceBackend, normalizedSource.Value!, summary, cancellationToken).ConfigureAwait(false);
                    if (removed.IsFailure)
                        return report with { Outcome = StorageTransferOutcome.NeedsReconciliation, Error = removed.Error, SourceDeleted = false };
                }
                else if (move)
                {
                    var deleted = await DeleteMovedSourceAsync(sourceBackend, normalizedSource.Value!, summary, state.Source, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsFailure)
                        return report with
                        {
                            Outcome = StorageTransferOutcome.NeedsReconciliation,
                            SourceDeleted = false,
                            Error = StorageErrors.PartialFailure(
                                "The destination completed, but the source could not be deleted.",
                                $"sourceDeleteError={deleted.Error!.Code};destinationState=complete")
                        };
                }

                report = report with { Outcome = StorageTransferOutcome.Completed, SourceDeleted = move ? true : null };
                publisher = CaptureEventPublisher();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Reported rather than thrown, so the caller learns what the transfer left behind.
            return report with
            {
                Outcome = report.DestinationCommitted == true ? StorageTransferOutcome.NeedsReconciliation : StorageTransferOutcome.Cancelled,
                Error = StorageErrors.Cancelled("The transfer was cancelled."),
                SourceDeleted = move ? false : null
            };
        }
        finally
        {
            destinationLease?.Dispose();
            sourceLease?.Dispose();
        }

        await PublishTransferResultAsync(
            Result.Success(),
            publisher,
            sameBackend,
            move,
            effectiveSourceId!,
            sourceProvider,
            report.SourcePath,
            effectiveDestinationId!,
            destinationProvider,
            report.WrittenPath ?? destinationTarget,
            summary ?? new StorageTransferSummary(report.SourceType ?? StorageItemType.File, report.Files, report.Directories, report.Bytes)).ConfigureAwait(false);
        return report;
    }

    /// <summary>Folds a coordinator summary into the report.</summary>
    private static StorageTransferReport SummaryReport(StorageTransferReport report, StorageTransferSummary summary, bool move) => report with
    {
        SourceType = summary.SourceType,
        Files = summary.Files,
        Directories = summary.Directories,
        Bytes = summary.Bytes,
        BytesResumed = summary.BytesResumed,
        SkippedFiles = summary.SkippedFiles,
        WrittenPath = summary.WrittenPath ?? (summary.SourceType == StorageItemType.File ? null : report.DestinationPath),
        SkipReason = summary.SkipReason,
        Sha256 = summary.Sha256,
        VerifiedBy = summary.VerifiedBy,
        DestinationETag = summary.Destination?.ETag,
        DestinationVersionId = summary.Destination?.VersionId,
        DestinationCommitted = summary.Files > 0 || summary.Directories > 0,
        ConditionEnforcement = summary.ConditionEnforcement,
        SourceDeleted = move ? false : null
    };

    /// <summary>Builds the report for a coordinator failure from what it recorded before stopping.</summary>
    private static StorageTransferReport FailedTransferReport(StorageTransferReport report, Error error, TransferState state, bool move)
    {
        var mixed = error.Code == StorageErrors.PartialFailureCode || state.RollbackIncomplete || state.BackupLeftBehind is not null ||
            (state.StagingLeftBehind is not null && !state.StagingResumable);
        StorageResumeToken? token = null;
        if (state.StagingResumable && state.StagingLeftBehind is { } staging)
        {
            token = new StorageResumeToken
            {
                DestinationPath = report.DestinationPath,
                StagingPath = staging,
                BytesStaged = state.BytesStaged,
                SourcePath = state.Source?.Path,
                SourceETag = state.Source?.ETag,
                SourceVersionId = state.Source?.VersionId,
                SourceLength = state.Source?.Size,
                SourceLastModified = state.Source?.LastModified
            };
        }
        return report with
        {
            Outcome = mixed ? StorageTransferOutcome.NeedsReconciliation
                : error.Code == StorageErrors.CancelledCode ? StorageTransferOutcome.Cancelled
                : StorageTransferOutcome.Failed,
            Error = error,
            SourceType = state.Source?.ItemType,
            DestinationCommitted = state.DestinationCommitted,
            SourceDeleted = move ? false : null,
            StagingLeftBehind = state.StagingLeftBehind,
            BackupRestored = state.BackupRestored,
            BackupLeftBehind = state.BackupLeftBehind,
            ConditionEnforcement = state.ConditionEnforcement,
            ResumeToken = token
        };
    }

    /// <summary>
    /// Deletes a moved source only while it is still the version that was copied: atomically where the
    /// source supports conditional deletes, otherwise by comparing its identity immediately before.
    /// </summary>
    private static async Task<Result> DeleteMovedSourceAsync(
        IStorageBackend source,
        string path,
        StorageTransferSummary summary,
        StorageItem? copied,
        CancellationToken cancellationToken)
    {
        var directory = summary.SourceType == StorageItemType.Directory;
        StorageMutationCondition? condition = null;
        if (!directory && copied is not null)
        {
            if (source.Capabilities.Supports(StorageFeature.ConditionalDelete) && (copied.ETag is not null || copied.VersionId is not null))
            {
                condition = new StorageMutationCondition { ExpectedETag = copied.ETag, ExpectedVersionId = copied.VersionId };
            }
            else
            {
                var current = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
                if (current.IsFailure)
                    return Result.Failure(current.Error!);
                if (current.Value!.Size != copied.Size || current.Value.LastModified != copied.LastModified ||
                    (copied.VersionId is not null && copied.VersionId != current.Value.VersionId) ||
                    (copied.ETag is not null && !StagedWriter.SameETag(copied.ETag, current.Value.ETag)))
                    return Result.Failure(StorageErrors.Conflict($"The source '{path}' changed after it was copied, so it was not deleted."));
            }
        }
        return await source.DeleteAsync(
            path,
            new StorageDeleteOptions { Recursive = directory, IgnoreMissing = false, Condition = condition },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a directory move must keep some source items: skipped files or skipped links.</summary>
    private static bool LeavesSourcesBehind(StorageTransferOptions options, StorageTransferSummary summary) =>
        summary.SkippedFiles > 0 || options.LinkHandling == StorageLinkHandling.Skip;

    /// <summary>
    /// Completes a partial directory move: deletes only the source files that were transferred, then
    /// removes directories left empty, deepest first. Skipped files and their directories stay in place.
    /// </summary>
    private static async Task<Result> DeleteTransferredSourcesAsync(
        IStorageBackend source,
        string sourceRoot,
        StorageTransferSummary summary,
        CancellationToken cancellationToken)
    {
        foreach (var path in summary.TransferredSources ?? [])
        {
            var deleted = await source.DeleteAsync(path, new StorageDeleteOptions { IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
            if (deleted.IsFailure)
                return Result.Failure(StorageErrors.PartialFailure(
                    "The destination completed, but a transferred source file could not be deleted.",
                    $"sourceDeleteError={deleted.Error!.Code};destinationState=complete"));
        }

        var directories = new List<string> { sourceRoot };
        await foreach (var item in source.EnumerateItemsAsync(sourceRoot, new StorageListOptions { Recursive = true }, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsSuccess && item.Value!.ItemType == StorageItemType.Directory)
                directories.Add(item.Value.Path);
        }
        foreach (var directory in directories.OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length))
        {
            // Non-recursive: a directory that still holds skipped items fails with a conflict and stays.
            _ = await source.DeleteAsync(directory, new StorageDeleteOptions { IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
        }
        return Result.Success();
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

    /// <summary>Compares two directory trees on (possibly different) connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Comparison criteria.</param>
    /// <param name="cancellationToken">Token used to cancel the comparison.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public Task<Result<Sync.StorageDiff>> CompareAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.CompareAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Synchronizes two directory trees on (possibly different) connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Direction, deletes, dry run, and comparison settings.</param>
    /// <param name="cancellationToken">Token used to cancel the sync.</param>
    /// <returns>The steps taken, or planned for a dry run.</returns>
    public Task<Result<Sync.StorageSyncReport>> SyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.SyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Plans a sync between two connections without changing anything; approve the plan by its digest.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
    /// <param name="cancellationToken">Token used to cancel the listings.</param>
    /// <returns>The plan.</returns>
    public Task<Result<Sync.StorageSyncPlan>> PlanSyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.PlanSyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, options, cancellationToken);

    /// <summary>Applies an approved sync plan between two connections.</summary>
    /// <param name="sourceConnectionId">Source connection.</param>
    /// <param name="sourcePath">Source directory; must be the plan's.</param>
    /// <param name="destinationConnectionId">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; must be the plan's.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="approvedDigest">The digest that was approved.</param>
    /// <param name="options">The options the plan was made with.</param>
    /// <param name="cancellationToken">Stops the run; the report keeps what was done.</param>
    /// <returns>Each step's outcome.</returns>
    public Task<Result<Sync.StorageSyncReport>> ApplySyncAsync(
        string sourceConnectionId,
        string sourcePath,
        string destinationConnectionId,
        string destinationPath,
        Sync.StorageSyncPlan plan,
        string approvedDigest,
        Sync.StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Sync.StorageSync.ApplySyncAsync(GetStorage(sourceConnectionId), sourcePath, GetStorage(destinationConnectionId), destinationPath, plan, approvedDigest, options, cancellationToken);

    /// <summary>
    /// Opens a background transfer queue. Its jobs live in <see cref="Queue.StorageTransferQueueOptions.Store"/>
    /// (in memory by default); a durable store brings back the jobs of an earlier run, with those left
    /// running either queued again (when their destination was never touched) or marked interrupted.
    /// </summary>
    /// <param name="options">Limits, retries, and the job store.</param>
    /// <param name="cancellationToken">Token used to cancel loading the store.</param>
    /// <returns>The queue, or why the options are invalid.</returns>
    public Task<Result<Queue.StorageTransferQueue>> OpenTransferQueueAsync(
        Queue.StorageTransferQueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOperational();
        return Queue.StorageTransferQueue.OpenAsync(this, options ?? new Queue.StorageTransferQueueOptions(), cancellationToken);
    }

    /// <summary>Returns an immutable snapshot containing sanitized connection information.</summary>
    public IReadOnlyList<StorageConnectionInfo> GetConnections()
    {
        EnsureOperational();
        lock (_registryGate)
        {
            var snapshot = _connectionInfos.Values
                .Select(connection => _health.TryGetValue(connection.Id, out var health) ? connection with { LastHealth = health } : connection)
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
                    await SaveConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
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
                _health.TryRemove(id, out _);
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
                _connectionInfos[id] = DescribeConnection(id, descriptor.Value.Provider, effectiveConnection);
                _health.TryRemove(id, out _);
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
                WithLimits(_factories[configuration.GetType()].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes,
                    _connectionObserver), configuration, _libraryLimits),
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
                WithLimits(_factories[typeof(LocalConnectionConfig)].Create(
                    id,
                    configuration,
                    runtime.MaxBufferedDownloadBytes), configuration, _libraryLimits),
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
                    await SaveConfigAsync(runtime.Context, candidate).ConfigureAwait(false);
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
                _health.TryRemove(id, out _);
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    private StorageEventPublisher? TryCaptureEventPublisher()
    {
        lock (_stateGate)
            return _context is { } context ? new StorageEventPublisher(context.Events, context.Logger) : null;
    }

    private ILogger? TryCaptureLogger()
    {
        lock (_stateGate)
            return _context?.Logger;
    }

    internal StorageConnectionObserver ConnectionObserver => _connectionObserver;

    /// <summary>Gets whether the library keeps its configuration in memory only.</summary>
    public bool RuntimeOnly => _runtimeOnly;

    /// <summary>Reads a configuration section, from memory in runtime-only mode.</summary>
    private T ReadConfig<T>(LibraryContext context) where T : ConfigModelBase, new() =>
        _runtimeOnly
            ? (T)_memoryConfig.GetOrAdd(typeof(T), _ => typeof(T) == typeof(StorageConfig) && _runtimeSettings is not null ? _runtimeSettings : new T())
            : context.Configuration.Get<T>();

    /// <summary>Saves a configuration section, to memory in runtime-only mode.</summary>
    private Task SaveConfigAsync<T>(LibraryContext context, T value) where T : ConfigModelBase, new()
    {
        if (!_runtimeOnly) return context.Configuration.SaveAsync(value);
        _memoryConfig[typeof(T)] = value;
        return Task.CompletedTask;
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
        var provider = target.Lease.Backend.Provider;
        var started = Stopwatch.GetTimestamp();
        HealthProbe Probe(bool healthy, string detail) =>
            new(target.Id, healthy, detail) { Provider = provider, Latency = Stopwatch.GetElapsedTime(started) };
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            try
            {
                probeTask = target.Lease.Backend.CheckHealthAsync(timeout.Token);
                var result = await probeTask
                    .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds))
                    .ConfigureAwait(false);
                return result.IsSuccess
                    ? Probe(true, string.Empty)
                    : Probe(false, result.Error?.Code ?? "storage.provider_error");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Probe(false, "storage.timeout");
            }
            catch (TimeoutException)
            {
                timeout.Cancel();
                return Probe(false, "storage.timeout");
            }
            catch (Exception)
            {
                return Probe(false, "storage.provider_error");
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

    private ProviderStorageConfigBase GetProviderConfig(LibraryContext context, Type connectionType)
    {
        if (connectionType == typeof(S3ConnectionConfig)) return ReadConfig<S3StorageConfig>(context);
        if (connectionType == typeof(FtpConnectionConfig)) return ReadConfig<FtpStorageConfig>(context);
        if (connectionType == typeof(SftpConnectionConfig)) return ReadConfig<SftpStorageConfig>(context);
        if (connectionType == typeof(WebDavConnectionConfig)) return ReadConfig<WebDavStorageConfig>(context);
        if (connectionType == typeof(AzureBlobConnectionConfig)) return ReadConfig<AzureStorageConfig>(context);
        if (connectionType == typeof(GoogleCloudConnectionConfig)) return ReadConfig<GoogleCloudStorageConfig>(context);
        if (connectionType == typeof(SwiftConnectionConfig)) return ReadConfig<SwiftStorageConfig>(context);
        throw new NotSupportedException($"Provider connection type '{connectionType.FullName}' is not supported.");
    }

    private Task SaveProviderConfigAsync(LibraryContext context, ProviderStorageConfigBase config) => config switch
    {
        S3StorageConfig value => SaveConfigAsync(context, value),
        FtpStorageConfig value => SaveConfigAsync(context, value),
        SftpStorageConfig value => SaveConfigAsync(context, value),
        WebDavStorageConfig value => SaveConfigAsync(context, value),
        AzureStorageConfig value => SaveConfigAsync(context, value),
        GoogleCloudStorageConfig value => SaveConfigAsync(context, value),
        SwiftStorageConfig value => SaveConfigAsync(context, value),
        _ => throw new NotSupportedException($"Provider configuration type '{config.GetType().FullName}' is not supported.")
    };

    private string? FindConfiguredSection(
        string id,
        LibraryContext context,
        Type? exceptConnectionType = null)
    {
        if (exceptConnectionType != typeof(LocalConnectionConfig) &&
            TryFindKey(ReadConfig<LocalStorageConfig>(context).Connections, id, out _))
            return "storage.local";
        if (exceptConnectionType != typeof(S3ConnectionConfig) && ReadConfig<S3StorageConfig>(context).ContainsConnection(id))
            return "storage.s3";
        if (exceptConnectionType != typeof(FtpConnectionConfig) && ReadConfig<FtpStorageConfig>(context).ContainsConnection(id))
            return "storage.ftp";
        if (exceptConnectionType != typeof(SftpConnectionConfig) && ReadConfig<SftpStorageConfig>(context).ContainsConnection(id))
            return "storage.sftp";
        if (exceptConnectionType != typeof(WebDavConnectionConfig) && ReadConfig<WebDavStorageConfig>(context).ContainsConnection(id))
            return "storage.webdav";
        if (exceptConnectionType != typeof(AzureBlobConnectionConfig) && ReadConfig<AzureStorageConfig>(context).ContainsConnection(id))
            return "storage.azure";
        if (exceptConnectionType != typeof(GoogleCloudConnectionConfig) && ReadConfig<GoogleCloudStorageConfig>(context).ContainsConnection(id))
            return "storage.gcs";
        if (exceptConnectionType != typeof(SwiftConnectionConfig) && ReadConfig<SwiftStorageConfig>(context).ContainsConnection(id))
            return "storage.swift";
        return null;
    }

    private ProviderConfigMatch? FindProviderConfigContaining(string id, LibraryContext context)
    {
        ProviderStorageConfigBase[] configs =
        [
            ReadConfig<S3StorageConfig>(context),
            ReadConfig<FtpStorageConfig>(context),
            ReadConfig<SftpStorageConfig>(context),
            ReadConfig<WebDavStorageConfig>(context),
            ReadConfig<AzureStorageConfig>(context),
            ReadConfig<GoogleCloudStorageConfig>(context),
            ReadConfig<SwiftStorageConfig>(context)
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
        long maxBufferedDownloadBytes,
        TransferLimits libraryLimits)
        where TConnection : StorageConnectionConfigBase
    {
        _factories.TryGetValue(typeof(TConnection), out var factory);
        foreach (var (id, connection) in config.Connections)
        {
            infos.Add(id, DescribeConnection(id, provider, connection));
            if (!connection.Enabled)
                continue;
            if (factory is null)
                throw new InvalidOperationException($"The {provider} provider factory is not registered.");
            entries.Add(id, new BackendEntry(
                WithLimits(factory.Create(id, connection, maxBufferedDownloadBytes, _connectionObserver), connection, libraryLimits),
                ownsBackend: true));
        }
    }

    private static StorageConnectionInfo DescribeConnection(string id, StorageProvider provider, StorageConnectionConfigBase connection)
    {
        var (host, port, security) = StorageEndpoints.Describe(connection);
        return new StorageConnectionInfo(id, provider, connection.MountRoot, connection.Enabled)
        {
            Host = host,
            Port = port,
            Security = security
        };
    }

    /// <summary>Attaches a connection's speed limits, plus the library-wide totals, to its backend.</summary>
    private static IStorageBackend WithLimits(IStorageBackend backend, StorageConnectionConfigBase configuration, TransferLimits libraryLimits) =>
        WithLimits(backend, configuration.TransferLimits, libraryLimits);

    private static IStorageBackend WithLimits(IStorageBackend backend, LocalConnectionConfig configuration, TransferLimits libraryLimits) =>
        WithLimits(backend, configuration.TransferLimits, libraryLimits);

    private static IStorageBackend WithLimits(IStorageBackend backend, StorageTransferLimitsConfig? configured, TransferLimits libraryLimits)
    {
        var limits = configured ?? new StorageTransferLimitsConfig();
        StorageTransferPipeline.SetLimits(backend, limits.MaxUploadBytesPerSecond, limits.MaxDownloadBytesPerSecond, libraryLimits);
        return backend;
    }

    private static void ValidateConnectionId(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A storage connection ID is required.", nameof(connectionId));
    }

    private string? FindConfiguredNonLocalSection(string id, LibraryContext context)
    {
        if (Contains(ReadConfig<S3StorageConfig>(context), id)) return "storage.s3";
        if (Contains(ReadConfig<FtpStorageConfig>(context), id)) return "storage.ftp";
        if (Contains(ReadConfig<SftpStorageConfig>(context), id)) return "storage.sftp";
        if (Contains(ReadConfig<WebDavStorageConfig>(context), id)) return "storage.webdav";
        if (Contains(ReadConfig<AzureStorageConfig>(context), id)) return "storage.azure";
        if (Contains(ReadConfig<GoogleCloudStorageConfig>(context), id)) return "storage.gcs";
        if (Contains(ReadConfig<SwiftStorageConfig>(context), id)) return "storage.swift";
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

    private sealed record HealthProbe(string Id, bool Healthy, string Detail)
    {
        public StorageProvider Provider { get; init; }
        public TimeSpan Latency { get; init; }
    }
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
