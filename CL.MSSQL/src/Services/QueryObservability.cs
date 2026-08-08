using CL.MSSQL.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CL.MSSQL.Configuration;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

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
    private static ConnectionManager? _connections;
    private static Func<string, SqlServerDatabaseConfig?>? _configLookup;

    /// <summary>Bind this sink to CodeLogic's event bus. Called by <c>MSSQLLibrary</c>.</summary>
    public static void Configure(IEventBus? events, ILogger? logger)
    {
        _events = events;
        _logger = logger;
    }

    internal static void Configure(IEventBus? events, ILogger? logger, ConnectionManager connections,
        Func<string, SqlServerDatabaseConfig?> configLookup)
    {
        Configure(events, logger);
        _connections = connections;
        _configLookup = configLookup;
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
        _logger?.Warning($"[MSSQL] [{connectionId}] Slow query ({elapsedMs}ms): {sql}");
        _ = PublishSlowAsync(connectionId, sql, elapsedMs, explainJson);
    }

    private static async Task PublishSlowAsync(string connectionId, string sql, long elapsedMs, string? plan)
    {
        if (plan is null && _connections is not null && _configLookup?.Invoke(connectionId)?.CaptureExplainOnSlowQuery == true)
        {
            try
            {
                plan = await CaptureEstimatedPlanAsync(connectionId, sql).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[MSSQL] Estimated plan capture skipped: {ex.Message}");
            }
        }
        if (_events is not null)
            await _events.PublishAsync(new SlowQueryEvent(connectionId, sql, elapsedMs, DateTime.UtcNow, plan)).ConfigureAwait(false);
    }

    internal static async Task<string?> CaptureEstimatedPlanAsync(string connectionId, string sql,
        CancellationToken ct = default,
        ConnectionManager? connections = null)
    {
        var manager = connections ?? _connections;
        if (manager is null) return null;
        return await manager.ExecuteWithConnectionAsync(async connection =>
        {
            // SqlClient sends parameterized text through sp_executesql, while SQL Server
            // forbids SET SHOWPLAN_XML inside a stored procedure. The original slow query
            // has just compiled/executed, so retrieve the same estimated ShowPlan XML from
            // the plan cache without executing user SQL a second time.
            await using var cached = connection.CreateCommand();
            cached.CommandText = """
                SELECT TOP (1) CONVERT(nvarchar(max), qp.query_plan)
                FROM sys.dm_exec_cached_plans cp
                CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st
                CROSS APPLY sys.dm_exec_query_plan(cp.plan_handle) qp
                WHERE (st.dbid = DB_ID() OR st.dbid IS NULL)
                  AND (st.text = @sql OR CHARINDEX(@sql, st.text) > 0)
                  AND qp.query_plan IS NOT NULL
                ORDER BY cp.usecounts DESC
                """;
            cached.Parameters.Add(new SqlParameter("@sql", System.Data.SqlDbType.NVarChar, -1) { Value = sql });
            return (await cached.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
        }, connectionId, ct).ConfigureAwait(false);
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
        _logger?.Warning($"[MSSQL] [{connectionId}] N+1 detected ({count}×): {template}");
        if (_events is null) return;
        _ = _events.PublishAsync(new N1QueryDetectedEvent(
            connectionId, template, count, DateTime.UtcNow));
    }
}
