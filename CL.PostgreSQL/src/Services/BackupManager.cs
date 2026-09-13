using System.Text;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Npgsql;
using CL.PostgreSQL.Core;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Creates and manages schema backup files for PostgreSQL tables and databases.
/// <para>
/// PostgreSQL has no <c>SHOW CREATE TABLE</c>, so the DDL is reconstructed from the
/// system catalogs: columns with their formatted types, defaults and not-null flags,
/// followed by primary key, unique and check constraints, indexes, foreign keys and
/// comments. The result is a replayable <c>CREATE TABLE</c> script.
/// </para>
/// <para>
/// This captures schema only, never data — restoring drops and recreates the table.
/// For data backups use <c>pg_dump</c>.
/// </para>
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
    /// Backs up the reconstructed CREATE TABLE DDL for one table to a timestamped .sql file.
    /// </summary>
    public async Task<Result<bool>> BackupTableSchemaAsync(
        string tableName,
        string schemaName = PostgreSqlDialect.DefaultSchema,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var backupDir = GetBackupDirectory(connectionId);
            Directory.CreateDirectory(backupDir);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var fileName = Path.Combine(backupDir, $"{BackupStem(schemaName, tableName)}_{timestamp}.sql");

            var ddl = await _connectionManager.ExecuteWithConnectionAsync(
                conn => GetTableDdlAsync(conn, schemaName, tableName, ct),
                connectionId, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(ddl))
            {
                _logger?.Warning(
                    $"[PostgreSQL] No DDL found for table \"{schemaName}\".\"{tableName}\" — backup skipped");
                return Result<bool>.Success(false);
            }

            var content =
                $"-- CL.PostgreSQL Schema Backup\n" +
                $"-- Schema: {schemaName}\n" +
                $"-- Table: {tableName}\n" +
                $"-- Date: {DateTime.UtcNow:u}\n\n{ddl}\n";
            await File.WriteAllTextAsync(fileName, content, ct).ConfigureAwait(false);
            _logger?.Info($"[PostgreSQL] Schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error(
                $"[PostgreSQL] BackupTableSchemaAsync failed for \"{schemaName}\".\"{tableName}\": {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.backup_failed"));
        }
    }

    /// <summary>Backs up the DDL for every table in the database's user schemas.</summary>
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
                foreach (var (schema, table) in tables)
                {
                    var ddl = await GetTableDdlAsync(conn, schema, table, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(ddl))
                        ddlParts.Add($"-- Table: {schema}.{table}\n{ddl}");
                }
                return string.Join("\n\n", ddlParts);
            }, connectionId, ct).ConfigureAwait(false);

            var content = $"-- CL.PostgreSQL Full Database Schema Backup\n-- Date: {DateTime.UtcNow:u}\n\n{allDdl}\n";
            await File.WriteAllTextAsync(fileName, content, ct).ConfigureAwait(false);
            _logger?.Info($"[PostgreSQL] Full database schema backup written: {fileName}");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] BackupDatabaseSchemaAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.backup_failed"));
        }
    }

    /// <summary>
    /// Removes backup files older than the specified number of days, from the backup
    /// directory configured for <paramref name="connectionId"/>.
    /// </summary>
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
                    _logger?.Debug($"[PostgreSQL] Deleted old backup: {file}");
                }
            }

            _logger?.Info($"[PostgreSQL] Cleanup complete — {deleted} old backup(s) removed");
            await Task.CompletedTask; // keep async signature consistent
            return Result<int>.Success(deleted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] CleanupOldBackupsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.backup_cleanup_failed"));
        }
    }

    /// <summary>
    /// Returns the most recent schema backup file for a table, or null if none exists. Backups
    /// are named <c>{schema}_{table}_{yyyyMMdd_HHmmss}.sql</c> in the backup directory.
    /// </summary>
    public string? GetLatestBackupFile(
        string tableName,
        string schemaName = PostgreSqlDialect.DefaultSchema,
        string connectionId = "Default")
    {
        var backupDir = GetBackupDirectory(connectionId);
        if (!Directory.Exists(backupDir)) return null;
        return Directory.GetFiles(backupDir, $"{BackupStem(schemaName, tableName)}_*.sql")
            .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Restores a table's schema by replaying a backup .sql file: drops the table, then re-runs
    /// the captured DDL. Destructive and operator-driven — the table's data is lost (only the
    /// DDL was ever backed up). When <paramref name="backupFile"/> is null the latest is used.
    /// </summary>
    public async Task<Result<bool>> RestoreTableSchemaAsync(
        string tableName,
        string schemaName = PostgreSqlDialect.DefaultSchema,
        string? backupFile = null,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var qualified = PostgreSqlDialect.Qualify(schemaName, tableName);
        try
        {
            var file = backupFile ?? GetLatestBackupFile(tableName, schemaName, connectionId);
            if (file is null || !File.Exists(file))
                return Result<bool>.Failure(Error.Internal(
                    "postgresql.restore_not_found", $"No schema backup found for {qualified}."));

            var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var createIdx = content.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
            if (createIdx < 0)
                return Result<bool>.Failure(Error.Internal(
                    "postgresql.restore_invalid", $"Backup file for {qualified} has no CREATE TABLE statement."));
            var script = content[createIdx..].Trim();

            _logger?.Warning(
                $"[PostgreSQL] Restoring schema for {qualified} from {file} (table will be dropped and recreated).");

            // One transaction: a restore that fails halfway must not leave the table dropped.
            await _connectionManager.ExecuteWithTransactionAsync(async (conn, tx) =>
            {
                await using (var drop = conn.CreateCommand())
                {
                    drop.Transaction = tx;
                    drop.CommandText = $"DROP TABLE IF EXISTS {qualified} CASCADE";
                    await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var create = conn.CreateCommand())
                {
                    create.Transaction = tx;
                    create.CommandText = script;
                    await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                await using (var clearState = conn.CreateCommand())
                {
                    // The restored table may not match the current model, so the CRC
                    // sentinel has to go: leaving it would let the next sync skip a table
                    // that was just rebuilt from a backup. Keyed schema.table.
                    clearState.Transaction = tx;
                    clearState.CommandText =
                        "DELETE FROM public.__schema_state WHERE \"TableName\" = @table";
                    clearState.Parameters.AddWithValue("@table", $"{schemaName}.{tableName}");
                    await clearState.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Info($"[PostgreSQL] Schema restored for {qualified}.");
            return Result<bool>.Success(true);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] RestoreTableSchemaAsync failed for {qualified}: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.restore_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Where backups for a connection are written: its configured <c>BackupDirectory</c>
    /// when set, otherwise <c>DataDirectory/backups</c>.
    /// </summary>
    private string GetBackupDirectory(string? connectionId = null)
    {
        var configured = Core.PostgreSqlRuntimeOptions.For(connectionId).BackupDirectory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(_dataDirectory, "backups")
            : configured;
    }

    /// <summary>
    /// Builds the filename stem for a backup. Any character that is not safe in a path is
    /// replaced, so a schema or table name can never escape the backup directory.
    /// </summary>
    private static string BackupStem(string schemaName, string tableName)
    {
        var raw = $"{schemaName}_{tableName}";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Reconstructs a replayable CREATE TABLE script from the system catalogs.
    /// <c>pg_get_constraintdef</c> and <c>pg_get_indexdef</c> do the heavy lifting: they
    /// return exactly the text PostgreSQL itself would emit.
    /// </summary>
    private static async Task<string> GetTableDdlAsync(
        NpgsqlConnection conn,
        string schemaName,
        string tableName,
        CancellationToken ct)
    {
        var qualified = PostgreSqlDialect.Qualify(schemaName, tableName);
        var sb = new StringBuilder();

        // ── Columns ──
        var columns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT a.attname,
                       pg_catalog.format_type(a.atttypid, a.atttypmod) AS coltype,
                       a.attnotnull,
                       pg_catalog.pg_get_expr(d.adbin, d.adrelid)      AS coldefault,
                       a.attidentity::text AS identity_kind,
                       coll.collname
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c      ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n  ON n.oid = c.relnamespace
                LEFT JOIN pg_catalog.pg_attrdef d
                       ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                LEFT JOIN pg_catalog.pg_collation coll ON coll.oid = a.attcollation
                WHERE n.nspname = @schema AND c.relname = @table
                  AND a.attnum > 0 AND NOT a.attisdropped
                ORDER BY a.attnum
                """;
            cmd.Parameters.AddWithValue("@schema", schemaName);
            cmd.Parameters.AddWithValue("@table", tableName);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                var notNull = reader.GetBoolean(2);
                var def = reader.IsDBNull(3) ? null : reader.GetString(3);
                var identity = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var collation = reader.IsDBNull(5) ? null : reader.GetString(5);

                var line = new StringBuilder($"    {PostgreSqlDialect.Quote(name)} {type}");
                if (collation is not null && collation != "default")
                    line.Append($" COLLATE {PostgreSqlDialect.Quote(collation)}");
                // Identity columns carry their sequence implicitly; a plain DEFAULT would
                // conflict with GENERATED, so the two are mutually exclusive here.
                if (identity == "a") line.Append(" GENERATED ALWAYS AS IDENTITY");
                else if (identity == "d") line.Append(" GENERATED BY DEFAULT AS IDENTITY");
                else if (def is not null) line.Append($" DEFAULT {def}");
                if (notNull) line.Append(" NOT NULL");
                columns.Add(line.ToString());
            }
        }

        if (columns.Count == 0) return string.Empty;

        // ── Table-level constraints (PK, UNIQUE, CHECK, FK) ──
        var constraints = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT con.conname, pg_catalog.pg_get_constraintdef(con.oid)
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class c     ON c.oid = con.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table
                ORDER BY con.contype DESC, con.conname
                """;
            cmd.Parameters.AddWithValue("@schema", schemaName);
            cmd.Parameters.AddWithValue("@table", tableName);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                constraints.Add($"    CONSTRAINT {PostgreSqlDialect.Quote(reader.GetString(0))} {reader.GetString(1)}");
        }

        sb.AppendLine($"CREATE TABLE {qualified} (");
        sb.AppendLine(string.Join(",\n", columns.Concat(constraints)));
        sb.AppendLine(");");

        // ── Indexes that are not already implied by a constraint ──
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pg_catalog.pg_get_indexdef(i.indexrelid)
                FROM pg_catalog.pg_index i
                JOIN pg_catalog.pg_class ic    ON ic.oid = i.indexrelid
                JOIN pg_catalog.pg_class c     ON c.oid = i.indrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_catalog.pg_constraint con
                      WHERE con.conindid = i.indexrelid)
                ORDER BY ic.relname
                """;
            cmd.Parameters.AddWithValue("@schema", schemaName);
            cmd.Parameters.AddWithValue("@table", tableName);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                sb.AppendLine($"{reader.GetString(0)};");
        }

        // ── Comments ──
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT a.attname, pg_catalog.col_description(c.oid, a.attnum)
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c     ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relname = @table
                  AND a.attnum > 0 AND NOT a.attisdropped
                  AND pg_catalog.col_description(c.oid, a.attnum) IS NOT NULL
                ORDER BY a.attnum
                """;
            cmd.Parameters.AddWithValue("@schema", schemaName);
            cmd.Parameters.AddWithValue("@table", tableName);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var column = PostgreSqlDialect.Quote(reader.GetString(0));
                var comment = reader.GetString(1).Replace("'", "''", StringComparison.Ordinal);
                sb.AppendLine($"COMMENT ON COLUMN {qualified}.{column} IS '{comment}';");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Lists every ordinary table in the database's non-system schemas.</summary>
    private static async Task<List<(string Schema, string Table)>> GetTableNamesAsync(
        NpgsqlConnection conn,
        CancellationToken ct)
    {
        var tables = new List<(string, string)>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, c.relname
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg_toast%'
              AND n.nspname NOT LIKE 'pg_temp%'
            ORDER BY n.nspname, c.relname
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            tables.Add((reader.GetString(0), reader.GetString(1)));
        return tables;
    }
}
