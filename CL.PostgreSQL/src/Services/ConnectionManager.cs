using System.Collections.Concurrent;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Manages PostgreSQL database connections — registration, pooling, health checking,
/// and transaction orchestration for multiple named connection IDs.
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;

    private readonly Dictionary<string, DatabaseConfig> _configs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, string> _connectionStringCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, int> _openCounts =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public ConnectionManager(ILogger? logger = null, IEventBus? events = null)
    {
        _logger = logger;
        _events = events;
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public void RegisterConfiguration(string connectionId, DatabaseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs[connectionId] = config;
        _connectionStringCache[connectionId] = config.BuildConnectionString();
        _logger?.Debug($"[PostgreSQL] Configuration registered for '{connectionId}' → {config.Host}:{config.Port}/{config.Database}");
    }

    public DatabaseConfig GetConfiguration(string connectionId = "Default")
        => RequireConfig(connectionId);

    public bool HasConfiguration(string connectionId = "Default")
        => _configs.ContainsKey(connectionId);

    public string GetConnectionString(string connectionId = "Default")
        => _connectionStringCache.TryGetValue(connectionId, out var cs)
            ? cs
            : RequireConfig(connectionId).BuildConnectionString();

    public IEnumerable<string> GetConnectionIds() => _configs.Keys;

    // ── Connection lifecycle ──────────────────────────────────────────────────

    public async Task<NpgsqlConnection> OpenConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var connStr = GetConnectionString(connectionId);
        var connection = new NpgsqlConnection(connStr);

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 1, (_, v) => v + 1);
            _logger?.Debug($"[PostgreSQL] Connection opened for '{connectionId}'");
            return connection;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] Failed to open connection for '{connectionId}': {ex.Message}", ex);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CloseConnectionAsync(NpgsqlConnection? connection)
    {
        if (connection is null) return;

        var connectionId = FindConnectionId(connection.ConnectionString) ?? "Default";
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _openCounts.AddOrUpdate(connectionId, 0, (_, v) => Math.Max(0, v - 1));
            _logger?.Debug($"[PostgreSQL] Connection closed for '{connectionId}'");

            if (_events is not null)
            {
                await _events.PublishAsync(new DatabaseDisconnectedEvent(connectionId, DateTime.UtcNow))
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> TestConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

            var serverVersion = conn.ServerVersion;
            _logger?.Info($"[PostgreSQL] Connection test passed for '{connectionId}' (v{serverVersion})");

            if (_events is not null)
            {
                var cfg = RequireConfig(connectionId);
                await _events.PublishAsync(new DatabaseConnectedEvent(
                    connectionId, cfg.Host, cfg.Port, cfg.Database, serverVersion, DateTime.UtcNow))
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[PostgreSQL] Connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    public async Task<(string ServerVersion, string Host, string Database)?> GetServerInfoAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        try
        {
            return await ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT version(), current_database(), inet_server_addr()::text";
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var version = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    var database = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var host = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    return ((string ServerVersion, string Host, string Database)?)(version, host, database);
                }
                return null;
            }, connectionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[PostgreSQL] GetServerInfoAsync failed for '{connectionId}': {ex.Message}");
            return null;
        }
    }

    public async Task<T> ExecuteWithConnectionAsync<T>(
        Func<NpgsqlConnection, Task<T>> func,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        await using var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        try
        {
            return await func(conn).ConfigureAwait(false);
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    public async Task<T> ExecuteWithTransactionAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> func,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        await using var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await func(conn, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    public int GetOpenConnectionCount(string connectionId = "Default")
        => _openCounts.TryGetValue(connectionId, out var v) ? v : 0;

    public IReadOnlyDictionary<string, int> GetAllConnectionCounts()
        => new Dictionary<string, int>(_openCounts, StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configs.Clear();
        _connectionStringCache.Clear();
        _openCounts.Clear();
    }

    private DatabaseConfig RequireConfig(string connectionId)
    {
        if (_configs.TryGetValue(connectionId, out var cfg)) return cfg;
        throw new InvalidOperationException(
            $"No database configuration registered for connection ID '{connectionId}'. " +
            $"Call RegisterConfiguration first.");
    }

    private string? FindConnectionId(string connectionString)
    {
        foreach (var kv in _connectionStringCache)
        {
            if (string.Equals(kv.Value, connectionString, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }
}
