using CodeLogic.Core.Logging;

namespace CL.MySQL2.Services;

/// <summary>
/// Reconciliation status recorded per table in <c>__schema_state</c>.
/// </summary>
public enum SchemaSyncStatus
{
    /// <summary>The live table fully matches the model — no outstanding work.</summary>
    Synced = 0,

    /// <summary>
    /// Additive (Production) sync was applied but a destructive change (column/index/FK drop)
    /// was required and deferred. A <see cref="Models.SyncMode.Migration"/> pass will complete it.
    /// </summary>
    DriftPending = 1
}

/// <summary>
/// Owns the <c>__schema_state</c> sentinel table: one row per model holding a CRC of the model's
/// desired schema plus reconciliation status and audit metadata. The CRC lets schema sync skip a
/// table entirely (no <c>information_schema</c> diffing) when nothing has changed.
/// </summary>
public sealed class SchemaStateStore
{
    private const string StateTable = "__schema_state";
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;

    public SchemaStateStore(ConnectionManager connectionManager, ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
    }

    /// <summary>
    /// Ensures the <c>__schema_state</c> table exists. Call once during library initialization.
    /// </summary>
    public async Task<bool> EnsureStateTableAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var sql = $@"
                CREATE TABLE IF NOT EXISTS `{StateTable}` (
                    `TableName`     VARCHAR(255) NOT NULL,
                    `SchemaCrc`     VARCHAR(64)  NOT NULL,
                    `Status`        VARCHAR(20)  NOT NULL,
                    `SyncMode`      VARCHAR(20)  NOT NULL,
                    `AppVersion`    VARCHAR(50)  NULL,
                    `ModelInfo`     TEXT         NULL,
                    `UpdatedAt`     DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    `UpdatedByNode` VARCHAR(100) NULL,
                    PRIMARY KEY (`TableName`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";

            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Debug("[MySQL2] Schema state table ready");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] Failed to ensure schema state table: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Returns the recorded state for a table, or null if none.</summary>
    public async Task<SchemaStateRecord?> GetStateAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT `TableName`, `SchemaCrc`, `Status`, `SyncMode`, `AppVersion`, `UpdatedAt`, `UpdatedByNode`
                    FROM `{StateTable}` WHERE `TableName` = @tbl";
                cmd.Parameters.AddWithValue("@tbl", tableName);

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return (SchemaStateRecord?)null;

                return new SchemaStateRecord(
                    TableName: reader.GetString(0),
                    SchemaCrc: reader.GetString(1),
                    Status: ParseStatus(reader.GetString(2)),
                    SyncMode: reader.GetString(3),
                    AppVersion: reader.IsDBNull(4) ? null : reader.GetString(4),
                    UpdatedAt: reader.GetDateTime(5),
                    UpdatedByNode: reader.IsDBNull(6) ? null : reader.GetString(6));
            }, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetStateAsync failed for `{tableName}`: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>Inserts or updates the state row for a table.</summary>
    public async Task<bool> UpsertStateAsync(
        string tableName,
        string schemaCrc,
        SchemaSyncStatus status,
        string syncMode,
        string? appVersion = null,
        string? modelInfo = null,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    INSERT INTO `{StateTable}`
                        (`TableName`, `SchemaCrc`, `Status`, `SyncMode`, `AppVersion`, `ModelInfo`, `UpdatedAt`, `UpdatedByNode`)
                    VALUES (@tbl, @crc, @status, @mode, @appver, @model, UTC_TIMESTAMP(), @node)
                    ON DUPLICATE KEY UPDATE
                        `SchemaCrc` = VALUES(`SchemaCrc`),
                        `Status` = VALUES(`Status`),
                        `SyncMode` = VALUES(`SyncMode`),
                        `AppVersion` = VALUES(`AppVersion`),
                        `ModelInfo` = VALUES(`ModelInfo`),
                        `UpdatedAt` = VALUES(`UpdatedAt`),
                        `UpdatedByNode` = VALUES(`UpdatedByNode`)";
                cmd.Parameters.AddWithValue("@tbl", tableName);
                cmd.Parameters.AddWithValue("@crc", schemaCrc);
                cmd.Parameters.AddWithValue("@status", status.ToString());
                cmd.Parameters.AddWithValue("@mode", syncMode);
                cmd.Parameters.AddWithValue("@appver", (object?)appVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@model", (object?)modelInfo ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@node", Environment.MachineName);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpsertStateAsync failed for `{tableName}`: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Removes the state row for a table (e.g. after a manual restore).</summary>
    public async Task<bool> RemoveStateAsync(
        string tableName,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM `{StateTable}` WHERE `TableName` = @tbl";
                cmd.Parameters.AddWithValue("@tbl", tableName);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] RemoveStateAsync failed for `{tableName}`: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Returns all recorded state rows, ordered by table name.</summary>
    public async Task<List<SchemaStateRecord>> GetAllAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT `TableName`, `SchemaCrc`, `Status`, `SyncMode`, `AppVersion`, `UpdatedAt`, `UpdatedByNode`
                    FROM `{StateTable}` ORDER BY `TableName` ASC";

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var records = new List<SchemaStateRecord>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    records.Add(new SchemaStateRecord(
                        TableName: reader.GetString(0),
                        SchemaCrc: reader.GetString(1),
                        Status: ParseStatus(reader.GetString(2)),
                        SyncMode: reader.GetString(3),
                        AppVersion: reader.IsDBNull(4) ? null : reader.GetString(4),
                        UpdatedAt: reader.GetDateTime(5),
                        UpdatedByNode: reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
                return records;
            }, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetAllAsync failed: {ex.Message}", ex);
            return [];
        }
    }

    private static SchemaSyncStatus ParseStatus(string value) =>
        Enum.TryParse<SchemaSyncStatus>(value, ignoreCase: true, out var s) ? s : SchemaSyncStatus.Synced;
}

/// <summary>A single row from the <c>__schema_state</c> sentinel table.</summary>
public record SchemaStateRecord(
    string TableName,
    string SchemaCrc,
    SchemaSyncStatus Status,
    string SyncMode,
    string? AppVersion,
    DateTime UpdatedAt,
    string? UpdatedByNode);
