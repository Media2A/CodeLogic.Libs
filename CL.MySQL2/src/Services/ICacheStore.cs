namespace CL.MySQL2.Services;

/// <summary>
/// Abstraction over the cache backend. The default implementation is in-process
/// (<see cref="InProcessCacheStore"/>). Distributed adapters (Redis, memcached) can
/// implement this without touching callers.
/// </summary>
public interface ICacheStore
{
    /// <summary>Attempts to read a cached value. Returns false if missing or expired.</summary>
    Task<(bool Found, object? Value)> TryGetAsync(string key, CancellationToken ct = default);

    /// <summary>Stores a value with a TTL.</summary>
    Task SetAsync(string key, object value, TimeSpan ttl, string tableName, CancellationToken ct = default);

    /// <summary>Evicts a single key.</summary>
    Task EvictAsync(string key, CancellationToken ct = default);

    /// <summary>Total entries currently in the cache (best-effort).</summary>
    int Count { get; }

    /// <summary>Wipes everything (tests, admin operations).</summary>
    void Clear();
}
