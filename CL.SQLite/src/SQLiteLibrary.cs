using CL.SQLite.Localization;
using CL.SQLite.Models;
using CL.SQLite.Services;
using CodeLogic.Framework.Libraries;

namespace CL.SQLite;

/// <summary>
/// <b>CL.SQLite</b> — CodeLogic library providing SQLite database access with a connection pool,
/// LINQ query builder, automatic table synchronization, and migration tracking.
/// </summary>
public sealed class SQLiteLibrary : ILibrary
{
    public LibraryManifest Manifest { get; } = new()
    {
        Id = "CL.SQLite",
        Name = "SQLite Library",
        Version = "3.0.0",
        Description = "SQLite database library with connection pooling, LINQ queries, and table sync",
        Author = "Media2A",
        Tags = ["sqlite", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private SQLiteStrings? _strings;
    private SQLiteConfig? _config;
    private bool _isEnabled;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<SQLiteConfig>("sqlite");
        context.Localization.Register<SQLiteStrings>();

        return Task.CompletedTask;
    }

    // ── Phase 2: Initialize ───────────────────────────────────────────────────

    public Task OnInitializeAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Initializing {Manifest.Name}");

        _config = context.Configuration.Get<SQLiteConfig>();
        _strings = context.Localization.Get<SQLiteStrings>();

        var enabledDbs = _config.Databases
            .Where(kvp => kvp.Value.Enabled)
            .ToList();

        if (enabledDbs.Count == 0)
        {
            _isEnabled = false;
            context.Logger.Warning($"{Manifest.Name} has no enabled databases — skipping initialization.");
            return Task.CompletedTask;
        }

        _isEnabled = true;

        var validation = _config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        _connectionManager = new ConnectionManager(context.Logger, context.DataDirectory);
        foreach (var kvp in enabledDbs)
            _connectionManager.RegisterConfiguration(kvp.Key, kvp.Value);

        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events);

        context.Logger.Info(string.Format(
            _strings?.LibraryInitialized ?? "SQLite library initialized with {0} database(s)",
            enabledDbs.Count));

        return Task.CompletedTask;
    }

    // ── Phase 3: Start ────────────────────────────────────────────────────────

    public Task OnStartAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Starting {Manifest.Name}");

        if (_connectionManager is null || !_isEnabled)
        {
            context.Logger.Info($"{Manifest.Name} started (disabled)");
            return Task.CompletedTask;
        }

        context.Logger.Info(_strings?.LibraryStarted ?? "SQLite library started");
        return Task.CompletedTask;
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        _connectionManager?.Dispose();
        _connectionManager = null;
        _tableSyncService = null;

        _context?.Logger.Info(_strings?.LibraryStopped ?? "SQLite library stopped");
        return Task.CompletedTask;
    }

    // ── Health check ──────────────────────────────────────────────────────────

    public async Task<HealthStatus> HealthCheckAsync()
    {
        if (!_isEnabled)
            return HealthStatus.Healthy("SQLite library is disabled");

        if (_connectionManager is null)
            return HealthStatus.Unhealthy("SQLite library not initialized");

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

            var msg = failedConnections.Count == 0
                ? string.Format(_strings?.HealthCheckPassed ?? "SQLite is operational: {0}", string.Join(", ", connectionIds))
                : string.Format(_strings?.HealthCheckFailed ?? "SQLite health check failed: {0}", string.Join(", ", failedConnections));

            return new HealthStatus
            {
                Status = failedConnections.Count switch
                {
                    0 => HealthStatusLevel.Healthy,
                    var count when count < connectionIds.Count => HealthStatusLevel.Degraded,
                    _ => HealthStatusLevel.Unhealthy
                },
                Message = msg,
                Data = new Dictionary<string, object>
                {
                    ["totalDatabases"] = connectionIds.Count,
                    ["failedDatabases"] = failedConnections.Count,
                    ["connections"] = connectionIds.ToDictionary(
                        id => id,
                        id => new Dictionary<string, object>
                        {
                            ["databasePath"] = _connectionManager.GetDatabasePath(id),
                            ["activeConnections"] = _connectionManager.GetActiveConnectionCount(id),
                            ["pooledConnections"] = _connectionManager.GetPooledConnectionCount(id)
                        })
                }
            };
        }
        catch (Exception ex)
        {
            _context?.Logger.Error($"[SQLite] Health check exception: {ex.Message}", ex);
            var msg = string.Format(_strings?.HealthCheckFailed ?? "SQLite health check failed: {0}", ex.Message);
            return HealthStatus.Unhealthy(msg);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public ConnectionManager ConnectionManager =>
        _connectionManager ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    public TableSyncService TableSync =>
        _tableSyncService ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    public MigrationTracker MigrationTracker =>
        _tableSyncService?.MigrationTracker ?? throw new InvalidOperationException(
            $"{Manifest.Name} has not been initialized or is disabled.");

    public Repository<T> GetRepository<T>(string connectionId = "Default") where T : class, new()
    {
        var slowMs = _config?.Databases.TryGetValue(connectionId, out var dbConfig) == true
            ? dbConfig.SlowQueryThresholdMs
            : 500;

        return new Repository<T>(
            ConnectionManager,
            _context?.Logger,
            connectionId,
            slowMs);
    }

    public QueryBuilder<T> GetQueryBuilder<T>(string connectionId = "Default") where T : class, new()
    {
        var slowMs = _config?.Databases.TryGetValue(connectionId, out var dbConfig) == true
            ? dbConfig.SlowQueryThresholdMs
            : 500;

        return new QueryBuilder<T>(
            ConnectionManager,
            _context?.Logger,
            connectionId,
            slowMs);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _connectionManager?.Dispose();
        _connectionManager = null;
        _tableSyncService = null;
    }
}
