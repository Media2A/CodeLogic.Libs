using System.Diagnostics;
using CL.MySQL2.Core;
using CL.MySQL2.Events;
using CL.MySQL2.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Synchronizes database table schema with entity class definitions.
/// Uses <see cref="SchemaAnalyzer"/> to detect and apply CREATE/ALTER TABLE statements.
/// </summary>
public sealed class TableSyncService
{
    private readonly ConnectionManager _connectionManager;
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;
    private readonly SchemaAnalyzer _analyzer;
    private readonly MigrationTracker _migrationTracker;
    private readonly BackupManager _backupManager;

    /// <param name="connectionManager">The connection manager to use for database access.</param>
    /// <param name="dataDirectory">Base data directory (for backup and migration storage).</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="events">Optional event bus for publishing sync events.</param>
    public TableSyncService(
        ConnectionManager connectionManager,
        string dataDirectory,
        ILogger? logger = null,
        IEventBus? events = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _events = events;
        _analyzer = new SchemaAnalyzer(logger);
        _migrationTracker = new MigrationTracker(connectionManager, logger);
        _backupManager = new BackupManager(connectionManager, dataDirectory, logger);
    }

    /// <summary>
    /// Synchronizes the table schema for entity type <typeparamref name="T"/>.
    /// Creates the table if it doesn't exist, or alters it to add missing columns/indexes.
    /// </summary>
    /// <param name="createBackup">Whether to back up the current schema before making changes.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<SyncResult>> SyncTableAsync<T>(
        bool createBackup = true,
        string connectionId = "Default",
        CancellationToken ct = default) where T : class
    {
        var entityType = typeof(T);
        var tableName = SchemaAnalyzer.GetTableName(entityType);
        var sw = Stopwatch.StartNew();
        var operations = new List<string>();
        var errors = new List<string>();

        _logger?.Info($"[MySQL2] Syncing table `{tableName}`...");

        try
        {
            var tableExists = await TableExistsAsync(tableName, connectionId, ct).ConfigureAwait(false);

            if (!tableExists)
            {
                // CREATE TABLE
                if (createBackup)
                {
                    // No existing table to back up
                }

                var createSql = _analyzer.GenerateCreateTable(entityType);
                await ExecuteSqlAsync(createSql, connectionId, ct).ConfigureAwait(false);
                operations.Add($"CREATE TABLE `{tableName}`");
                _logger?.Info($"[MySQL2] Created table `{tableName}`");
            }
            else
            {
                // Backup before altering
                if (createBackup)
                {
                    var backupResult = await _backupManager.BackupTableSchemaAsync(tableName, connectionId, ct)
                        .ConfigureAwait(false);
                    if (backupResult.IsFailure)
                        _logger?.Warning($"[MySQL2] Schema backup failed for `{tableName}`: {backupResult.Error?.Message}");
                }

                // ALTER TABLE as needed
                var alterStatements = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
                {
                    return await _analyzer.GenerateAlterStatementsAsync(entityType, conn, ct)
                        .ConfigureAwait(false);
                }, connectionId, ct).ConfigureAwait(false);

                foreach (var stmt in alterStatements)
                {
                    await ExecuteSqlAsync(stmt, connectionId, ct).ConfigureAwait(false);
                    operations.Add(stmt);
                    _logger?.Debug($"[MySQL2] Applied: {stmt}");
                }

                if (operations.Count > 0)
                    _logger?.Info($"[MySQL2] Updated table `{tableName}` ({operations.Count} changes)");
                else
                    _logger?.Debug($"[MySQL2] Table `{tableName}` is up to date");
            }

            sw.Stop();
            var syncResult = new SyncResult
            {
                Success = true,
                TableName = tableName,
                Operations = operations,
                Errors = errors,
                Duration = sw.Elapsed
            };

            if (_events is not null)
            {
                await _events.PublishAsync(new TableSyncedEvent(
                    connectionId, tableName, !tableExists, operations, sw.Elapsed))
                    .ConfigureAwait(false);
            }

            return Result<SyncResult>.Success(syncResult);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error($"[MySQL2] Table sync failed for `{tableName}`: {ex.Message}", ex);
            errors.Add(ex.Message);

            return Result<SyncResult>.Failure(
                Error.Internal("mysql.sync_failed", $"Table synchronization failed for {tableName}", ex.Message));
        }
    }

    /// <summary>
    /// Synchronizes multiple entity types and returns a dictionary of results.
    /// </summary>
    /// <param name="entityTypes">Entity types to sync.</param>
    /// <param name="createBackup">Whether to back up schemas before altering.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<Dictionary<string, SyncResult>>> SyncTablesAsync(
        IEnumerable<Type> entityTypes,
        bool createBackup = true,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, SyncResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in entityTypes)
        {
            var tableName = SchemaAnalyzer.GetTableName(entityType);
            try
            {
                // We use reflection to call the generic SyncTableAsync<T>
                var method = typeof(TableSyncService)
                    .GetMethod(nameof(SyncTableAsync))!
                    .MakeGenericMethod(entityType);

                var task = (Task<Result<SyncResult>>)method.Invoke(this, [createBackup, connectionId, ct])!;
                var result = await task.ConfigureAwait(false);

                results[tableName] = result.IsSuccess
                    ? result.Value!
                    : new SyncResult
                    {
                        Success = false,
                        TableName = tableName,
                        Errors = [result.Error?.Message ?? "Unknown error"]
                    };
            }
            catch (Exception ex)
            {
                _logger?.Error($"[MySQL2] Failed to sync `{tableName}`: {ex.Message}", ex);
                results[tableName] = new SyncResult
                {
                    Success = false,
                    TableName = tableName,
                    Errors = [ex.Message]
                };
            }
        }

        return Result<Dictionary<string, SyncResult>>.Success(results);
    }

    /// <summary>Returns the <see cref="MigrationTracker"/> used by this service.</summary>
    public MigrationTracker GetMigrationTracker() => _migrationTracker;

    /// <summary>Returns the <see cref="BackupManager"/> used by this service.</summary>
    public BackupManager GetBackupManager() => _backupManager;

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<bool> TableExistsAsync(
        string tableName,
        string connectionId,
        CancellationToken ct)
    {
        return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*) FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tbl";
            cmd.Parameters.AddWithValue("@tbl", tableName);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            return count > 0;
        }, connectionId, ct).ConfigureAwait(false);
    }

    private async Task ExecuteSqlAsync(string sql, string connectionId, CancellationToken ct)
    {
        await _connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }, connectionId, ct).ConfigureAwait(false);
    }
}
