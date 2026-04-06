using CL.PostgreSQL.Events;
using CL.PostgreSQL.Localization;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;

namespace CL.PostgreSQL;

/// <summary>
/// CL.PostgreSQL — CodeLogic library providing PostgreSQL database access with
/// multi-database support, a fluent LINQ query builder, automatic table synchronization,
/// migrations, and schema backups.
/// </summary>
public sealed class PostgreSQLLibrary : ILibrary
{
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.PostgreSQL",
        Name = "PostgreSQL Library",
        Version = "2.0.0",
        Description = "PostgreSQL database access with multi-database support, LINQ query builder, table sync, and migrations",
        Author = "Media2A",
        Tags = ["postgresql", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private PostgreSQLStrings? _strings;
    private PostgreSQLConfig? _config;
    private bool _isEnabled;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<PostgreSQLConfig>("postgresql");
        context.Localization.Register<PostgreSQLStrings>();

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    public async Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        _config = context.Configuration.Get<PostgreSQLConfig>();
        _strings = context.Localization.Get<PostgreSQLStrings>();

        var enabledDbs = _config.Databases
            .Where(kvp => kvp.Value.Enabled)
            .ToList();

        if (enabledDbs.Count == 0)
        {
            _isEnabled = false;
            context.Logger.Warning($"{Manifest.Name} has no enabled databases — skipping initialization.");
            return;
        }

        _isEnabled = true;

        // Validate all enabled databases
        var validation = _config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException(
                $"{Manifest.Name} configuration is invalid: {errors}");
        }

        // Create ConnectionManager
        _connectionManager = new ConnectionManager(context.Logger, context.Events);

        foreach (var kvp in enabledDbs)
        {
            _connectionManager.RegisterConfiguration(kvp.Key, kvp.Value);
            context.Logger.Info(string.Format(
                _strings?.ConnectionRegistered ?? "Registered database: {0} -> {1}",
                kvp.Key, $"{kvp.Value.Host}:{kvp.Value.Port}/{kvp.Value.Database}"));
        }

        // Create TableSyncService
        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events);

        // Test all connections
        foreach (var kvp in enabledDbs)
        {
            var connected = await _connectionManager.TestConnectionAsync(kvp.Key).ConfigureAwait(false);
            if (connected)
            {
                var serverInfo = await _connectionManager.GetServerInfoAsync(kvp.Key).ConfigureAwait(false);
                var version = serverInfo?.ServerVersion ?? "unknown";
                context.Logger.Info(string.Format(
                    _strings?.ConnectionTestSuccess ?? "Connection '{0}' test successful (v{1})",
                    kvp.Key, version));
            }
            else
            {
                context.Logger.Warning(string.Format(
                    _strings?.ConnectionTestFailed ?? "Connection '{0}' test failed: {1}",
                    kvp.Key, "Unable to connect"));
            }
        }

        context.Logger.Info(string.Format(
            _strings?.LibraryInitialized ?? "PostgreSQL library initialized with {0} database(s)",
            enabledDbs.Count));
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Starting {Manifest.Name}");

        if (!_isEnabled)
        {
            context.Logger.Info($"{Manifest.Name} started (no enabled databases)");
            return Task.CompletedTask;
        }

        context.Logger.Info(_strings?.LibraryStarted ?? "PostgreSQL library started");
        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        _tableSyncService = null;
        _connectionManager?.Dispose();
        _connectionManager = null;

        _context?.Logger.Info(_strings?.LibraryStopped ?? "PostgreSQL library stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!_isEnabled)
            return HealthStatus.Healthy("PostgreSQL library has no enabled databases");

        if (_connectionManager is null)
            return HealthStatus.Unhealthy("PostgreSQL library not initialized");

        var failedConnections = new List<string>();
        var connectionIds = _connectionManager.GetConnectionIds().ToList();

        foreach (var id in connectionIds)
        {
            var ok = await _connectionManager.TestConnectionAsync(id).ConfigureAwait(false);
            if (!ok) failedConnections.Add(id);
        }

        var data = new Dictionary<string, object>
        {
            ["totalDatabases"] = connectionIds.Count,
            ["failedDatabases"] = failedConnections.Count,
            ["totalOpenConnections"] = connectionIds.Sum(id => _connectionManager.GetOpenConnectionCount(id))
        };

        if (failedConnections.Count == 0)
        {
            return new HealthStatus
            {
                Status = HealthStatusLevel.Healthy,
                Message = string.Format(
                    _strings?.HealthCheckPassed ?? "All {0} database connection(s) operational",
                    connectionIds.Count),
                Data = data
            };
        }

        if (failedConnections.Count < connectionIds.Count)
        {
            return new HealthStatus
            {
                Status = HealthStatusLevel.Degraded,
                Message = string.Format(
                    _strings?.HealthCheckFailed ?? "Failed connections: {0}",
                    string.Join(", ", failedConnections)),
                Data = data
            };
        }

        return new HealthStatus
        {
            Status = HealthStatusLevel.Unhealthy,
            Message = string.Format(
                _strings?.HealthCheckFailed ?? "Failed connections: {0}",
                string.Join(", ", failedConnections)),
            Data = data
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public ConnectionManager ConnectionManager =>
        _connectionManager ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or has no enabled databases.");

    public TableSyncService TableSync =>
        _tableSyncService ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or has no enabled databases.");

    public BackupManager BackupManager =>
        _tableSyncService?.GetBackupManager() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or has no enabled databases.");

    public MigrationTracker MigrationTracker =>
        _tableSyncService?.GetMigrationTracker() ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or has no enabled databases.");

    public Repository<T> GetRepository<T>(string connectionId = "Default") where T : class, new()
    {
        var slowMs = _config?.Databases.TryGetValue(connectionId, out var cfg) == true
            ? cfg.SlowQueryThresholdMs
            : 1000;

        return new Repository<T>(ConnectionManager, _context?.Logger, connectionId, slowMs);
    }

    public QueryBuilder<T> Query<T>(string connectionId = "Default") where T : class, new()
    {
        var slowMs = _config?.Databases.TryGetValue(connectionId, out var cfg) == true
            ? cfg.SlowQueryThresholdMs
            : 1000;

        return new QueryBuilder<T>(ConnectionManager, _context?.Logger, connectionId, slowMs);
    }

    public QueryBuilder QueryRaw(string connectionId = "Default")
        => new(ConnectionManager, _context?.Logger);

    public async Task<TransactionScope> BeginTransactionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await ConnectionManager.OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new TransactionScope(connectionId, conn, tx, _context?.Logger);
    }

    public Task<Result<SyncResult>> SyncTableAsync<T>(
        bool createBackup = true,
        string connectionId = "Default") where T : class
        => TableSync.SyncTableAsync<T>(connectionId, createBackup);

    public void RegisterDatabase(string connectionId, DatabaseConfig config)
    {
        ConnectionManager.RegisterConfiguration(connectionId, config);
        _context?.Logger.Info(string.Format(
            _strings?.ConnectionRegistered ?? "Registered database: {0} -> {1}",
            connectionId, $"{config.Host}:{config.Port}/{config.Database}"));
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _tableSyncService = null;
        _connectionManager?.Dispose();
        _connectionManager = null;
    }
}
