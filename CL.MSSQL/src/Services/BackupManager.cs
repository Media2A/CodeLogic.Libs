using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.SqlClient;
using CL.MSSQL.Core;
using System.Security.Cryptography;
using System.Text;

namespace CL.MSSQL.Services;

/// <summary>
/// Creates and manages schema backup files for SQL Server tables and databases.
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

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            var fileName = Path.Combine(backupDir, $"{GetTableBackupStem(tableName)}_{timestamp}.sql");

            var ddl = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                return await GetTableDdlAsync(conn, tableName, ct).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(ddl))
            {
                _logger?.Warning($"[MSSQL] No DDL found for table [{tableName}] — backup skipped");
                return Result<bool>.Success(false);
            }

            var content = $"-- CL.MSSQL Schema Backup\n-- Table: {tableName}\n-- Date: {DateTime.UtcNow:u}\n\n{ddl};\n";
            await File.WriteAllTextAsync(fileName, content, ct).ConfigureAwait(false);
            _logger?.Info($"[MSSQL] Schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] BackupTableSchemaAsync failed for [{tableName}]: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.backup_failed"));
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

            var content = $"-- CL.MSSQL Full Database Schema Backup\n-- Date: {DateTime.UtcNow:u}\n\n{allDdl}\n";
            await File.WriteAllTextAsync(fileName, content, ct).ConfigureAwait(false);
            _logger?.Info($"[MSSQL] Full database schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] BackupDatabaseSchemaAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.backup_failed"));
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
                    _logger?.Debug($"[MSSQL] Deleted old backup: {file}");
                }
            }

            _logger?.Info($"[MSSQL] Cleanup complete — {deleted} old backup(s) removed");
            await Task.CompletedTask; // keep async signature consistent
            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] CleanupOldBackupsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.backup_cleanup_failed"));
        }
    }

    /// <summary>
    /// Returns the most recent schema backup file for a table, or null if none exists. Backups are
    /// use a filesystem-safe table stem plus a UTC timestamp in the backup directory.
    /// </summary>
    public string? GetLatestBackupFile(string tableName)
    {
        var backupDir = GetBackupDirectory();
        if (!Directory.Exists(backupDir)) return null;
        return Directory.GetFiles(backupDir, $"{GetTableBackupStem(tableName)}_*.sql")
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
            var file = backupFile ?? GetLatestBackupFile(tableName);
            if (file is null || !File.Exists(file))
                return Result<bool>.Failure(Error.Internal(
                    "mssql.restore_not_found", $"No schema backup found for [{tableName}]."));

            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            // Keep only the CREATE TABLE statement (strip leading comment lines and trailing ';').
            var createIdx = content.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
            if (createIdx < 0)
                return Result<bool>.Failure(Error.Internal(
                    "mssql.restore_invalid", $"Backup file for [{tableName}] has no CREATE TABLE statement."));
            var createSql = content[createIdx..].TrimEnd().TrimEnd(';');
            var expectedTarget = SqlServerDialect.QuoteMultipart(tableName.Contains('.') ? tableName : $"dbo.{tableName}");
            if (!createSql.StartsWith($"CREATE TABLE {expectedTarget}", StringComparison.OrdinalIgnoreCase))
                return Result<bool>.Failure(Error.Internal(
                    "mssql.restore_target_mismatch", $"Backup does not create the requested table {expectedTarget}."));

            _logger?.Warning($"[MSSQL] Restoring schema for [{tableName}] from {file} (table will be dropped and recreated).");

            await _connectionManager.ExecuteWithTransactionAsync(async (conn, transaction) =>
            {
                await using (var drop = conn.CreateCommand())
                {
                    drop.Transaction = transaction;
                    drop.CommandText = $"DROP TABLE IF EXISTS {SqlServerDialect.QuoteMultipart(tableName.Contains('.') ? tableName : $"dbo.{tableName}")}";
                    await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var create = conn.CreateCommand())
                {
                    create.Transaction = transaction;
                    create.CommandText = createSql;
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var clearState = conn.CreateCommand())
                {
                    clearState.Transaction = transaction;
                    clearState.CommandText = "IF OBJECT_ID(N'[dbo].[__schema_state]', N'U') IS NOT NULL DELETE FROM [dbo].[__schema_state] WHERE [TableName]=@table";
                    clearState.Parameters.Add(new SqlParameter("@table", System.Data.SqlDbType.NVarChar, 255) { Value = tableName.Split('.').Last() });
                    await clearState.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Info($"[MSSQL] Schema restored for [{tableName}].");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] RestoreTableSchemaAsync failed for [{tableName}]: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.restore_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private string GetBackupDirectory() =>
        Path.Combine(_dataDirectory, "backups");

    private static string GetTableBackupStem(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var readable = new string(tableName.Take(48)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tableName)))[..12];
        return $"{readable}_{hash}";
    }

    private static async Task<string> GetTableDdlAsync(SqlConnection conn, string tableName, CancellationToken ct)
    {
        var parts = tableName.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];
        var definitions = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable,
                       CONVERT(bit, COLUMNPROPERTY(c.object_id,c.name,'IsIdentity')), dc.name, dc.definition, c.collation_name
                FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id
                JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
                LEFT JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
                WHERE s.name=@schema AND t.name=@table ORDER BY c.column_id
                """;
            cmd.Parameters.AddWithValue("@schema", schema); cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0); var type = reader.GetString(1); var max = reader.GetInt16(2);
                var precision = reader.GetByte(3); var scale = reader.GetByte(4); var nullable = reader.GetBoolean(5);
                var identity = reader.GetBoolean(6); var defaultName = reader.IsDBNull(7) ? null : reader.GetString(7);
                var defaultValue = reader.IsDBNull(8) ? null : reader.GetString(8); var collation = reader.IsDBNull(9) ? null : reader.GetString(9);
                var declaration = type switch
                {
                    "varchar" or "char" or "varbinary" or "binary" => $"{type}({(max == -1 ? "max" : max)})",
                    "nvarchar" or "nchar" => $"{type}({(max == -1 ? "max" : max / 2)})",
                    "decimal" or "numeric" => $"{type}({precision},{scale})",
                    "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
                    _ => type
                };
                var line = $"{SqlServerDialect.Quote(name)} {declaration}";
                // COLLATE accepts a collation-name token, not a delimited identifier.
                // The value comes from sys.columns rather than caller input.
                if (collation is not null) line += $" COLLATE {collation}";
                if (identity) line += " IDENTITY(1,1)";
                if (type is not "timestamp" and not "rowversion") line += nullable ? " NULL" : " NOT NULL";
                if (defaultValue is not null) line += $" CONSTRAINT {SqlServerDialect.Quote(defaultName!)} DEFAULT {defaultValue}";
                definitions.Add(line);
            }
        }
        if (definitions.Count == 0) return string.Empty;

        var ddl = new StringBuilder($"IF SCHEMA_ID(N'{schema.Replace("'", "''")}') IS NULL EXEC(N'CREATE SCHEMA {SqlServerDialect.Quote(schema)}');\nCREATE TABLE {SqlServerDialect.Qualify(schema, table)} (\n  {string.Join(",\n  ", definitions)}\n);");
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT i.name, i.is_unique, i.is_primary_key,
                       STRING_AGG(CASE WHEN ic.key_ordinal>0 THEN QUOTENAME(c.name) END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id),
                       STRING_AGG(CASE WHEN ic.is_included_column=1 THEN QUOTENAME(c.name) END, ', ') WITHIN GROUP (ORDER BY ic.index_column_id)
                FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                WHERE s.name=@schema AND t.name=@table AND i.name IS NOT NULL AND i.is_hypothetical=0
                GROUP BY i.name,i.is_unique,i.is_primary_key,i.index_id ORDER BY i.index_id
                """;
            cmd.Parameters.AddWithValue("@schema", schema); cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0); var unique = reader.GetBoolean(1); var primary = reader.GetBoolean(2);
                var keys = reader.IsDBNull(3) ? "" : reader.GetString(3); var includes = reader.IsDBNull(4) ? "" : reader.GetString(4);
                if (primary) ddl.Append($"\nALTER TABLE {SqlServerDialect.Qualify(schema, table)} ADD CONSTRAINT {SqlServerDialect.Quote(name)} PRIMARY KEY ({keys});");
                else ddl.Append($"\nCREATE {(unique ? "UNIQUE " : "")}INDEX {SqlServerDialect.Quote(name)} ON {SqlServerDialect.Qualify(schema, table)} ({keys}){(includes.Length > 0 ? $" INCLUDE ({includes})" : "")};");
            }
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT cc.name, cc.definition
                FROM sys.check_constraints cc
                JOIN sys.tables t ON t.object_id=cc.parent_object_id
                JOIN sys.schemas s ON s.schema_id=t.schema_id
                WHERE s.name=@schema AND t.name=@table
                ORDER BY cc.name
                """;
            cmd.Parameters.AddWithValue("@schema", schema); cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                ddl.Append($"\nALTER TABLE {SqlServerDialect.Qualify(schema, table)} ADD CONSTRAINT {SqlServerDialect.Quote(reader.GetString(0))} CHECK {reader.GetString(1)};");
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fk.name, pc.name, rs.name, rt.name, rc.name, fk.delete_referential_action_desc, fk.update_referential_action_desc
                FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
                JOIN sys.tables pt ON pt.object_id=fk.parent_object_id JOIN sys.schemas ps ON ps.schema_id=pt.schema_id
                JOIN sys.columns pc ON pc.object_id=pt.object_id AND pc.column_id=fkc.parent_column_id
                JOIN sys.tables rt ON rt.object_id=fk.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
                JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=fkc.referenced_column_id
                WHERE ps.name=@schema AND pt.name=@table ORDER BY fk.name,fkc.constraint_column_id
                """;
            cmd.Parameters.AddWithValue("@schema", schema); cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                ddl.Append($"\nALTER TABLE {SqlServerDialect.Qualify(schema, table)} ADD CONSTRAINT {SqlServerDialect.Quote(reader.GetString(0))} FOREIGN KEY ({SqlServerDialect.Quote(reader.GetString(1))}) REFERENCES {SqlServerDialect.Qualify(reader.GetString(2), reader.GetString(3))} ({SqlServerDialect.Quote(reader.GetString(4))}) ON DELETE {reader.GetString(5).Replace('_', ' ')} ON UPDATE {reader.GetString(6).Replace('_', ' ')};");
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT CONVERT(nvarchar(max), ep.value), c.name
                FROM sys.extended_properties ep JOIN sys.tables t ON t.object_id=ep.major_id JOIN sys.schemas s ON s.schema_id=t.schema_id
                LEFT JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=ep.minor_id
                WHERE ep.name=N'MS_Description' AND s.name=@schema AND t.name=@table
                """;
            cmd.Parameters.AddWithValue("@schema", schema); cmd.Parameters.AddWithValue("@table", table);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var value = reader.GetString(0).Replace("'", "''", StringComparison.Ordinal);
                var column = reader.IsDBNull(1) ? null : reader.GetString(1).Replace("'", "''", StringComparison.Ordinal);
                ddl.Append($"\nEXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'{value}', @level0type=N'SCHEMA', @level0name=N'{schema.Replace("'", "''")}', @level1type=N'TABLE', @level1name=N'{table.Replace("'", "''")}'{(column is null ? "" : $", @level2type=N'COLUMN', @level2name=N'{column}'")};");
            }
        }
        return ddl.ToString();
    }

    private static async Task<List<string>> GetTableNamesAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT s.name + N'.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            tables.Add(reader.GetString(0));
        return tables;
    }
}
