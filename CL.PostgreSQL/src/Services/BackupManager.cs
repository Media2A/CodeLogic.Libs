using CL.PostgreSQL.Core;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Creates and manages schema backup files for PostgreSQL tables.
/// Since PostgreSQL has no built-in SHOW CREATE TABLE equivalent,
/// backups contain column metadata from information_schema.
/// </summary>
public sealed class BackupManager
{
    private readonly ConnectionManager _connectionManager;
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;
    private readonly SchemaAnalyzer _analyzer;

    public BackupManager(
        ConnectionManager connectionManager,
        string dataDirectory,
        ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _analyzer = new SchemaAnalyzer(logger);
    }

    /// <summary>
    /// Backs up the schema (column definitions) for the specified table to a timestamped file.
    /// </summary>
    public async Task<Result<bool>> BackupTableSchemaAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        string connectionId,
        CancellationToken ct = default)
    {
        try
        {
            var backupDir = GetBackupDirectory();
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = Path.Combine(backupDir, $"{schema}_{table}_{timestamp}.sql");

            var columns = await _analyzer.GetColumnsAsync(conn, schema, table, ct).ConfigureAwait(false);

            var lines = new List<string>
            {
                $"-- CL.PostgreSQL Schema Backup",
                $"-- Schema: {schema}, Table: {table}",
                $"-- Date: {DateTime.UtcNow:u}",
                $"-- Connection: {connectionId}",
                $"-- Columns:"
            };

            foreach (var col in columns)
            {
                var pk = col.IsPrimaryKey ? " [PK]" : string.Empty;
                var nullable = col.IsNullable ? " NULL" : " NOT NULL";
                var def = col.DefaultValue is not null ? $" DEFAULT {col.DefaultValue}" : string.Empty;
                lines.Add($"--   {col.Name} {col.DataType}{nullable}{pk}{def}");
            }

            await File.WriteAllLinesAsync(fileName, lines, ct).ConfigureAwait(false);
            _logger?.Info($"[PostgreSQL] Schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] BackupTableSchemaAsync failed for \"{schema}\".\"{table}\": {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.backup_failed"));
        }
    }

    /// <summary>
    /// Overload for use without an existing connection — opens a new one.
    /// </summary>
    public async Task<Result<bool>> BackupTableSchemaAsync(
        string schema,
        string table,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                return await BackupTableSchemaAsync(conn, schema, table, connectionId, ct).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] BackupTableSchemaAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.backup_failed"));
        }
    }

    /// <summary>
    /// Removes old backup files, keeping only the specified number of newest files.
    /// </summary>
    public async Task<Result<int>> CleanupOldBackupsAsync(int keepCount = 10)
    {
        try
        {
            var backupDir = GetBackupDirectory();
            if (!Directory.Exists(backupDir))
                return Result<int>.Success(0);

            var files = Directory.GetFiles(backupDir, "*.sql")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            var toDelete = files.Skip(keepCount).ToList();
            var deleted = 0;

            foreach (var file in toDelete)
            {
                file.Delete();
                deleted++;
                _logger?.Debug($"[PostgreSQL] Deleted old backup: {file.FullName}");
            }

            _logger?.Info($"[PostgreSQL] Cleanup complete — {deleted} old backup(s) removed");
            await Task.CompletedTask;
            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] CleanupOldBackupsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.backup_cleanup_failed"));
        }
    }

    private string GetBackupDirectory() => Path.Combine(_dataDirectory, "backups");
}
