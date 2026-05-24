using CL.MySQL2.Configuration;
using CL.MySQL2.Events;
using CL.MySQL2.Localization;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.MySQL2;

/// <summary>
/// <b>CL.MySQL2</b> — CodeLogic library providing MySQL database access with a fluent
/// LINQ query builder, automatic table synchronization, migrations, schema backups,
/// and multiple named database connections.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><description><b>Configure</b> — Registers <see cref="DatabaseConfiguration"/> and <see cref="MySQL2Strings"/>.</description></item>
///   <item><description><b>Initialize</b> — Loads config, validates, creates <see cref="ConnectionManager"/> and <see cref="TableSyncService"/>. Tests the connection.</description></item>
///   <item><description><b>Start</b> — Runs a health check and logs server version.</description></item>
///   <item><description><b>Stop</b> — Closes connections and disposes resources.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class MySQL2Library : ILibrary
{
    /// <inheritdoc/>
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.MySQL2",
        Name = "MySQL2 Library",
        Version = CL.Internal.InternalLibraryVersion.Current,
        Description = "MySQL with typed LINQ, SQL aggregation, projection pushdown, compiled materializers, covering indexes, retention, and a working cache.",
        Author = "Media2A",
        Tags = ["mysql", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private MySQL2Strings? _strings;
    private bool _isEnabled;
    private RetentionWorker? _retentionWorker;
    private readonly HashSet<Type> _registeredEntities = new();

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<DatabaseConfiguration>("mysql");
        context.Configuration.Register<CacheConfiguration>("mysql.cache");
        context.Localization.Register<MySQL2Strings>();

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        var config = context.Configuration.Get<DatabaseConfiguration>();
        _strings = context.Localization.Get<MySQL2Strings>();

        var enabledDbs = config.Databases
            .Where(kvp => kvp.Value.Enabled)
            .ToList();

        if (enabledDbs.Count == 0)
        {
            _isEnabled = false;
            context.Logger.Warning($"{Manifest.Name} has no enabled databases — skipping initialization.");
            return;
        }

        _isEnabled = true;

        // Validate configuration
        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException(
                $"{Manifest.Name} configuration is invalid: {errors}");
        }

        // Create services
        _connectionManager = new ConnectionManager(context.Logger, context.Events);

        foreach (var kvp in enabledDbs)
        {
            _connectionManager.RegisterConfiguration(kvp.Value, kvp.Key);
        }

        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events,
            connectionId => config.Databases.TryGetValue(connectionId, out var cfg) ? cfg : null);

        foreach (var kvp in enabledDbs)
        {
            context.Logger.Info($"[MySQL2] Connecting '{kvp.Key}' to {kvp.Value.Host}:{kvp.Value.Port}/{kvp.Value.Database}");

            var connected = await _connectionManager.TestConnectionAsync(kvp.Key, default).ConfigureAwait(false);
            if (connected)
            {
                context.Logger.Info(string.Format(
                    _strings?.ConnectionTestSuccess ?? "Connection '{0}' test successful",
                    kvp.Key));
            }
            else
            {
                context.Logger.Warning(string.Format(
                    _strings?.ConnectionTestFailed ?? "Connection '{0}' test failed",
                    kvp.Key));
            }
        }

        // Apply cache configuration so queries see the right knobs from the first call.
        var cacheConfig = context.Configuration.Get<CacheConfiguration>();
        QueryCache.Configure(
            enabled: cacheConfig.Enabled,
            maxEntries: cacheConfig.MaxEntries,
            timeQuantizeSeconds: cacheConfig.TimeQuantizeSeconds);

        // Wire the smart-cache pool registry so its background timers log to the
        // app's logger and so it can be cleanly disposed on stop.
        SmartCachePoolRegistry.Configure(context.Logger);

        // Bind observability to CodeLogic's event bus so QueryExecutedEvent /
        // CacheHit / CacheMiss / SlowQuery land on the app's existing bus.
        QueryObservability.Configure(context.Events, context.Logger);

        context.Logger.Info(_strings?.LibraryInitialized ?? "MySQL2 library initialized");
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Starting {Manifest.Name}");

        if (_connectionManager is null || !_isEnabled)
        {
            context.Logger.Info($"{Manifest.Name} started (disabled)");
            return;
        }

        try
        {
            foreach (var connectionId in _connectionManager.GetConnectionIds())
            {
                var serverInfo = await _connectionManager.GetServerInfoAsync(connectionId).ConfigureAwait(false);
                context.Logger.Info($"[MySQL2] [{connectionId}] Server: {serverInfo.Version} ({serverInfo.Comment})");
                context.Logger.Info($"[MySQL2] [{connectionId}] Database: {serverInfo.Database} on {serverInfo.Host}");
            }
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[MySQL2] Could not retrieve server info: {ex.Message}");
        }

        // Start the retention worker if any registered entity carries [RetainDays].
        _retentionWorker = new RetentionWorker(
            _connectionManager, context.Logger, _registeredEntities);
        if (_retentionWorker.HasWork) _retentionWorker.Start();

        context.Logger.Info(_strings?.LibraryStarted ?? "MySQL2 library started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        if (_retentionWorker is not null)
            await _retentionWorker.DisposeAsync().ConfigureAwait(false);
        _retentionWorker = null;

        // Stop every smart-cache pool's background timer.
        await SmartCachePoolRegistry.DisposeAllAsync().ConfigureAwait(false);

        _tableSyncService = null;
        _connectionManager = null;

        _context?.Logger.Info(_strings?.LibraryStopped ?? "MySQL2 library stopped");
    }

    // ── Health check ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!_isEnabled)
            return HealthStatus.Healthy("MySQL2 library is disabled");

        if (_connectionManager is null)
            return HealthStatus.Unhealthy("MySQL2 library not initialized");

        try
        {
            var connectionIds = _connectionManager.GetConnectionIds().ToList();
            var failedConnections = new List<string>();

            foreach (var connectionId in connectionIds)
            {
                var ok = await _connectionManager.TestConnectionAsync(connectionId).ConfigureAwait(false);
                if (!ok)
                    failedConnections.Add(connectionId);
            }

            var config = _context?.Configuration.Get<DatabaseConfiguration>();

            var data = new Dictionary<string, object>
            {
                ["totalDatabases"] = connectionIds.Count,
                ["failedDatabases"] = failedConnections.Count,
                ["connections"] = connectionIds.ToDictionary(
                    id => id,
                    id =>
                    {
                        var dbConfig = config?.Databases.TryGetValue(id, out var cfg) == true ? cfg : null;
                        return new Dictionary<string, object?>
                        {
                            ["host"] = dbConfig?.Host ?? "?",
                            ["database"] = dbConfig?.Database ?? "?",
                            ["openConnections"] = _connectionManager.GetOpenConnectionCount(id)
                        };
                    })
            };

            if (failedConnections.Count == 0)
            {
                return new HealthStatus
                {
                    Status = HealthStatusLevel.Healthy,
                    Message = _strings?.HealthCheckPassed ?? "Health check passed",
                    Data = data
                };
            }

            if (failedConnections.Count < connectionIds.Count)
            {
                return new HealthStatus
                {
                    Status = HealthStatusLevel.Degraded,
                    Message = string.Format(
                        _strings?.HealthCheckFailed ?? "Health check failed: {0}",
                        string.Join(", ", failedConnections)),
                    Data = data
                };
            }

            return new HealthStatus
            {
                Status = HealthStatusLevel.Unhealthy,
                Message = string.Format(
                    _strings?.HealthCheckFailed ?? "Health check failed: {0}",
                    string.Join(", ", failedConnections)),
                Data = data
            };
        }
        catch (Exception ex)
        {
            _context?.Logger.Error($"[MySQL2] Health check exception: {ex.Message}", ex);
            return HealthStatus.FromException(ex);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the <see cref="ConnectionManager"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public ConnectionManager ConnectionManager =>
        _connectionManager ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="TableSyncService"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the library is not initialized or disabled.</exception>
    public TableSyncService TableSync =>
        _tableSyncService ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="MigrationTracker"/>.</summary>
    public MigrationTracker MigrationTracker =>
        _tableSyncService?.GetMigrationTracker() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>Returns the <see cref="BackupManager"/>.</summary>
    public BackupManager BackupManager =>
        _tableSyncService?.GetBackupManager() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    /// <summary>
    /// Creates a <see cref="Repository{T}"/> for the given entity type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="connectionId">The connection ID to use. Default: "Default".</param>
    public Repository<T> GetRepository<T>(string connectionId = "Default") where T : class, new()
    {
        var config = _context?.Configuration.Get<DatabaseConfiguration>();
        return new Repository<T>(
            ConnectionManager,
            _context?.Logger,
            connectionId,
            config?.Databases.TryGetValue(connectionId, out var dbConfig) == true
                ? dbConfig.SlowQueryThresholdMs
                : 1000);
    }

    /// <summary>
    /// Creates a fluent <see cref="QueryBuilder{T}"/> for the given entity type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="connectionId">The connection ID to use. Default: "Default".</param>
    public QueryBuilder<T> Query<T>(string connectionId = "Default") where T : class, new()
    {
        var config = _context?.Configuration.Get<DatabaseConfiguration>();
        return new QueryBuilder<T>(
            ConnectionManager,
            _context?.Logger,
            connectionId,
            config?.Databases.TryGetValue(connectionId, out var dbConfig) == true
                ? dbConfig.SlowQueryThresholdMs
                : 1000);
    }

    /// <summary>
    /// Begins a new database transaction and returns a <see cref="TransactionScope"/>.
    /// </summary>
    public async Task<TransactionScope> BeginTransactionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await ConnectionManager.OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new TransactionScope(connectionId, conn, tx, _context?.Logger);
    }

    /// <summary>
    /// Syncs the table schema for the specified entity type. The type is also registered
    /// with the library so that entity-level workers (e.g. retention purge) pick it up.
    /// </summary>
    public Task<Result<SyncResult>> SyncTableAsync<T>(
        bool createBackup = true,
        string connectionId = "Default") where T : class
    {
        _registeredEntities.Add(typeof(T));
        return TableSync.SyncTableAsync<T>(createBackup, connectionId);
    }

    // ── Smart cache pools ────────────────────────────────────────────────────

    /// <summary>
    /// Registers a named <see cref="SmartCachePool"/>. Queries opt into the
    /// pool via <c>.SmartCache(name)</c>; the pool's background timer keeps
    /// every registered query's cache entry warm.
    /// <para>
    /// Idempotent — calling twice with the same name returns the existing
    /// pool unchanged (refresh interval is NOT updated on re-register).
    /// </para>
    /// </summary>
    /// <param name="name">Pool name (case-insensitive) referenced by <c>.SmartCache</c>.</param>
    /// <param name="refreshEvery">How often the pool re-runs every registered query.</param>
    /// <param name="maxIdleFires">
    /// Drop a registered entry after this many consecutive refresh ticks with
    /// no read. Default 3 — at a 30-second refresh interval, an unread entry
    /// is dropped after ~90 seconds, bounding cardinality on parameterized queries.
    /// </param>
    /// <param name="warmUp">
    /// Optional warm-up callback. When supplied, the pool runs it once as a
    /// fire-and-forget task right after registration so the cache is hot
    /// before the first user request hits it. Inside the callback, just call
    /// the queries that should be warm (with their normal
    /// <c>.SmartCache(name)</c> decoration) — they auto-register with the
    /// pool as usual. Exceptions are caught and logged; the pool stays
    /// lazy if warm-up fails.
    /// </param>
    public SmartCachePool RegisterCachePool(
        string name,
        TimeSpan refreshEvery,
        int maxIdleFires = 10,
        Func<Task>? warmUp = null) =>
        SmartCachePoolRegistry.Register(name, refreshEvery, maxIdleFires, warmUp);

    /// <summary>Triggers an out-of-schedule refresh for the named pool.</summary>
    public Task RefreshCachePoolAsync(string name, CancellationToken ct = default) =>
        SmartCachePoolRegistry.RefreshNowAsync(name, ct);

    /// <summary>Diagnostic snapshot of every registered smart-cache pool.</summary>
    public IReadOnlyList<SmartCachePoolStats> GetCachePoolStats() =>
        SmartCachePoolRegistry.GetStats();

    /// <summary>
    /// Diagnostic snapshot of the underlying <see cref="QueryCache"/>:
    /// total entries, per-table breakdown, table-version counters. Use
    /// from the admin UI to spot orphan accumulation or a hot table.
    /// </summary>
    public QueryCacheStats GetCacheStats() => QueryCache.GetStats();

    /// <summary>
    /// Tests the MySQL connection for the given connection ID.
    /// </summary>
    public async Task<Result<bool>> TestConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var ok = await ConnectionManager.TestConnectionAsync(connectionId, ct).ConfigureAwait(false);
            return Result<bool>.Success(ok);
        }
        catch (Exception ex)
        {
            return Result<bool>.Failure(Error.FromException(ex, "mysql.connection_failed"));
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _tableSyncService = null;
        _connectionManager = null;
    }
}
