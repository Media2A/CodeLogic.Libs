using System.Collections.Concurrent;
using CL.SQLite.Models;
using CodeLogic.Core.Logging;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Manages pools of SQLite connections for multiple named database files.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly ILogger? _logger;
    private readonly string? _dataDirectory;
    private readonly ConcurrentDictionary<string, DatabaseState> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ConnectionManager(ILogger? logger = null, string? dataDirectory = null)
    {
        _logger = logger;
        _dataDirectory = dataDirectory;
    }

    public void RegisterConfiguration(string connectionId, SQLiteDatabaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var state = new DatabaseState(config, ResolveDatabasePath(config));
        EnsureDirectoryExists(state.DatabasePath);

        if (_states.TryRemove(connectionId, out var existing))
            existing.Dispose();

        _states[connectionId] = state;
        _logger?.Debug($"[SQLite] Configuration registered for '{connectionId}' -> {state.DatabasePath}");
    }

    public SQLiteDatabaseConfig GetConfiguration(string connectionId = "Default")
        => RequireState(connectionId).Config;

    public IEnumerable<string> GetConnectionIds() => _states.Keys;

    public string GetDatabasePath(string connectionId = "Default")
        => RequireState(connectionId).DatabasePath;

    public int GetActiveConnectionCount(string connectionId = "Default")
        => RequireState(connectionId).ActiveCount;

    public int GetPooledConnectionCount(string connectionId = "Default")
        => RequireState(connectionId).Pool.Count;

    public async Task<SqliteConnection> GetConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = RequireState(connectionId);

        while (state.Pool.TryPop(out var pooled))
        {
            if (pooled.IsValid() && !pooled.IsIdleTooLong())
            {
                pooled.MarkInUse();
                Interlocked.Increment(ref state.ActiveCount);
                _logger?.Debug($"[SQLite] Reused pooled connection for '{connectionId}'");
                return pooled.Connection;
            }

            pooled.Connection.Dispose();
        }

        var conn = await CreateConnectionAsync(state, ct).ConfigureAwait(false);
        Interlocked.Increment(ref state.ActiveCount);
        return conn;
    }

    public Task ReleaseConnectionAsync(
        SqliteConnection connection,
        string connectionId = "Default")
    {
        if (connection is null)
            return Task.CompletedTask;

        if (_disposed)
        {
            connection.Dispose();
            return Task.CompletedTask;
        }

        var state = RequireState(connectionId);
        Interlocked.Decrement(ref state.ActiveCount);

        if (state.Pool.Count < state.Config.MaxPoolSize)
        {
            var pooled = new PooledConnection(connection);
            pooled.MarkAvailable();
            state.Pool.Push(pooled);
            _logger?.Debug($"[SQLite] Connection returned to pool for '{connectionId}'");
        }
        else
        {
            connection.Dispose();
        }

        return Task.CompletedTask;
    }

    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, Task<T>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
        try
        {
            return await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseConnectionAsync(conn, connectionId).ConfigureAwait(false);
        }
    }

    public async Task ExecuteAsync(
        Func<SqliteConnection, Task> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
        try
        {
            await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseConnectionAsync(conn, connectionId).ConfigureAwait(false);
        }
    }

    public async Task<bool> TestConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            var conn = await GetConnectionAsync(connectionId, ct).ConfigureAwait(false);
            await ReleaseConnectionAsync(conn, connectionId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[SQLite] Connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var state in _states.Values)
            state.Dispose();

        _states.Clear();
    }

    private DatabaseState RequireState(string connectionId)
    {
        if (_states.TryGetValue(connectionId, out var state))
            return state;

        throw new InvalidOperationException(
            $"No SQLite configuration registered for connection ID '{connectionId}'.");
    }

    private string ResolveDatabasePath(SQLiteDatabaseConfig config)
    {
        if (_dataDirectory is not null && !Path.IsPathRooted(config.DatabasePath))
            return Path.Combine(_dataDirectory, config.DatabasePath);

        return config.DatabasePath;
    }

    private static void EnsureDirectoryExists(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private async Task<SqliteConnection> CreateConnectionAsync(DatabaseState state, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = state.DatabasePath
        };

        builder.Mode = state.Config.CacheMode switch
        {
            CacheMode.Shared => SqliteOpenMode.ReadWriteCreate,
            CacheMode.Private => SqliteOpenMode.ReadWriteCreate,
            _ => SqliteOpenMode.ReadWriteCreate
        };

        if (state.Config.CacheMode == CacheMode.Shared)
            builder.Cache = SqliteCacheMode.Shared;

        var conn = new SqliteConnection(builder.ToString());
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (state.Config.UseWAL)
        {
            await using var walCmd = conn.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (state.Config.EnableForeignKeys)
        {
            await using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await fkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger?.Debug($"[SQLite] Created new connection for '{state.DatabasePath}'");
        return conn;
    }

    private sealed class DatabaseState : IDisposable
    {
        public DatabaseState(SQLiteDatabaseConfig config, string databasePath)
        {
            Config = config;
            DatabasePath = databasePath;
        }

        public SQLiteDatabaseConfig Config { get; }
        public string DatabasePath { get; }
        public ConcurrentStack<PooledConnection> Pool { get; } = new();
        public int ActiveCount;

        public void Dispose()
        {
            while (Pool.TryPop(out var pooled))
                pooled.Connection.Dispose();
        }
    }

    private sealed class PooledConnection
    {
        private static readonly TimeSpan MaxIdleTime = TimeSpan.FromMinutes(5);

        public PooledConnection(SqliteConnection connection)
        {
            Connection = connection;
            _lastUsed = DateTime.UtcNow;
        }

        public SqliteConnection Connection { get; }
        private DateTime _lastUsed;
        private bool _inUse;

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
