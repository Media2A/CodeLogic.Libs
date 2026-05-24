using System.Collections.Concurrent;
using CodeLogic.Core.Logging;

namespace CL.MySQL2.Services;

/// <summary>
/// Process-wide registry of named <see cref="SmartCachePool"/> instances.
/// Pools are declared once at app startup (typically in a plugin's
/// <c>OnInitializeAsync</c>) and referenced from queries by name.
/// </summary>
public static class SmartCachePoolRegistry
{
    private static readonly ConcurrentDictionary<string, SmartCachePool> _pools =
        new(StringComparer.OrdinalIgnoreCase);
    private static ILogger? _logger;

    internal static void Configure(ILogger? logger) => _logger = logger;

    /// <summary>
    /// Registers and starts a new pool. If a pool with the same name already
    /// exists it is returned unchanged (idempotent on plugin reloads).
    /// <para>
    /// When <paramref name="warmUp"/> is supplied the pool runs it once as
    /// a fire-and-forget task so its hot queries are populated before the
    /// first user request. Inside <paramref name="warmUp"/>, just call the
    /// queries that should be warm — they auto-register with the pool via
    /// their normal <c>.SmartCache(name)</c> decoration. The warm-up is
    /// skipped on a repeat registration of an existing pool.
    /// </para>
    /// </summary>
    public static SmartCachePool Register(
        string name,
        TimeSpan refreshEvery,
        int maxIdleFires = 3,
        Func<Task>? warmUp = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Pool name must be non-empty.", nameof(name));
        if (refreshEvery <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshEvery), "Refresh interval must be positive.");

        var created = false;
        var pool = _pools.GetOrAdd(name, key =>
        {
            var p = new SmartCachePool(key, refreshEvery, maxIdleFires, _logger);
            p.Start();
            created = true;
            return p;
        });
        if (created && warmUp is not null) pool.WarmUp(warmUp);
        return pool;
    }

    /// <summary>Returns the pool with the given name, or <c>null</c> if not registered.</summary>
    public static SmartCachePool? Get(string name) =>
        _pools.TryGetValue(name, out var pool) ? pool : null;

    /// <summary>
    /// Returns <c>true</c> if any registered pool currently has refresh entries
    /// for the given table name. Used by <see cref="QueryCache.Invalidate"/> to
    /// skip mutation-triggered eviction for tables that are kept warm by a pool's
    /// background refresh loop.
    /// </summary>
    public static bool HasEntriesForTable(string tableName) =>
        _pools.Values.Any(p => p.HasEntriesForTable(tableName));

    /// <summary>Triggers an out-of-schedule refresh for the named pool.</summary>
    public static Task RefreshNowAsync(string name, CancellationToken ct = default) =>
        _pools.TryGetValue(name, out var pool) ? pool.RefreshNowAsync(ct) : Task.CompletedTask;

    /// <summary>Diagnostic snapshot of every registered pool.</summary>
    public static IReadOnlyList<SmartCachePoolStats> GetStats() =>
        _pools.Values.Select(p => p.GetStats()).ToList();

    /// <summary>Stops and disposes every pool. Called by the library on shutdown.</summary>
    public static async Task DisposeAllAsync()
    {
        foreach (var pool in _pools.Values)
        {
            try { await pool.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _pools.Clear();
    }
}
