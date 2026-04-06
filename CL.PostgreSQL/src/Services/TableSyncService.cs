using System.Diagnostics;
using System.Reflection;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Synchronizes PostgreSQL table schemas with entity class definitions.
/// Creates tables that don't exist, and alters tables to add missing columns/indexes.
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
        _migrationTracker = new MigrationTracker(dataDirectory, logger);
        _backupManager = new BackupManager(connectionManager, dataDirectory, logger);
    }

    /// <summary>
    /// Synchronizes the table schema for entity type T.
    /// Creates the table if it doesn't exist, or alters it to add missing columns/indexes.
    /// </summary>
    public async Task<Result<SyncResult>> SyncTableAsync<T>(
        string connectionId = "Default",
        bool createBackup = true,
        CancellationToken ct = default) where T : class
    {
        var entityType = typeof(T);
        var tableName = SchemaAnalyzer.GetTableName(entityType);
        var schemaName = SchemaAnalyzer.GetSchemaName(entityType);
        var sw = Stopwatch.StartNew();
        var operations = new List<string>();
        var errors = new List<string>();

        _logger?.Info($"[PostgreSQL] Syncing table \"{schemaName}\".\"{tableName}\"...");

        try
        {
            var tableExists = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
                await _analyzer.TableExistsAsync(conn, schemaName, tableName, ct).ConfigureAwait(false),
                connectionId, ct).ConfigureAwait(false);

            if (!tableExists)
            {
                var createSql = _analyzer.GenerateCreateTableSql(entityType);
                await ExecuteSqlScriptAsync(createSql, connectionId, ct).ConfigureAwait(false);
                operations.Add($"CREATE TABLE \"{schemaName}\".\"{tableName}\"");
                _logger?.Info($"[PostgreSQL] Created table \"{schemaName}\".\"{tableName}\"");
            }
            else
            {
                // Backup before altering
                if (createBackup)
                {
                    var backupResult = await _backupManager.BackupTableSchemaAsync(schemaName, tableName, connectionId, ct)
                        .ConfigureAwait(false);
                    if (backupResult.IsFailure)
                        _logger?.Warning($"[PostgreSQL] Schema backup failed for \"{schemaName}\".\"{tableName}\": {backupResult.Error?.Message}");
                }

                var alterStatements = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
                    await _analyzer.GenerateAlterStatementsAsync(entityType, conn, ct).ConfigureAwait(false),
                    connectionId, ct).ConfigureAwait(false);

                foreach (var stmt in alterStatements)
                {
                    await ExecuteSqlScriptAsync(stmt, connectionId, ct).ConfigureAwait(false);
                    operations.Add(stmt);
                    _logger?.Debug($"[PostgreSQL] Applied: {stmt}");
                }

                if (operations.Count > 0)
                    _logger?.Info($"[PostgreSQL] Updated table \"{schemaName}\".\"{tableName}\" ({operations.Count} changes)");
                else
                    _logger?.Debug($"[PostgreSQL] Table \"{schemaName}\".\"{tableName}\" is up to date");
            }

            sw.Stop();
            var syncResult = new SyncResult
            {
                Success = true,
                TableName = tableName,
                SchemaName = schemaName,
                Operations = operations,
                Errors = errors,
                Duration = sw.Elapsed
            };

            if (_events is not null)
            {
                await _events.PublishAsync(new TableSyncedEvent(
                    connectionId, schemaName, tableName, !tableExists, operations, sw.Elapsed))
                    .ConfigureAwait(false);
            }

            return Result<SyncResult>.Success(syncResult);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error($"[PostgreSQL] Table sync failed for \"{schemaName}\".\"{tableName}\": {ex.Message}", ex);
            errors.Add(ex.Message);

            return Result<SyncResult>.Failure(
                Error.Internal("postgresql.sync_failed",
                    $"Table synchronization failed for {schemaName}.{tableName}", ex.Message));
        }
    }

    /// <summary>
    /// Synchronizes multiple entity types and returns a dictionary of results keyed by table name.
    /// </summary>
    public async Task<Result<Dictionary<string, SyncResult>>> SyncTablesAsync(
        Type[] modelTypes,
        string connectionId = "Default",
        bool createBackup = true,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, SyncResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var entityType in modelTypes)
        {
            var tableName = SchemaAnalyzer.GetTableName(entityType);
            var schemaName = SchemaAnalyzer.GetSchemaName(entityType);
            var key = $"{schemaName}.{tableName}";

            try
            {
                var method = typeof(TableSyncService)
                    .GetMethod(nameof(SyncTableAsync))!
                    .MakeGenericMethod(entityType);

                var task = (Task<Result<SyncResult>>)method.Invoke(this, [connectionId, createBackup, ct])!;
                var result = await task.ConfigureAwait(false);

                results[key] = result.IsSuccess
                    ? result.Value!
                    : new SyncResult
                    {
                        Success = false,
                        TableName = tableName,
                        SchemaName = schemaName,
                        Errors = [result.Error?.Message ?? "Unknown error"]
                    };
            }
            catch (Exception ex)
            {
                _logger?.Error($"[PostgreSQL] Failed to sync \"{key}\": {ex.Message}", ex);
                results[key] = new SyncResult
                {
                    Success = false,
                    TableName = tableName,
                    SchemaName = schemaName,
                    Errors = [ex.Message]
                };
            }
        }

        return Result<Dictionary<string, SyncResult>>.Success(results);
    }

    /// <summary>
    /// Synchronizes all entity types from the given namespace.
    /// </summary>
    public async Task<Result<Dictionary<string, SyncResult>>> SyncNamespaceAsync(
        string namespaceName,
        string connectionId = "Default",
        bool createBackup = true,
        bool includeDerivedNamespaces = false,
        CancellationToken ct = default)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var types = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return []; }
            })
            .Where(t => t.IsClass && !t.IsAbstract
                && t.GetCustomAttribute<TableAttribute>() is not null
                && (includeDerivedNamespaces
                    ? t.Namespace?.StartsWith(namespaceName, StringComparison.OrdinalIgnoreCase) == true
                    : string.Equals(t.Namespace, namespaceName, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        return await SyncTablesAsync(types, connectionId, createBackup, ct).ConfigureAwait(false);
    }

    public BackupManager GetBackupManager() => _backupManager;
    public MigrationTracker GetMigrationTracker() => _migrationTracker;

    private async Task ExecuteSqlScriptAsync(string sql, string connectionId, CancellationToken ct)
    {
        // Split multi-statement scripts on semicolons
        var statements = sql
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        foreach (var stmt in statements)
        {
            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);
        }
    }
}
