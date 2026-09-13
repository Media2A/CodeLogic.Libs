using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
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

    // Set only when at least one database configures N1DetectorThreshold > 0. A plain static
    // bool read keeps the detector free on the hot path for everyone who leaves it at the
    // default of 0 - no dictionary touch, no allocation, no string work.
    private static volatile bool _n1Enabled;

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

    /// <summary>
    /// Registers a connection's <c>N1DetectorThreshold</c>. A threshold of 0 or less removes
    /// the registration, which is the default and leaves the detector completely inert.
    /// </summary>
    public static void ConfigureN1Detection(string connectionId, int threshold)
    {
        if (threshold <= 0) _n1Thresholds.TryRemove(connectionId, out _);
        else _n1Thresholds[connectionId] = threshold;
        _n1Enabled = !_n1Thresholds.IsEmpty;
        if (!_n1Enabled) _n1Windows.Clear();
    }

    /// <summary>
    /// Clears every N+1 registration and the accumulated counters, so a library restart does
    /// not inherit the previous instance's thresholds.
    /// </summary>
    internal static void ResetN1Detection()
    {
        _n1Thresholds.Clear();
        _n1Enabled = false;
        _n1Windows.Clear();
    }

    /// <summary>Fire <see cref="QueryExecutedEvent"/>; always, regardless of speed.</summary>
    public static void RecordExecuted(
        string connectionId, string sql, long elapsedMs, int rowCount, bool cacheHit)
    {
        if (_n1Enabled && !cacheHit) TrackN1(connectionId, sql);
        if (_events is null) return;
        _ = _events.PublishAsync(new QueryExecutedEvent(
            connectionId, sql, elapsedMs, rowCount, cacheHit, DateTime.UtcNow));
    }

    // -- N+1 detection ---------------------------------------------------------

    /// <summary>Rolling window for the repeat counter: one second.</summary>
    private static readonly long N1WindowTicks = Stopwatch.Frequency;

    /// <summary>
    /// Hard ceiling on tracked (connection, template) pairs. The map is pruned by age first;
    /// if a pathological workload still fills it, it is dropped wholesale rather than allowed
    /// to grow - this is a heuristic detector, not an accounting ledger.
    /// </summary>
    private const int N1Capacity = 512;

    private static readonly ConcurrentDictionary<string, N1Window> _n1Windows =
        new(StringComparer.Ordinal);

    private sealed class N1Window
    {
        public long StartedAt;
        public int Count;
        public bool Published;
    }

    private static readonly ConcurrentDictionary<string, int> _n1Thresholds =
        new(StringComparer.OrdinalIgnoreCase);

    private static void TrackN1(string connectionId, string sql)
    {
        if (!_n1Thresholds.TryGetValue(connectionId, out var threshold) || threshold <= 0) return;

        var template = NormalizeTemplate(sql);
        var key = connectionId + "\u0000" + template;

        if (_n1Windows.Count >= N1Capacity && !_n1Windows.ContainsKey(key)) PruneN1();

        var window = _n1Windows.GetOrAdd(key, _ => new N1Window { StartedAt = Stopwatch.GetTimestamp(), Count = 0 });

        int count;
        lock (window)
        {
            var now = Stopwatch.GetTimestamp();
            if (now - window.StartedAt >= N1WindowTicks)
            {
                // Window rolled over: this execution starts a fresh one.
                window.StartedAt = now;
                window.Count = 1;
                window.Published = false;
                return;
            }

            window.Count++;
            if (window.Published || window.Count < threshold) return;
            window.Published = true;      // publish once per window, not on every later execution
            count = window.Count;
        }

        RecordN1(connectionId, template, count);
    }

    private static void PruneN1()
    {
        var cutoff = Stopwatch.GetTimestamp() - (N1WindowTicks * 4);
        foreach (var pair in _n1Windows)
        {
            long startedAt;
            lock (pair.Value) startedAt = pair.Value.StartedAt;
            if (startedAt < cutoff) _n1Windows.TryRemove(pair.Key, out _);
        }
        // Everything is still live: drop the lot rather than grow without bound.
        if (_n1Windows.Count >= N1Capacity) _n1Windows.Clear();
    }

    /// <summary>
    /// Reduces a statement to a comparison template: runs of whitespace collapse to a single
    /// space and the digits inside generated parameter names (<c>@p_0</c>, <c>@p12</c>) are
    /// dropped, so the same query executed in a loop maps to one template even when its
    /// parameter numbering differs between builds.
    /// </summary>
    internal static string NormalizeTemplate(string sql)
    {
        var builder = new StringBuilder(sql.Length);
        var inParameter = false;
        var lastWasSpace = true;
        foreach (var ch in sql)
        {
            if (char.IsWhiteSpace(ch))
            {
                inParameter = false;
                if (!lastWasSpace) { builder.Append(' '); lastWasSpace = true; }
                continue;
            }
            lastWasSpace = false;
            if (ch == '@') { inParameter = true; builder.Append(ch); continue; }
            if (inParameter)
            {
                if (char.IsAsciiDigit(ch)) continue;                  // @p_0 / @p12 -> @p_ / @p
                if (!char.IsLetterOrDigit(ch) && ch != '_') inParameter = false;
            }
            builder.Append(ch);
        }
        return builder.ToString().TrimEnd();
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

    /// <summary>
    /// Publishes <see cref="N1QueryDetectedEvent"/> and logs a warning. Called by the detector
    /// when one normalized statement crosses <c>N1DetectorThreshold</c> executions within a
    /// second on a single connection; public so an application with its own request-scope
    /// tracking can report a repeat it detected itself.
    /// </summary>
    public static void RecordN1(string connectionId, string template, int count)
    {
        _logger?.Warning($"[MSSQL] [{connectionId}] N+1 detected ({count}×): {template}");
        if (_events is null) return;
        _ = _events.PublishAsync(new N1QueryDetectedEvent(
            connectionId, template, count, DateTime.UtcNow));
    }
}
