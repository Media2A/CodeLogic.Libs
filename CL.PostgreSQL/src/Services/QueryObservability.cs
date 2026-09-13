using CL.PostgreSQL.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;

namespace CL.PostgreSQL.Services;

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

    /// <summary>Bind this sink to CodeLogic's event bus. Called by <c>PostgreSQLLibrary</c>.</summary>
    public static void Configure(IEventBus? events, ILogger? logger)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Fire <see cref="QueryExecutedEvent"/>; always, regardless of speed. Also feeds the
    /// N+1 detector, which is a no-op unless a connection configured a non-zero
    /// <c>N1DetectorThreshold</c>.
    /// </summary>
    public static void RecordExecuted(
        string connectionId, string sql, long elapsedMs, int rowCount, bool cacheHit)
    {
        // Cache hits never touched the server, so they are not part of an N+1 pattern.
        if (!cacheHit) N1Detector.Record(connectionId, sql);
        if (_events is null) return;
        _ = _events.PublishAsync(new QueryExecutedEvent(
            connectionId, sql, elapsedMs, rowCount, cacheHit, DateTime.UtcNow));
    }

    /// <summary>
    /// Fire <see cref="SlowQueryEvent"/> and log. <paramref name="explainJson"/> is only
    /// included when the per-DB <c>CaptureExplainOnSlowQuery</c> flag is on and the plan
    /// was fetched successfully. Query pipelines call
    /// <see cref="SlowQueryExplain.Record"/> rather than this method directly, so the flag
    /// and the best-effort rules are applied in one place.
    /// </summary>
    public static void RecordSlow(
        string connectionId, string sql, long elapsedMs, string? explainJson = null)
    {
        _logger?.Warning($"[PostgreSQL] [{connectionId}] Slow query ({elapsedMs}ms): {sql}");
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

    /// <summary>
    /// Fire <see cref="N1QueryDetectedEvent"/> and log. Called by <see cref="N1Detector"/>
    /// once per rolling window per offending template.
    /// </summary>
    public static void RecordN1(string connectionId, string template, int count)
    {
        _logger?.Warning($"[PostgreSQL] [{connectionId}] N+1 detected ({count}×): {template}");
        if (_events is null) return;
        _ = _events.PublishAsync(new N1QueryDetectedEvent(
            connectionId, template, count, DateTime.UtcNow));
    }
}
