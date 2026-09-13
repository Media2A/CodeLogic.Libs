using CL.MySQL2.Configuration;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;
using CL.MySQL2.Core;

namespace CL.MySQL2.Services;

/// <summary>
/// Creates and manages schema backup files for MySQL tables and databases.
/// Backups contain DDL (CREATE TABLE statements) and are stored as .sql files.
/// <para>
/// Files go to <c>DataDirectory/backups</c> unless the connection's
/// <see cref="MySqlDatabaseConfig.BackupDirectory"/> is set, in which case that directory is
/// used for both writing and reading back.
/// </para>
/// </summary>
public sealed class BackupManager
{
    private readonly ConnectionManager _connectionManager;
    private readonly string _dataDirectory;
    private readonly ILogger? _logger;
    private readonly Func<string, MySqlDatabaseConfig?>? _configLookup;

    /// <param name="connectionManager">The connection manager to use for database access.</param>
    /// <param name="dataDirectory">Base data directory; backups default to its <c>backups</c> subfolder.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="configLookup">
    /// Optional delegate resolving per-connection config, so a configured
    /// <see cref="MySqlDatabaseConfig.BackupDirectory"/> can override the default location.
    /// </param>
    public BackupManager(
        ConnectionManager connectionManager,
        string dataDirectory,
        ILogger? logger = null,
        Func<string, MySqlDatabaseConfig?>? configLookup = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _logger = logger;
        _configLookup = configLookup;
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
            var backupDir = GetBackupDirectory(connectionId);
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
            var backupDir = GetBackupDirectory(connectionId);
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
    /// <param name="connectionId">The connection whose backup directory to clean.</param>
    /// <returns>The number of files deleted.</returns>
    public async Task<Result<int>> CleanupOldBackupsAsync(int olderThanDays = 30, string connectionId = "Default")
    {
        try
        {
            var backupDir = GetBackupDirectory(connectionId);
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

    /// <summary>
    /// Returns the most recent schema backup file for a table, or null if none exists. Backups are
    /// named <c>{table}_{yyyyMMdd_HHmmss}.sql</c> in the backup directory.
    /// </summary>
    public string? GetLatestBackupFile(string tableName, string connectionId = "Default")
    {
        var backupDir = GetBackupDirectory(connectionId);
        if (!Directory.Exists(backupDir)) return null;
        return Directory.GetFiles(backupDir, $"{tableName}_*.sql")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Restores a table's schema by replaying a backup .sql file: drops the table, then re-runs the
    /// captured CREATE TABLE. Destructive and operator-driven — the table's data is lost (only the
    /// DDL was ever backed up). When <paramref name="backupFile"/> is null the latest backup is used.
    /// </summary>
    /// <param name="tableName">The table to restore.</param>
    /// <param name="backupFile">Path to the backup file, or null to use the latest.</param>
    /// <param name="connectionId">The connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<Result<bool>> RestoreTableSchemaAsync(
        string tableName,
        string? backupFile = null,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var file = backupFile ?? GetLatestBackupFile(tableName, connectionId);
            if (file is null || !File.Exists(file))
                return Result<bool>.Failure(Error.Internal(
                    "mysql.restore_not_found", $"No schema backup found for {MySqlDialect.Quote(tableName)}."));

            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            // Keep only the CREATE TABLE statement (strip leading comment lines and trailing ';').
            var createIdx = content.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
            if (createIdx < 0)
                return Result<bool>.Failure(Error.Internal(
                    "mysql.restore_invalid", $"Backup file for {MySqlDialect.Quote(tableName)} has no CREATE TABLE statement."));
            var createSql = content[createIdx..].TrimEnd().TrimEnd(';');

            _logger?.Warning($"[MySQL2] Restoring schema for `{tableName}` from {file} (table will be dropped and recreated).");

            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using (var drop = conn.CreateCommand())
                {
                    drop.CommandText = $"DROP TABLE IF EXISTS {MySqlDialect.Quote(tableName)}";
                    await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var create = conn.CreateCommand())
                {
                    create.CommandText = createSql;
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Info($"[MySQL2] Schema restored for `{tableName}`.");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] RestoreTableSchemaAsync failed for `{tableName}`: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.restore_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The backup directory for a connection: its configured <c>BackupDirectory</c> when set,
    /// otherwise <c>DataDirectory/backups</c>. A relative configured path resolves against the
    /// data directory, so a bare folder name stays inside the application's own storage.
    /// </summary>
    private string GetBackupDirectory(string connectionId = "Default")
    {
        var configured = _configLookup?.Invoke(connectionId)?.BackupDirectory;
        if (string.IsNullOrWhiteSpace(configured))
            return Path.Combine(_dataDirectory, "backups");
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(_dataDirectory, configured);
    }

    private static async Task<string> GetTableDdlAsync(
        MySqlConnection conn,
        string tableName,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE {MySqlDialect.Quote(tableName)}";
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
