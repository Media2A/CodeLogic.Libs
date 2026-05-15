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
    /// </summary>
    public static SmartCachePool Register(
        string name,
        TimeSpan refreshEvery,
        int maxIdleFires = 3)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Pool name must be non-empty.", nameof(name));
        if (refreshEvery <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(refreshEvery), "Refresh interval must be positive.");

        return _pools.GetOrAdd(name, key =>
        {
            var pool = new SmartCachePool(key, refreshEvery, maxIdleFires, _logger);
            pool.Start();
            return pool;
        });
    }

    /// <summary>Returns the pool with the given name, or <c>null</c> if not registered.</summary>
    public static SmartCachePool? Get(string name) =>
        _pools.TryGetValue(name, out var pool) ? pool : null;

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
