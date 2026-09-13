using System.Security.Cryptography;
using System.Text;
using CodeLogic.Core.Logging;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// A cross-node advisory lock around a schema-sync / migration pass, implemented with
/// PostgreSQL's session-scoped <c>pg_advisory_lock</c>. Because the lock is released
/// automatically when the holding session ends, this type keeps a single dedicated
/// connection open for its whole lifetime and releases the lock (and the connection) on
/// <see cref="DisposeAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Multiple application nodes booting at once all contend for the same lock key; the winner
/// runs the DDL pass while the others wait, then find the schema already reconciled (matching
/// CRCs) and do nothing. Acquire via <see cref="AcquireAsync"/>; always check <see cref="Acquired"/>.
/// </para>
/// <para>
/// PostgreSQL advisory locks are keyed by a 64-bit integer rather than a string, so the lock
/// name is hashed. The key space is global to the database, which is why the hash is taken
/// over a namespaced name rather than a bare one.
/// </para>
/// </remarks>
public sealed class SchemaSyncLock : IAsyncDisposable
{
    /// <summary>The well-known lock name. Shared by declarative sync and the migration runner.</summary>
    public const string LockName = "cl_postgresql_schema_sync";

    private readonly NpgsqlConnection _connection;
    private readonly string _lockName;
    private readonly long _lockKey;
    private readonly ILogger? _logger;
    private bool _released;

    private SchemaSyncLock(NpgsqlConnection connection, string lockName, long lockKey, bool acquired, ILogger? logger)
    {
        _connection = connection;
        _lockName = lockName;
        _lockKey = lockKey;
        Acquired = acquired;
        _logger = logger;
    }

    /// <summary>True when the advisory lock was actually obtained within the timeout.</summary>
    public bool Acquired { get; }

    /// <summary>
    /// Derives the 64-bit advisory-lock key for a name. Uses the first 8 bytes of the SHA-256
    /// digest so the mapping is stable across processes, platforms and releases — unlike
    /// <see cref="string.GetHashCode()"/>, which is randomised per process and would let two
    /// nodes take "the same" lock under different keys.
    /// </summary>
    internal static long KeyFor(string lockName)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(lockName));
        return BitConverter.ToInt64(digest, 0);
    }

    /// <summary>
    /// Opens a dedicated connection and attempts to take the advisory lock, giving up after
    /// <paramref name="timeoutSeconds"/>. The returned instance must be disposed to release the
    /// lock — even when <see cref="Acquired"/> is false (the connection still needs closing).
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
        var key = KeyFor(name);
        var conn = await connectionManager.OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);

        try
        {
            // pg_advisory_lock blocks indefinitely and takes no timeout argument, so the wait
            // is bounded with lock_timeout for this session only. pg_try_advisory_lock would
            // avoid that but returns immediately, turning a slow peer into a skipped sync.
            await using (var timeout = conn.CreateCommand())
            {
                timeout.CommandText = "SET lock_timeout = @ms";
                timeout.Parameters.AddWithValue("@ms", Math.Max(0, timeoutSeconds) * 1000);
                await timeout.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var acquired = false;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_lock(@key)";
                cmd.Parameters.AddWithValue("@key", key);
                await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                acquired = true;
            }
            catch (PostgresException ex) when (ex.SqlState == "55P03")
            {
                // lock_not_available: the lock_timeout elapsed while another node held it.
                acquired = false;
            }
            finally
            {
                // Leave the session's lock_timeout as we found it; this connection is dedicated
                // to the lock, but it returns to the pool on dispose.
                await using var reset = conn.CreateCommand();
                reset.CommandText = "SET lock_timeout = DEFAULT";
                await reset.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (acquired)
                logger?.Debug($"[PostgreSQL] Acquired schema-sync lock '{name}' (key {key})");
            else
                logger?.Warning(
                    $"[PostgreSQL] Could not acquire schema-sync lock '{name}' within {timeoutSeconds}s — another node may be syncing.");

            return new SchemaSyncLock(conn, name, key, acquired, logger);
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
                    cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                    cmd.Parameters.AddWithValue("@key", _lockKey);
                    await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    _logger?.Debug($"[PostgreSQL] Released schema-sync lock '{_lockName}'");
                }
                catch (Exception ex)
                {
                    // Not fatal: closing the session below releases every advisory lock it holds.
                    _logger?.Warning($"[PostgreSQL] Failed to release schema-sync lock '{_lockName}': {ex.Message}");
                }
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
