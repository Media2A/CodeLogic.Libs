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
    private static ICacheCoordinator _coordinator = NullCacheCoordinator.Instance;
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

    /// <summary>
    /// Installs a multi-node <see cref="ICacheCoordinator"/> (e.g. a Redis pub/sub adapter)
    /// so mutations fan out to peers and smart-cache pools refresh single-flight. Wires the
    /// coordinator's peer-invalidation callback to the local (non-broadcasting) invalidation
    /// path. Pair with a shared <see cref="ICacheStore"/> via <see cref="UseStore"/>.
    /// </summary>
    public static void UseCoordinator(ICacheCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _coordinator.OnInvalidation(InvalidateFromPeer);
    }

    /// <summary>The active coordinator. Single-node no-op unless <see cref="UseCoordinator"/> ran.</summary>
    internal static ICacheCoordinator Coordinator => _coordinator;

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
    /// <para>
    /// Failure <see cref="CodeLogic.Core.Results.Result{T}"/> values are NEVER cached and never
    /// served as cache hits — a transient DB failure during a cold-warmup cannot poison the
    /// cache. If a previously-cached value is detected as a failure (legacy entries from older
    /// versions), it's evicted on read so the call falls through to a fresh execution.
    /// </para>
    /// </summary>
    public static async Task<T> GetOrSetAsync<T>(
        string cacheKey, string tableName, Func<Task<T>> factory, TimeSpan ttl,
        string? connectionId = null)
    {
        if (!Enabled) return await factory().ConfigureAwait(false);

        var (found, value) = await _store.TryGetAsync(cacheKey).ConfigureAwait(false);
        if (found && !IsFailureResult(value))
        {
            if (connectionId is not null)
                QueryObservability.RecordCacheHit(connectionId, tableName, cacheKey);
            return (T)value!;
        }

        // Poisoned entry: evict so subsequent reads don't re-hit it before
        // the eventual pool refresh / TTL kicks in.
        if (found)
            await _store.EvictAsync(cacheKey).ConfigureAwait(false);

        if (connectionId is not null)
            QueryObservability.RecordCacheMiss(connectionId, tableName, cacheKey);

        var result = await factory().ConfigureAwait(false);
        if (result is not null && !IsFailureResult(result))
            await _store.SetAsync(cacheKey, result, ttl, tableName).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Duck-typed check for a <c>Result&lt;T&gt;</c>-shaped object whose
    /// <c>IsFailure</c> property is <c>true</c>. Uses cached PropertyInfo per
    /// type so the reflection cost is amortised. Returns <c>false</c> for
    /// anything that doesn't expose the property (lists, scalars, etc.) —
    /// those are cached as before.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _isFailureProps = new();
    private static bool IsFailureResult(object? value)
    {
        if (value is null) return false;
        var prop = _isFailureProps.GetOrAdd(value.GetType(), t =>
            t.GetProperty("IsFailure", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        if (prop is null || prop.PropertyType != typeof(bool)) return false;
        try { return (bool)(prop.GetValue(value) ?? false); }
        catch { return false; }
    }

    /// <summary>
    /// Direct write into the cache, bypassing the cache-aside flow. Used by
    /// <see cref="SmartCachePool"/> to overwrite an entry after a background
    /// refresh produces a fresh value. No observability event is emitted —
    /// the refresh isn't a cache hit or miss from a caller's perspective.
    /// </summary>
    public static Task SetDirectAsync(string cacheKey, object value, TimeSpan ttl, string tableName, CancellationToken ct = default)
    {
        if (!Enabled) return Task.CompletedTask;
        // Same guard as GetOrSetAsync — a pool refresh that produced a
        // failure Result must not be written back into the cache.
        if (IsFailureResult(value)) return Task.CompletedTask;
        return _store.SetAsync(cacheKey, value, ttl, tableName, ct);
    }

    /// <summary>
    /// Invalidate all cached entries for the given table.
    /// <para>
    /// Two-step: bump the per-table version (so future reads compute a
    /// different cache key and miss any in-flight refresh writes), then
    /// evict the now-orphaned entries from the underlying store so they
    /// don't accumulate. Without the second step, every mutation leaves
    /// behind the previous version's entries until TTL/LRU clears them,
    /// which on a busy app produces unbounded memory growth.
    /// </para>
    /// <para>
    /// When a table has active <see cref="SmartCachePool"/> entries, the
    /// pool's background refresh is the source of truth for freshness — it
    /// re-executes queries every <c>RefreshEvery</c> and writes to the
    /// current version's cache key. Flushing the cache on every mutation
    /// (e.g. a stats ingest INSERT) would defeat the pool: every request
    /// between the flush and the next refresh tick hits the DB cold. For
    /// pool-managed tables we skip the eviction and let the pool refresh
    /// deliver naturally-stale-within-interval reads instead.
    /// </para>
    /// </summary>
    public static void Invalidate(string tableName)
    {
        // SmartCache-managed tables: the pool refresh is the freshness
        // mechanism. Skip the version bump + eviction so entries stay warm
        // between refresh ticks. The data is at most RefreshEvery stale,
        // which is an acceptable trade-off for stats/dashboard tables that
        // receive high-frequency inserts.
        if (SmartCachePoolRegistry.HasEntriesForTable(tableName))
            return;

        BumpAndEvict(tableName);

        // Fan the mutation out to peer nodes so they invalidate too. No-op on
        // the single-node default coordinator. Fire-and-forget — a transport
        // hiccup must never fail the mutation that triggered it.
        _ = _coordinator.PublishInvalidationAsync(tableName);
    }

    /// <summary>
    /// Applies an invalidation broadcast by a peer node. Same effect as
    /// <see cref="Invalidate(string)"/> but does NOT re-broadcast (that would loop).
    /// </summary>
    private static void InvalidateFromPeer(string tableName)
    {
        if (SmartCachePoolRegistry.HasEntriesForTable(tableName))
            return;
        BumpAndEvict(tableName);
    }

    private static void BumpAndEvict(string tableName)
    {
        _tableVersions.AddOrUpdate(tableName, 1L, (_, v) => v + 1);
        _ = _store.EvictByTableAsync(tableName);
    }

    /// <summary>Invalidate for the table behind entity type <typeparamref name="T"/>.</summary>
    public static void Invalidate<T>() where T : class => Invalidate(ResolveTableName<T>());

    /// <summary>
    /// Diagnostic snapshot: total entries + per-table counts + table-version
    /// map. Returned types are immutable copies; safe to log or render on an
    /// admin page without holding cache locks.
    /// </summary>
    public static QueryCacheStats GetStats()
    {
        var byTable = _store.CountByTable();
        var versions = new Dictionary<string, long>(_tableVersions, StringComparer.OrdinalIgnoreCase);
        return new QueryCacheStats(_store.Count, byTable, versions);
    }

    /// <summary>Clear the entire cache (admin / tests).</summary>
    public static void Clear()
    {
        _store.Clear();
        _tableVersions.Clear();
    }

    /// <summary>Internal hook for SmartCachePool to evict a single key.</summary>
    internal static Task EvictAsync(string cacheKey) => _store.EvictAsync(cacheKey);

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

        // byte[] (StorageType.Binary parameters — Guid→byte[16], etc.)
        // byte[].ToString() returns "System.Byte[]" for every array, which
        // collapses all binary parameter values into one cache key and causes
        // cross-query cache poisoning.
        if (value is byte[] bytes) return Convert.ToHexString(bytes);

        // DateTime near now → quantize to the configured window so that
        // `Where(x => x.At >= DateTime.UtcNow.AddDays(-30))` produces a stable key
        // across back-to-back calls. Far-future / far-past absolute dates pass through.
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
