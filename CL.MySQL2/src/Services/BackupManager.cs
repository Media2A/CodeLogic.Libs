using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Creates and manages schema backup files for MySQL tables and databases.
/// Backups contain DDL (CREATE TABLE statements) and are stored as .sql files.
/// </summary>
public sealed class BackupManager
{
    private readonly ConnectionManager _connectionManager;
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;

    public BackupManager(
        ConnectionManager connectionManager,
        string dataDirectory,
        ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
    }

    /// <summary>
    /// Backs up the CREATE TABLE DDL for the specified table to a timestamped .sql file.
    /// </summary>
    /// <param name="tableName">The table to back up.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<bool>> BackupTableSchemaAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var backupDir = GetBackupDirectory();
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = Path.Combine(backupDir, $"{tableName}_{timestamp}.sql");

            var ddl = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                return await GetTableDdlAsync(conn, tableName, ct).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(ddl))
            {
                _logger?.Warning($"[MySQL2] No DDL found for table `{tableName}` — backup skipped");
                return Result<bool>.Success(false);
            }

            var content = $"-- CL.MySQL2 Schema Backup\n-- Table: {tableName}\n-- Date: {DateTime.UtcNow:u}\n\n{ddl};\n";
            await File.WriteAllTextAsync(fileName, content, ct).ConfigureAwait(false);
            _logger?.Info($"[MySQL2] Schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] BackupTableSchemaAsync failed for `{tableName}`: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.backup_failed"));
        }
    }

    /// <summary>
    /// Backs up the DDL for all tables in the current database.
    /// </summary>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<bool>> BackupDatabaseSchemaAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var backupDir = GetBackupDirectory();
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = Path.Combine(backupDir, $"database_{timestamp}.sql");

            var allDdl = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                var tables = await GetTableNamesAsync(conn, ct).ConfigureAwait(false);
                var ddlParts = new List<string>();
                foreach (var tbl in tables)
                {
                    var ddl = await GetTableDdlAsync(conn, tbl, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ddl))
                        ddlParts.Add($"-- Table: {tbl}\n{ddl};");
                }
                return string.Join("\n\n", ddlParts);
            }, connectionId, ct).ConfigureAwait(false);

            var content = $"-- CL.MySQL2 Full Database Schema Backup\n-- Date: {DateTime.UtcNow:u}\n\n{allDdl}\n";
            await File.WriteAllTextAsync(fileName, content, ct).ConfigureAwait(false);
            _logger?.Info($"[MySQL2] Full database schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] BackupDatabaseSchemaAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.backup_failed"));
        }
    }

    /// <summary>
    /// Removes backup files older than the specified number of days.
    /// </summary>
    /// <param name="olderThanDays">Files older than this many days will be deleted.</param>
    /// <returns>The number of files deleted.</returns>
    public async Task<Result<int>> CleanupOldBackupsAsync(int olderThanDays = 30)
    {
        try
        {
            var backupDir = GetBackupDirectory();
            if (!Directory.Exists(backupDir))
                return Result<int>.Success(0);

            var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            var deleted = 0;

            foreach (var file in Directory.GetFiles(backupDir, "*.sql"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                    deleted++;
                    _logger?.Debug($"[MySQL2] Deleted old backup: {file}");
                }
            }

            _logger?.Info($"[MySQL2] Cleanup complete — {deleted} old backup(s) removed");
            await Task.CompletedTask; // keep async signature consistent
            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] CleanupOldBackupsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.backup_cleanup_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private string GetBackupDirectory() =>
        Path.Combine(_dataDirectory, "backups");

    private static async Task<string> GetTableDdlAsync(
        MySqlConnection conn,
        string tableName,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE `{tableName}`";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
            return reader.GetString(1);
        return string.Empty;
    }

    private static async Task<List<string>> GetTableNamesAsync(
        MySqlConnection conn,
        CancellationToken ct)
    {
        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW TABLES";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            tables.Add(reader.GetString(0));
        return tables;
    }
}
