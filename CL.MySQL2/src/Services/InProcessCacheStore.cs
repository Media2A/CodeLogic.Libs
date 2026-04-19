using System.Collections.Concurrent;

namespace CL.MySQL2.Services;

/// <summary>
/// Default <see cref="ICacheStore"/>: a thread-safe, process-local dictionary with
/// TTL-based expiry and a soft cap on total entries. Eviction is lazy: expired entries
/// are purged during <c>Set</c> once the cap is exceeded, and the oldest 25% go with them.
/// </summary>
internal sealed class InProcessCacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private int _maxEntries;
    private int _evicting;

    public InProcessCacheStore(int maxEntries = 10_000)
    {
        _maxEntries = Math.Max(1, maxEntries);
    }

    public void Configure(int maxEntries) => _maxEntries = Math.Max(1, maxEntries);

    public int Count => _entries.Count;

    public Task<(bool Found, object? Value)> TryGetAsync(string key, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(key, out var entry) && !entry.IsExpired)
            return Task.FromResult((true, (object?)entry.Value));
        return Task.FromResult<(bool, object?)>((false, null));
    }

    public Task SetAsync(string key, object value, TimeSpan ttl, string tableName, CancellationToken ct = default)
    {
        _entries[key] = new CacheEntry(value, DateTime.UtcNow, ttl, tableName);
        if (_entries.Count > _maxEntries) EvictExpiredAndOldest();
        return Task.CompletedTask;
    }

    public Task EvictAsync(string key, CancellationToken ct = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public void Clear() => _entries.Clear();

    private void EvictExpiredAndOldest()
    {
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0) return;
        try
        {
            // Pass 1: purge expired.
            foreach (var kv in _entries)
                if (kv.Value.IsExpired) _entries.TryRemove(kv.Key, out _);

            if (_entries.Count <= _maxEntries) return;

            // Pass 2: drop oldest 25% by cached-at.
            var victims = _entries
                .OrderBy(kv => kv.Value.CachedAt)
                .Take(Math.Max(1, _entries.Count / 4))
                .Select(kv => kv.Key)
                .ToArray();
            foreach (var k in victims) _entries.TryRemove(k, out _);
        }
        finally { Interlocked.Exchange(ref _evicting, 0); }
    }

    private sealed record CacheEntry(object Value, DateTime CachedAt, TimeSpan Ttl, string TableName)
    {
        public bool IsExpired => DateTime.UtcNow - CachedAt > Ttl;
    }
}
