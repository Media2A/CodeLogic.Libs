using CL.MySQL2.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;

namespace CL.MySQL2.Services;

/// <summary>
/// Process-wide sink for query lifecycle notifications. Query pipelines call the
/// lightweight <c>Record*</c> methods without needing an <see cref="IEventBus"/>
/// instance passed through. The library wires the sink to CodeLogic's event bus at
/// init time.
/// </summary>
public static class QueryObservability
{
    private static IEventBus? _events;
    private static ILogger? _logger;

    /// <summary>Bind this sink to CodeLogic's event bus. Called by <c>MySQL2Library</c>.</summary>
    public static void Configure(IEventBus? events, ILogger? logger)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>Fire <see cref="QueryExecutedEvent"/>; always, regardless of speed.</summary>
    public static void RecordExecuted(
        string connectionId, string sql, long elapsedMs, int rowCount, bool cacheHit)
    {
        if (_events is null) return;
        _ = _events.PublishAsync(new QueryExecutedEvent(
            connectionId, sql, elapsedMs, rowCount, cacheHit, DateTime.UtcNow));
    }

    /// <summary>
    /// Fire <see cref="SlowQueryEvent"/> and log. <paramref name="explainJson"/> is only
    /// included when the per-DB <c>CaptureExplainOnSlowQuery</c> flag is on and the
    /// caller has fetched the plan.
    /// </summary>
    public static void RecordSlow(
        string connectionId, string sql, long elapsedMs, string? explainJson = null)
    {
        _logger?.Warning($"[MySQL2] [{connectionId}] Slow query ({elapsedMs}ms): {sql}");
        if (_events is null) return;
        _ = _events.PublishAsync(new SlowQueryEvent(
            connectionId, sql, elapsedMs, DateTime.UtcNow, explainJson));
    }

    public static void RecordCacheHit(string connectionId, string tableName, string cacheKey)
    {
        if (_events is null) return;
        _ = _events.PublishAsync(new CacheHitEvent(connectionId, tableName, cacheKey, DateTime.UtcNow));
    }

    public static void RecordCacheMiss(string connectionId, string tableName, string cacheKey)
    {
        if (_events is null) return;
        _ = _events.PublishAsync(new CacheMissEvent(connectionId, tableName, cacheKey, DateTime.UtcNow));
    }

    public static void RecordN1(string connectionId, string template, int count)
    {
        _logger?.Warning($"[MySQL2] [{connectionId}] N+1 detected ({count}×): {template}");
        if (_events is null) return;
        _ = _events.PublishAsync(new N1QueryDetectedEvent(
            connectionId, template, count, DateTime.UtcNow));
    }
}
