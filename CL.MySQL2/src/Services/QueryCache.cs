using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CL.MySQL2.Models;

namespace CL.MySQL2.Services;

/// <summary>
/// Process-local, thread-safe query result cache with TTL-based expiry and table-level
/// invalidation. Opt-in via <see cref="QueryBuilder{T}.WithCache"/> — queries without it
/// execute exactly as before with zero overhead.
/// <para>
/// Also usable as a standalone generic cache via <see cref="GetOrSetAsync{T}"/> for any
/// expensive computation keyed by a string.
/// </para>
/// </summary>
public static class QueryCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tableIndex = new();
    private static int _maxEntries = 1000;
    private static int _evicting;

    /// <summary>Total number of cached entries.</summary>
    public static int Count => _cache.Count;

    /// <summary>Number of cached entries associated with a specific table.</summary>
    public static int CountForTable(string tableName) =>
        _tableIndex.TryGetValue(tableName, out var keys) ? keys.Count : 0;

    /// <summary>Set the maximum number of cache entries (default 1000). Soft cap — enforced lazily on add.</summary>
    public static void Configure(int maxEntries) => _maxEntries = Math.Max(1, maxEntries);

    /// <summary>
    /// Generic cache-aside helper. Returns the cached value if fresh; otherwise runs
    /// <paramref name="factory"/>, stores the result, and returns it.
    /// </summary>
    /// <typeparam name="T">The result type (List, PagedResult, long, any object).</typeparam>
    /// <param name="cacheKey">Unique key for this query/computation (use <see cref="BuildCacheKey"/> for SQL queries).</param>
    /// <param name="tableName">Logical group name for invalidation (e.g. the MySQL table name).</param>
    /// <param name="factory">The async function that produces the value on cache miss.</param>
    /// <param name="ttl">How long the cached value stays fresh.</param>
    public static async Task<T> GetOrSetAsync<T>(
        string cacheKey, string tableName, Func<Task<T>> factory, TimeSpan ttl)
    {
        if (_cache.TryGetValue(cacheKey, out var entry) && !entry.IsExpired)
            return (T)entry.Value!;

        var result = await factory().ConfigureAwait(false);

        _cache[cacheKey] = new CacheEntry(result, DateTime.UtcNow, ttl, tableName);

        var tableKeys = _tableIndex.GetOrAdd(tableName, _ => new ConcurrentDictionary<string, byte>());
        tableKeys[cacheKey] = 0;

        if (_cache.Count > _maxEntries) EvictExpiredAndOldest();

        return result;
    }

    /// <summary>Evict all cached entries associated with the given table name.</summary>
    public static void Invalidate(string tableName)
    {
        if (!_tableIndex.TryRemove(tableName, out var keys)) return;
        foreach (var key in keys.Keys)
            _cache.TryRemove(key, out _);
    }

    /// <summary>Evict all cached entries for the table associated with entity type <typeparamref name="T"/>.</summary>
    public static void Invalidate<T>() where T : class => Invalidate(ResolveTableName<T>());

    /// <summary>Evict everything.</summary>
    public static void Clear()
    {
        _cache.Clear();
        _tableIndex.Clear();
    }

    /// <summary>
    /// Build a deterministic cache key from a SQL query and its parameters.
    /// Same SQL + same param values + same connectionId → same key.
    /// </summary>
    internal static string BuildCacheKey(string connectionId, string sql, Dictionary<string, object?> parameters)
    {
        var sb = new StringBuilder();
        sb.Append(connectionId).Append('|').Append(sql);
        foreach (var kv in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value?.ToString() ?? "NULL");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    internal static string ResolveTableName<T>() where T : class
    {
        var attr = typeof(T).GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : typeof(T).Name;
    }

    private static void EvictExpiredAndOldest()
    {
        // Only one thread evicts at a time; others just skip.
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0) return;

        try
        {
            // Pass 1: purge expired
            var expired = _cache.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                if (_cache.TryRemove(key, out var entry))
                {
                    if (_tableIndex.TryGetValue(entry.TableName, out var tbl))
                        tbl.TryRemove(key, out _);
                }
            }

            if (_cache.Count <= _maxEntries) return;

            // Pass 2: evict oldest 25% by CachedAt
            var toRemove = _cache
                .OrderBy(kv => kv.Value.CachedAt)
                .Take(_cache.Count / 4)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                if (_cache.TryRemove(key, out var entry))
                {
                    if (_tableIndex.TryGetValue(entry.TableName, out var tbl))
                        tbl.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _evicting, 0);
        }
    }

    private sealed record CacheEntry(object? Value, DateTime CachedAt, TimeSpan Ttl, string TableName)
    {
        public bool IsExpired => DateTime.UtcNow - CachedAt > Ttl;
    }
}
