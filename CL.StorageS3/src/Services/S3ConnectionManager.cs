using Amazon.S3;
using Amazon.S3.Model;
using CL.StorageS3.Models;
using CodeLogic.Core.Logging;

namespace CL.StorageS3.Services;

/// <summary>
/// Manages a cache of <see cref="AmazonS3Client"/> instances keyed by connection ID.
/// Thread-safe via a simple lock.
/// </summary>
public sealed class S3ConnectionManager : IDisposable
{
    private readonly Dictionary<string, S3ConnectionConfig> _configs = [];
    private readonly Dictionary<string, AmazonS3Client> _clients = [];
    private readonly object _lock = new();
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="S3ConnectionManager"/>.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public S3ConnectionManager(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a connection configuration. Replaces any existing config with the same <see cref="S3ConnectionConfig.ConnectionId"/>.
    /// </summary>
    /// <param name="config">Connection configuration to register.</param>
    public void RegisterConfiguration(S3ConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_lock)
        {
            // If a client already exists for this ID, dispose it so it gets recreated
            if (_clients.TryGetValue(config.ConnectionId, out var existingClient))
            {
                existingClient.Dispose();
                _clients.Remove(config.ConnectionId);
                _logger?.Debug($"Disposed existing S3 client for connection '{config.ConnectionId}' due to config update");
            }

            _configs[config.ConnectionId] = config;
            _logger?.Debug($"Registered S3 connection config '{config.ConnectionId}'");
        }
    }

    /// <summary>
    /// Returns a cached (or newly created) <see cref="AmazonS3Client"/> for the given connection ID.
    /// </summary>
    /// <param name="connectionId">Connection ID to look up. Defaults to <c>"Default"</c>.</param>
    /// <exception cref="InvalidOperationException">Thrown when no configuration is registered for the given ID.</exception>
    public AmazonS3Client GetClient(string connectionId = "Default")
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(connectionId, out var cached))
                return cached;

            if (!_configs.TryGetValue(connectionId, out var config))
                throw new InvalidOperationException($"No S3 connection configuration registered for ID '{connectionId}'");

            _logger?.Debug($"Creating new AmazonS3Client for connection '{connectionId}'");
            var client = config.BuildClient();
            _clients[connectionId] = client;
            return client;
        }
    }

    /// <summary>
    /// Tests whether the connection identified by <paramref name="connectionId"/> is reachable
    /// by performing a lightweight <c>ListBuckets</c> call.
    /// </summary>
    /// <param name="connectionId">Connection ID to test. Defaults to <c>"Default"</c>.</param>
    /// <returns><see langword="true"/> when the call succeeds; <see langword="false"/> on error.</returns>
    public async Task<bool> TestConnectionAsync(string connectionId = "Default")
    {
        try
        {
            var client = GetClient(connectionId);
            await client.ListBucketsAsync();
            _logger?.Debug($"S3 connection test succeeded for '{connectionId}'");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"S3 connection test failed for '{connectionId}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Returns the <see cref="S3ConnectionConfig"/> registered under <paramref name="connectionId"/>,
    /// or <see langword="null"/> when not found.
    /// </summary>
    public S3ConnectionConfig? GetConfiguration(string connectionId = "Default")
    {
        lock (_lock)
        {
            return _configs.TryGetValue(connectionId, out var config) ? config : null;
        }
    }

    /// <summary>
    /// Returns all registered connection IDs.
    /// </summary>
    public List<string> GetConnectionIds()
    {
        lock (_lock)
        {
            return [.. _configs.Keys];
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var client in _clients.Values)
            {
                try { client.Dispose(); }
                catch { /* best effort */ }
            }
            _clients.Clear();
        }

        _logger?.Debug("S3ConnectionManager disposed all clients");
    }
}
