using System.Collections.Concurrent;

namespace CL.Common.Caching;

/// <summary>
/// Thread-safe in-process cache backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Supports optional TTL per entry and runs a background cleanup loop to evict expired entries.
/// Implements <see cref="ICache"/> and <see cref="IDisposable"/>.
/// </summary>
public sealed class MemoryCache : ICache, IDisposable
{
    private sealed class CacheEntry
    {
        /// <summary>The stored value (boxed).</summary>
        public required object? Value { get; init; }

        /// <summary>UTC time when this entry expires, or null if it never expires.</summary>
        public DateTime? ExpiresAt { get; init; }

        /// <summary>UTC time when this entry was created.</summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>Returns true if the entry has passed its expiry time.</summary>
        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _cleanupInterval;

    /// <summary>
    /// Initialises a new <see cref="MemoryCache"/> and starts the background cleanup loop.
    /// </summary>
    /// <param name="cleanupInterval">
    /// How often the cleanup loop runs to evict expired entries.
    /// Default: 60 seconds.
    /// </param>
    public MemoryCache(TimeSpan? cleanupInterval = null)
    {
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromSeconds(60);
        _ = Task.Run(CleanupLoopAsync, _cts.Token);
    }

    /// <inheritdoc/>
    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var entry = new CacheEntry
        {
            Value     = value,
            ExpiresAt = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : null,
            CreatedAt = DateTime.UtcNow
        };
        _store[key] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<T?> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _store.TryRemove(key, out _);
                return Task.FromResult<T?>(default);
            }
            return Task.FromResult(entry.Value is T typed ? typed : default);
        }
        return Task.FromResult<T?>(default);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.IsExpired)
            {
                _store.TryRemove(key, out _);
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveByPrefixAsync(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var keys = _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in keys)
            _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAsync()
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> GetCountAsync()
    {
        var count = _store.Values.Count(e => !e.IsExpired);
        return Task.FromResult(count);
    }

    private async Task CleanupLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, _cts.Token);
                EvictExpired();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void EvictExpired()
    {
        var expiredKeys = _store
            .Where(kv => kv.Value.IsExpired)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expiredKeys)
            _store.TryRemove(key, out _);
    }

    /// <summary>
    /// Cancels the background cleanup loop and releases all resources.
    /// </summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
