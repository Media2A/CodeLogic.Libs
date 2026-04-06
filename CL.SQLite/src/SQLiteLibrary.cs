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
        Version = "2.0.0",
        Description = "SQLite database library with connection pooling, LINQ queries, and table sync",
        Author = "Media2A",
        Tags = ["sqlite", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private SQLiteStrings? _strings;
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

        var config = context.Configuration.Get<SQLiteConfig>();
        _strings = context.Localization.Get<SQLiteStrings>();

        if (!config.Enabled)
        {
            _isEnabled = false;
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
            return Task.CompletedTask;
        }

        _isEnabled = true;

        var validation = config.Validate();
        if (!validation.IsValid)
        {
            var errors = string.Join("; ", validation.Errors);
            throw new InvalidOperationException($"{Manifest.Name} configuration is invalid: {errors}");
        }

        _connectionManager = new ConnectionManager(config, context.Logger, context.DataDirectory);
        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events);

        context.Logger.Info(string.Format(_strings?.LibraryInitialized ?? "SQLite library initialized: {0}", config.DatabasePath));

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
            var conn = await _connectionManager.GetConnectionAsync().ConfigureAwait(false);
            await _connectionManager.ReleaseConnectionAsync(conn).ConfigureAwait(false);

            var dbPath = _connectionManager.DatabasePath;
            var msg = string.Format(_strings?.HealthCheckPassed ?? "SQLite is operational: {0}", dbPath);

            return new HealthStatus
            {
                Status = HealthStatusLevel.Healthy,
                Message = msg,
                Data = new Dictionary<string, object>
                {
                    ["databasePath"] = dbPath,
                    ["activeConnections"] = _connectionManager.ActiveConnectionCount,
                    ["pooledConnections"] = _connectionManager.PooledConnectionCount
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

    public Repository<T> GetRepository<T>() where T : class, new()
    {
        var config = _context?.Configuration.Get<SQLiteConfig>();
        return new Repository<T>(
            ConnectionManager,
            _context?.Logger,
            config?.SlowQueryThresholdMs ?? 500);
    }

    public QueryBuilder<T> GetQueryBuilder<T>() where T : class, new()
    {
        var config = _context?.Configuration.Get<SQLiteConfig>();
        return new QueryBuilder<T>(
            ConnectionManager,
            _context?.Logger,
            config?.SlowQueryThresholdMs ?? 500);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _connectionManager?.Dispose();
        _connectionManager = null;
        _tableSyncService = null;
    }
}
