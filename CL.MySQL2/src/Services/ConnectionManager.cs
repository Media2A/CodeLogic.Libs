using System.Collections.Concurrent;
using CL.MySQL2.Configuration;
using CL.MySQL2.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Manages MySQL database connections — registration, pooling, health checking,
/// and transaction orchestration for multiple named connection IDs.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ILogger? _logger;
    private readonly IEventBus? _events;

    // Per-connection-id configuration storage
    private readonly Dictionary<string, DatabaseConfiguration> _configs = new(StringComparer.OrdinalIgnoreCase);

    // Per-connection-id open connection counter
    private readonly ConcurrentDictionary<string, int> _openCounts = new(StringComparer.OrdinalIgnoreCase);

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a ConnectionManager with an optional default configuration.
    /// </summary>
    /// <param name="config">Default connection configuration (registered as "Default").</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="events">Optional event bus for publishing connection events.</param>
    public ConnectionManager(
        DatabaseConfiguration config,
        ILogger? logger = null,
        IEventBus? events = null)
    {
        _logger = logger;
        _events = events;
        RegisterConfiguration(config);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>
    /// Registers (or replaces) a connection configuration under a given ID.
    /// </summary>
    public void RegisterConfiguration(DatabaseConfiguration config, string connectionId = "Default")
    {
        ArgumentNullException.ThrowIfNull(config);
        _configs[connectionId] = config;
        _logger?.Debug($"[MySQL2] Configuration registered for '{connectionId}' → {config.Host}:{config.Port}/{config.Database}");
    }

    /// <summary>Returns the configuration for the given connection ID, or null if not found.</summary>
    public DatabaseConfiguration? GetConfiguration(string connectionId = "Default")
        => _configs.TryGetValue(connectionId, out var cfg) ? cfg : null;

    /// <summary>Returns true when a configuration exists for the given connection ID.</summary>
    public bool HasConfiguration(string connectionId = "Default")
        => _configs.ContainsKey(connectionId);

    /// <summary>Builds the ADO.NET connection string for the given connection ID.</summary>
    public string GetConnectionString(string connectionId = "Default")
        => RequireConfig(connectionId).BuildConnectionString();

    // ── Connection lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Opens and returns a new <see cref="MySqlConnection"/> for the given connection ID.
    /// Publishes a <see cref="DatabaseConnectedEvent"/> on success.
    /// </summary>
    public async Task<MySqlConnection> OpenConnectionAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        var config = RequireConfig(connectionId);
        var connection = new MySqlConnection(config.BuildConnectionString());

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 1, (_, v) => v + 1);
            _logger?.Debug($"[MySQL2] Connection opened for '{connectionId}'");

            if (_events is not null)
            {
                await _events.PublishAsync(new DatabaseConnectedEvent(
                    connectionId, config.Host, config.Port, config.Database, DateTime.UtcNow))
                    .ConfigureAwait(false);
            }

            return connection;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] Failed to open connection for '{connectionId}': {ex.Message}", ex);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Closes the connection and publishes a <see cref="DatabaseDisconnectedEvent"/>.
    /// </summary>
    public async Task CloseConnectionAsync(MySqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Decrement open count for whatever connection ID this belongs to
        // (we track by string ID; the physical connection state is managed by the pool)
        var connectionId = FindConnectionId(connection.ConnectionString) ?? "Default";

        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _openCounts.AddOrUpdate(connectionId, 0, (_, v) => Math.Max(0, v - 1));
            _logger?.Debug($"[MySQL2] Connection closed for '{connectionId}'");

            if (_events is not null)
            {
                await _events.PublishAsync(new DatabaseDisconnectedEvent(connectionId, DateTime.UtcNow))
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tests connectivity for the given connection ID.
    /// Returns true on success, false on failure (does not throw).
    /// </summary>
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
            await CloseConnectionAsync(conn).ConfigureAwait(false);
            _logger?.Info($"[MySQL2] Connection test passed for '{connectionId}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[MySQL2] Connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    // ── Higher-order helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Opens a connection, executes the given action, closes the connection, and returns the result.
    /// </summary>
    public async Task<TResult> ExecuteWithConnectionAsync<TResult>(
        Func<MySqlConnection, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        try
        {
            return await action(conn).ConfigureAwait(false);
        }
        finally
        {
            await conn.CloseAsync().ConfigureAwait(false);
            _openCounts.AddOrUpdate(connectionId, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    /// <summary>
    /// Opens a connection, begins a transaction, executes the action, commits, and returns the result.
    /// Rolls back automatically on exception.
    /// </summary>
    public async Task<TResult> ExecuteWithTransactionAsync<TResult>(
        Func<MySqlConnection, MySqlTransaction, Task<TResult>> action,
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var conn = await OpenConnectionAsync(connectionId, ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await action(conn, tx).ConfigureAwait(false);
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

    // ── Server info ───────────────────────────────────────────────────────────

    /// <summary>Retrieves version and database metadata from the MySQL server.</summary>
    public async Task<ServerInfo> GetServerInfoAsync(
        string connectionId = "Default",
        CancellationToken ct = default)
    {
        return await ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT VERSION(), @@version_comment, DATABASE(), @@hostname";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new ServerInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
            }
            return new ServerInfo(string.Empty, string.Empty, string.Empty, string.Empty);
        }, connectionId, ct).ConfigureAwait(false);
    }

    // ── Counters ──────────────────────────────────────────────────────────────

    /// <summary>Returns the current open connection count for the given connection ID.</summary>
    public int GetOpenConnectionCount(string connectionId = "Default")
        => _openCounts.TryGetValue(connectionId, out var v) ? v : 0;

    /// <summary>Returns a snapshot of open connection counts for all registered IDs.</summary>
    public IReadOnlyDictionary<string, int> GetAllConnectionCounts()
        => new Dictionary<string, int>(_openCounts, StringComparer.OrdinalIgnoreCase);

    // ── Private helpers ───────────────────────────────────────────────────────

    private DatabaseConfiguration RequireConfig(string connectionId)
    {
        if (_configs.TryGetValue(connectionId, out var cfg)) return cfg;
        throw new InvalidOperationException(
            $"No database configuration registered for connection ID '{connectionId}'. " +
            $"Call RegisterConfiguration first.");
    }

    private string? FindConnectionId(string connectionString)
    {
        foreach (var kv in _configs)
        {
            if (string.Equals(kv.Value.BuildConnectionString(), connectionString, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }
}

/// <summary>Basic MySQL server metadata.</summary>
public record ServerInfo(string Version, string Comment, string Database, string Host);
