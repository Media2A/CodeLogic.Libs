using System.Collections.Concurrent;
using CL.SQLite.Models;
using CodeLogic.Core.Logging;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Manages a pool of SQLite connections for a single database file.
/// Uses a <see cref="ConcurrentStack{T}"/> of <see cref="PooledConnection"/> objects
/// to reuse connections across operations.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly SQLiteConfig _config;
    private readonly ILogger? _logger;
    private readonly string _databasePath;
    private readonly ConcurrentStack<PooledConnection> _pool = new();
    private int _activeCount;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="ConnectionManager"/> and ensures the database directory exists.
    /// </summary>
    /// <param name="config">SQLite configuration (database path, pool size, pragmas).</param>
    /// <param name="logger">Optional logger for connection lifecycle events.</param>
    /// <param name="dataDirectory">
    /// Optional base directory prepended to a relative <see cref="SQLiteConfig.DatabasePath"/>.
    /// </param>
    public ConnectionManager(
        SQLiteConfig config,
        ILogger? logger = null,
        string? dataDirectory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;

        if (dataDirectory is not null && !Path.IsPathRooted(config.DatabasePath))
            _databasePath = Path.Combine(dataDirectory, config.DatabasePath);
        else
            _databasePath = config.DatabasePath;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Gets the resolved absolute path to the SQLite database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>Gets the number of connections currently checked out from the pool.</summary>
    public int ActiveConnectionCount => _activeCount;

    /// <summary>Gets the number of idle connections currently held in the pool.</summary>
    public int PooledConnectionCount => _pool.Count;

    /// <summary>
    /// Obtains an open <see cref="SqliteConnection"/> from the pool, or creates a new one if the pool is empty.
    /// The caller is responsible for returning the connection via <see cref="ReleaseConnectionAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open <see cref="SqliteConnection"/> ready for use.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has been disposed.</exception>
    public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Try to reuse a pooled connection
        while (_pool.TryPop(out var pooled))
        {
            if (pooled.IsValid() && !pooled.IsIdleTooLong())
            {
                pooled.MarkInUse();
                Interlocked.Increment(ref _activeCount);
                _logger?.Debug(_config.ToString() ?? "Reused pooled connection");
                return pooled.Connection;
            }
            // Invalid or idle too long — dispose and try next
            pooled.Connection.Dispose();
        }

        // Create a new connection
        var conn = await CreateConnectionAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _activeCount);
        return conn;
    }

    /// <summary>
    /// Returns a connection to the pool. If the pool is at capacity the connection is disposed instead.
    /// </summary>
    /// <param name="connection">The connection to release. Disposed immediately if the pool is full or the manager is disposed.</param>
    /// <returns>A completed task.</returns>
    public Task ReleaseConnectionAsync(SqliteConnection connection)
    {
        if (connection is null || _disposed)
        {
            connection?.Dispose();
            return Task.CompletedTask;
        }

        Interlocked.Decrement(ref _activeCount);

        if (_pool.Count < _config.MaxPoolSize)
        {
            var pooled = new PooledConnection(connection);
            pooled.MarkAvailable();
            _pool.Push(pooled);
            _logger?.Debug("Connection returned to pool");
        }
        else
        {
            connection.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Borrows a connection, executes the given asynchronous function, then returns the connection to the pool.
    /// </summary>
    /// <typeparam name="T">The type of value produced by <paramref name="action"/>.</typeparam>
    /// <param name="action">The asynchronous function to execute with the borrowed connection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value returned by <paramref name="action"/>.</returns>
    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseConnectionAsync(conn).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Borrows a connection, executes the given asynchronous action, then returns the connection to the pool.
    /// </summary>
    /// <param name="action">The asynchronous action to execute with the borrowed connection.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ExecuteAsync(Func<SqliteConnection, Task> action, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseConnectionAsync(conn).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes all pooled connections and marks the manager as disposed.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_pool.TryPop(out var pooled))
            pooled.Connection.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath
        };

        builder.Mode = _config.CacheMode switch
        {
            CacheMode.Shared  => SqliteOpenMode.ReadWriteCreate,
            CacheMode.Private => SqliteOpenMode.ReadWriteCreate,
            _                 => SqliteOpenMode.ReadWriteCreate
        };

        if (_config.CacheMode == CacheMode.Shared)
            builder.Cache = SqliteCacheMode.Shared;

        var conn = new SqliteConnection(builder.ToString());
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Configure pragmas
        if (_config.UseWAL)
        {
            await using var walCmd = conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (_config.EnableForeignKeys)
        {
            await using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await fkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger?.Debug($"Created new SQLite connection: {_databasePath}");
        return conn;
    }

    // ── Inner class: PooledConnection ────────────────────────────────────────

    private sealed class PooledConnection
    {
        private static readonly TimeSpan MaxIdleTime = TimeSpan.FromMinutes(5);

        public SqliteConnection Connection { get; }
        private DateTime _lastUsed;
        private bool _inUse;

        public PooledConnection(SqliteConnection connection)
        {
            Connection = connection;
            _lastUsed = DateTime.UtcNow;
        }

        public void MarkInUse()
        {
            _inUse = true;
            _lastUsed = DateTime.UtcNow;
        }

        public void MarkAvailable()
        {
            _inUse = false;
            _lastUsed = DateTime.UtcNow;
        }

        public bool IsValid() =>
            !_inUse &&
            Connection.State == System.Data.ConnectionState.Open;

        public bool IsIdleTooLong() =>
            DateTime.UtcNow - _lastUsed > MaxIdleTime;
    }
}
