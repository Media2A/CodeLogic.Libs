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

    public string DatabasePath => _databasePath;
    public int ActiveConnectionCount => _activeCount;
    public int PooledConnectionCount => _pool.Count;

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
