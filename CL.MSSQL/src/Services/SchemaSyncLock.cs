using CodeLogic.Core.Logging;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

/// <summary>
/// A cross-node advisory lock around a schema-sync / migration pass, implemented with SQL Server's
/// connection-scoped <c>sp_getapplock</c>. Because the lock is released automatically when the holding
/// connection closes, this type keeps a single dedicated connection open for its whole lifetime and
/// releases the lock (and the connection) on <see cref="DisposeAsync"/>.
/// </summary>
/// <remarks>
/// Multiple application nodes booting at once all contend for the same named lock; the winner runs
/// the DDL pass while the others wait, then find the schema already reconciled (matching CRCs) and
/// do nothing. Acquire via <see cref="AcquireAsync"/>; always check <see cref="Acquired"/>.
/// </remarks>
public sealed class SchemaSyncLock : IAsyncDisposable
{
    /// <summary>The well-known lock name. Shared by declarative sync and the migration runner.</summary>
    public const string LockName = "clmssql2_schema_sync";

    private readonly SqlConnection _connection;
    private readonly string _lockName;
    private readonly ILogger? _logger;
    private bool _released;

    private SchemaSyncLock(SqlConnection connection, string lockName, bool acquired, ILogger? logger)
    {
        _connection = connection;
        _lockName = lockName;
        Acquired = acquired;
        _logger = logger;
    }

    /// <summary>True when the advisory lock was actually obtained within the timeout.</summary>
    public bool Acquired { get; }

    /// <summary>
    /// Opens a dedicated connection and attempts <c>sp_getapplock(name, timeout)</c>. The returned
    /// instance must be disposed to release the lock — even when <see cref="Acquired"/> is false
    /// (the connection still needs closing).
    /// </summary>
    /// <param name="connectionManager">Connection source.</param>
    /// <param name="connectionId">Connection ID to lock on.</param>
    /// <param name="timeoutSeconds">How long to wait for the lock before giving up. Default 30s.</param>
    /// <param name="lockName">Advisory lock name. Defaults to <see cref="LockName"/>.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<SchemaSyncLock> AcquireAsync(
        ConnectionManager connectionManager,
        string connectionId = "Default",
        int timeoutSeconds = 30,
        string? lockName = null,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionManager);
        var name = lockName ?? LockName;
        var conn = await connectionManager.OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=@name, @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=@timeout; SELECT @result;";
            cmd.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 255) { Value = name });
            cmd.Parameters.Add(new SqlParameter("@timeout", System.Data.SqlDbType.Int) { Value = checked(timeoutSeconds * 1000) });
            // sp_getapplock returns 1 = acquired, 0 = timed out, NULL = error.
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var acquired = result is not null and not DBNull && Convert.ToInt32(result) >= 0;

            if (acquired)
                logger?.Debug($"[MSSQL] Acquired schema-sync lock '{name}'");
            else
                logger?.Warning($"[MSSQL] Could not acquire schema-sync lock '{name}' within {timeoutSeconds}s — another node may be syncing.");

            return new SchemaSyncLock(conn, name, acquired, logger);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Releases the advisory lock (if held) and closes the dedicated connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;
            if (Acquired)
            {
                try
                {
                    await using var cmd = _connection.CreateCommand();
                    cmd.CommandText = "DECLARE @result int; EXEC @result = sys.sp_releaseapplock @Resource=@name, @LockOwner='Session'; SELECT @result;";
                    cmd.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 255) { Value = _lockName });
                    await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    _logger?.Debug($"[MSSQL] Released schema-sync lock '{_lockName}'");
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"[MSSQL] Failed to release schema-sync lock '{_lockName}': {ex.Message}");
                }
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
