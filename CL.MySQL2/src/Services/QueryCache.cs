using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CL.MySQL2.Models;

namespace CL.MySQL2.Services;

/// <summary>
/// Facade over <see cref="ICacheStore"/> providing query-result caching with two key
/// properties:
/// <list type="bullet">
///   <item><b>Time-quantized keys</b> — DateTime parameters derived from <c>UtcNow</c>
///     are rounded to a configurable window before hashing, so
///     <c>.Where(x =&gt; x.At &gt;= UtcNow.AddDays(-30))</c> no longer produces a unique key per call.</item>
///   <item><b>Table-version invalidation</b> — mutations bump a per-table version counter
///     that participates in the cache key; prior entries simply become un-hittable and
///     are swept on eviction. No need to track-and-evict individual keys.</item>
/// </list>
/// Keeps a static facade so existing callers (<c>QueryBuilder</c>, <c>Repository</c>)
/// compile unchanged.
/// </summary>
public static class QueryCache
{
    private static ICacheStore _store = new InProcessCacheStore();
    private static readonly ConcurrentDictionary<string, long> _tableVersions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How far from <c>UtcNow</c> a DateTime parameter must be to qualify for
    /// quantization. 30 days covers typical "last N days" windows without catching
    /// far-future / far-past absolute dates.</summary>
    private static readonly TimeSpan QuantizeRelevance = TimeSpan.FromDays(365);

    /// <summary>Current quantization window. 0 = off.</summary>
    internal static int TimeQuantizeSeconds { get; private set; } = 60;

    /// <summary>Whether the cache is enabled globally.</summary>
    internal static bool Enabled { get; private set; } = true;

    /// <summary>Replace the underlying store (e.g. with a Redis adapter).</summary>
    public static void UseStore(ICacheStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Apply runtime configuration from <see cref="Configuration.CacheConfiguration"/>.</summary>
    public static void Configure(bool enabled, int maxEntries, int timeQuantizeSeconds)
    {
        Enabled = enabled;
        TimeQuantizeSeconds = Math.Max(0, timeQuantizeSeconds);
        if (_store is InProcessCacheStore inProc) inProc.Configure(maxEntries);
    }

    /// <summary>Total cached entries (best-effort).</summary>
    public static int Count => _store.Count;

    /// <summary>
    /// Cache-aside helper. Returns the cached value if fresh; otherwise runs <paramref name="factory"/>,
    /// stores the result, and returns it.
    /// </summary>
    public static async Task<T> GetOrSetAsync<T>(
        string cacheKey, string tableName, Func<Task<T>> factory, TimeSpan ttl)
    {
        if (!Enabled) return await factory().ConfigureAwait(false);

        var (found, value) = await _store.TryGetAsync(cacheKey).ConfigureAwait(false);
        if (found) return (T)value!;

        var result = await factory().ConfigureAwait(false);
        if (result is not null)
            await _store.SetAsync(cacheKey, result, ttl, tableName).ConfigureAwait(false);
        return result;
    }

    /// <summary>Invalidate all cached entries for the given table.</summary>
    public static void Invalidate(string tableName) =>
        _tableVersions.AddOrUpdate(tableName, 1L, (_, v) => v + 1);

    /// <summary>Invalidate for the table behind entity type <typeparamref name="T"/>.</summary>
    public static void Invalidate<T>() where T : class => Invalidate(ResolveTableName<T>());

    /// <summary>Clear the entire cache (admin / tests).</summary>
    public static void Clear()
    {
        _store.Clear();
        _tableVersions.Clear();
    }

    /// <summary>
    /// Build a deterministic cache key from a SQL query, its parameters, and the table it
    /// reads from. DateTime parameters close to "now" are quantized to the configured
    /// window. The table's current version is mixed in so invalidation is free.
    /// </summary>
    internal static string BuildCacheKey(
        string connectionId,
        string tableName,
        string sql,
        Dictionary<string, object?> parameters)
    {
        var version = _tableVersions.TryGetValue(tableName, out var v) ? v : 0L;

        var sb = new StringBuilder();
        sb.Append(connectionId).Append('|').Append(tableName).Append('|')
          .Append(version).Append('|').Append(sql);

        foreach (var kv in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append('|').Append(kv.Key).Append('=');
            sb.Append(StringifyForKey(kv.Value));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    private static string StringifyForKey(object? value)
    {
        if (value is null) return "NULL";

        // DateTime near now → quantize to the configured window so that
        // `Where(x => x.At >= DateTime.UtcNow.AddDays(-30))` produces a stable key
        // across back-to-back calls. See NEWSHAPE.md §Cache key.
        if (value is DateTime dt && TimeQuantizeSeconds > 0)
        {
            var delta = DateTime.UtcNow - dt;
            if (delta.Duration() < QuantizeRelevance)
            {
                var q = TimeQuantizeSeconds;
                var ticksPerBucket = TimeSpan.FromSeconds(q).Ticks;
                var rounded = dt.Ticks - (dt.Ticks % ticksPerBucket);
                return "DTQ:" + new DateTime(rounded, dt.Kind).ToString("O");
            }
        }

        return value.ToString() ?? "NULL";
    }

    internal static string ResolveTableName<T>() where T : class
    {
        var attr = typeof(T).GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : typeof(T).Name;
    }
}
