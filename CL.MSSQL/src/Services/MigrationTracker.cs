using CodeLogic.Core.Logging;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

/// <summary>
/// Tracks applied database migrations in a dedicated <c>__migrations</c> table.
/// Supports recording migrations and querying applied history.
/// </summary>
public sealed class MigrationTracker
{
    private const string MigrationsTable = "__migrations";
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;

    public MigrationTracker(
        ConnectionManager connectionManager,
        ILogger? logger = null)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
    }

    /// <summary>
    /// Ensures the migrations tracking table exists.
    /// Should be called once during library initialization.
    /// </summary>
    /// <param name="connectionId">Connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> EnsureMigrationsTableAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var sql = $@"
                IF OBJECT_ID(N'[dbo].[{MigrationsTable}]', N'U') IS NULL
                CREATE TABLE [dbo].[{MigrationsTable}] (
                    [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK___migrations] PRIMARY KEY,
                    [MigrationId] nvarchar(255) NOT NULL CONSTRAINT [UQ___migrations_MigrationId] UNIQUE,
                    [Description] nvarchar(500) NULL,
                    [AppliedAt] datetime2(7) NOT NULL CONSTRAINT [DF___migrations_AppliedAt] DEFAULT SYSUTCDATETIME(),
                    [Checksum] varchar(64) NULL
                );";

            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Debug($"[MSSQL] Migrations table ready");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] Failed to ensure migrations table: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns true when the migration with the given ID has already been applied.
    /// </summary>
    /// <param name="migrationId">The unique migration identifier.</param>
    /// <param name="connectionId">Connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> HasMigrationBeenAppliedAsync(
        string migrationId,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COUNT(*) FROM [dbo].[{MigrationsTable}] WHERE [MigrationId] = @id";
                cmd.Parameters.AddWithValue("@id", migrationId);
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                return count > 0;
            }, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] HasMigrationBeenAppliedAsync failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Records a successfully applied migration.
    /// </summary>
    /// <param name="migrationId">The unique migration identifier.</param>
    /// <param name="description">Human-readable description. Optional.</param>
    /// <param name="checksum">Optional checksum of the migration script.</param>
    /// <param name="connectionId">Connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RecordMigrationAsync(
        string migrationId,
        string? description = null,
        string? checksum = null,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    IF NOT EXISTS (SELECT 1 FROM [dbo].[{MigrationsTable}] WITH (UPDLOCK, HOLDLOCK) WHERE [MigrationId] = @id)
                    INSERT INTO [dbo].[{MigrationsTable}] ([MigrationId], [Description], [Checksum], [AppliedAt])
                    VALUES (@id, @desc, @checksum, SYSUTCDATETIME())";
                cmd.Parameters.AddWithValue("@id", migrationId);
                cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@checksum", (object?)checksum ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Info($"[MSSQL] Migration recorded: {migrationId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] RecordMigrationAsync failed: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns the list of all applied migration records, ordered by application date.
    /// </summary>
    /// <param name="connectionId">Connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<MigrationRecord>> GetAppliedMigrationsAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            return await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
                    SELECT [Id], [MigrationId], [Description], [AppliedAt], [Checksum]
                    FROM [dbo].[{MigrationsTable}]
                    ORDER BY [AppliedAt] ASC";

                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var records = new List<MigrationRecord>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    records.Add(new MigrationRecord(
                        Id: reader.GetInt32(0),
                        MigrationId: reader.GetString(1),
                        Description: reader.IsDBNull(2) ? null : reader.GetString(2),
                        AppliedAt: reader.GetDateTime(3),
                        Checksum: reader.IsDBNull(4) ? null : reader.GetString(4)));
                }
                return records;
            }, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] GetAppliedMigrationsAsync failed: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>
    /// Removes a migration record (e.g., for rollback purposes).
    /// </summary>
    /// <param name="migrationId">The migration ID to remove.</param>
    /// <param name="connectionId">Connection ID to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> RemoveMigrationRecordAsync(
        string migrationId,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM [dbo].[{MigrationsTable}] WHERE [MigrationId] = @id";
                cmd.Parameters.AddWithValue("@id", migrationId);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return true;
            }, connectionId, ct).ConfigureAwait(false);

            _logger?.Info($"[MSSQL] Migration record removed: {migrationId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] RemoveMigrationRecordAsync failed: {ex.Message}", ex);
            return false;
        }
    }
}

/// <summary>
/// Represents a single applied migration record from the tracking table.
/// </summary>
public record MigrationRecord(
    int Id,
    string MigrationId,
    string? Description,
    DateTime AppliedAt,
    string? Checksum);
