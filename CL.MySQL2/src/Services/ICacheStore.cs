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

    /// <summary>
    /// Evicts every entry whose tableName matches. Used by
    /// <see cref="QueryCache.Invalidate(string)"/> so orphan entries (e.g.
    /// older table-version snapshots) don't accumulate after a mutation.
    /// Default implementation is a no-op for adapters that can't enumerate;
    /// the in-process store overrides this with an O(n) scan that's still
    /// cheap relative to the savings.
    /// </summary>
    Task<int> EvictByTableAsync(string tableName, CancellationToken ct = default);

    /// <summary>Total entries currently in the cache (best-effort).</summary>
    int Count { get; }

    /// <summary>
    /// Diagnostic snapshot — entry count grouped by tableName. Used by the
    /// admin UI to surface "what's actually in the cache" without dumping
    /// values. Default implementation returns an empty dict for adapters
    /// that can't enumerate.
    /// </summary>
    System.Collections.Generic.IReadOnlyDictionary<string, int> CountByTable();

    /// <summary>Wipes everything (tests, admin operations).</summary>
    void Clear();
}
