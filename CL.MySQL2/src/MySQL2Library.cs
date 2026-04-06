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
/// LINQ query builder, automatic table synchronization, migrations, and schema backups.
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
        Version = "2.0.0",
        Description = "MySQL database access with LINQ query builder, table sync, migrations, and backups",
        Author = "Media2A",
        Tags = ["mysql", "database", "orm", "repository"]
    };

    private LibraryContext? _context;
    private ConnectionManager? _connectionManager;
    private TableSyncService? _tableSyncService;
    private MySQL2Strings? _strings;
    private bool _isEnabled;

    // ── Phase 1: Configure ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnConfigureAsync(LibraryContext context)
    {
        _context = context;
        context.Logger.Info($"Configuring {Manifest.Name} v{Manifest.Version}");

        context.Configuration.Register<DatabaseConfiguration>("mysql");
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

        if (!config.Enabled)
        {
            _isEnabled = false;
            context.Logger.Warning($"{Manifest.Name} is disabled in configuration — skipping initialization.");
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
        _connectionManager = new ConnectionManager(config, context.Logger, context.Events);
        _tableSyncService = new TableSyncService(
            _connectionManager,
            context.DataDirectory,
            context.Logger,
            context.Events);

        context.Logger.Info($"[MySQL2] Connecting to {config.Host}:{config.Port}/{config.Database}");

        // Test connection
        var connected = await _connectionManager.TestConnectionAsync(ct: default).ConfigureAwait(false);
        if (connected)
        {
            context.Logger.Info(_strings?.ConnectionTestSuccess ?? "Connection test successful");
        }
        else
        {
            context.Logger.Warning(_strings?.ConnectionTestFailed ?? "Connection test failed");
        }

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
            var serverInfo = await _connectionManager.GetServerInfoAsync().ConfigureAwait(false);
            context.Logger.Info($"[MySQL2] Server: {serverInfo.Version} ({serverInfo.Comment})");
            context.Logger.Info($"[MySQL2] Database: {serverInfo.Database} on {serverInfo.Host}");
        }
        catch (Exception ex)
        {
            context.Logger.Warning($"[MySQL2] Could not retrieve server info: {ex.Message}");
        }

        context.Logger.Info(_strings?.LibraryStarted ?? "MySQL2 library started");
    }

    // ── Phase 4: Stop ─────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task OnStopAsync()
    {
        _context?.Logger.Info($"Stopping {Manifest.Name}");

        _tableSyncService = null;
        _connectionManager = null;

        _context?.Logger.Info(_strings?.LibraryStopped ?? "MySQL2 library stopped");
        return Task.CompletedTask;
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
            var ok = await _connectionManager.TestConnectionAsync().ConfigureAwait(false);
            var config = _context?.Configuration.Get<DatabaseConfiguration>();
            var openCounts = _connectionManager.GetAllConnectionCounts();

            var data = new Dictionary<string, object>
            {
                ["host"] = config?.Host ?? "?",
                ["database"] = config?.Database ?? "?",
                ["openConnections"] = _connectionManager.GetOpenConnectionCount()
            };

            if (ok)
            {
                return new HealthStatus
                {
                    Status = HealthStatusLevel.Healthy,
                    Message = _strings?.HealthCheckPassed ?? "Health check passed",
                    Data = data
                };
            }
            else
            {
                return new HealthStatus
                {
                    Status = HealthStatusLevel.Unhealthy,
                    Message = string.Format(
                        _strings?.HealthCheckFailed ?? "Health check failed: {0}",
                        "Connection test returned false"),
                    Data = data
                };
            }
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
            config?.SlowQueryThresholdMs ?? 1000);
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
            config?.SlowQueryThresholdMs ?? 1000);
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
    /// Syncs the table schema for the specified entity type.
    /// </summary>
    public Task<Result<SyncResult>> SyncTableAsync<T>(
        bool createBackup = true,
        string connectionId = "Default") where T : class
        => TableSync.SyncTableAsync<T>(createBackup, connectionId);

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
