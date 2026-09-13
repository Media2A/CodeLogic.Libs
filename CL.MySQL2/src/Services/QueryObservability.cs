using System.Collections.Concurrent;
using System.Text;
using CL.MySQL2.Events;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using MySqlConnector;

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
        // N+1 accounting first: it is a no-op (no allocation, no dictionary touch) unless a
        // connection has a non-zero threshold configured, which is not the default.
        if (_n1Enabled) TrackN1(connectionId, sql);

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
        _logger?.Warning($"[MySQL2] [{connectionId}] N+1 detected ({count}x): {template}");
        if (_events is null) return;
        _ = _events.PublishAsync(new N1QueryDetectedEvent(
            connectionId, template, count, DateTime.UtcNow));
    }

    // ── Slow-query EXPLAIN capture ────────────────────────────────────────────

    /// <summary>
    /// Slow-query hook that honours the connection's <c>CaptureExplainOnSlowQuery</c> flag.
    /// With the flag off (the default), or for a statement that cannot be explained, this is
    /// exactly <see cref="RecordSlow(string, string, long, string?)"/>. With it on, the plan is
    /// fetched on a <i>separate pooled connection</i> — never the caller's transaction, never on
    /// the caller's thread — and the event publishes once the attempt finishes. Any failure is
    /// swallowed and the event still publishes with a null payload.
    /// </summary>
    internal static void RecordSlow(
        ConnectionManager? connectionManager,
        string connectionId,
        string sql,
        long elapsedMs,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        var capture = connectionManager is not null
                      && connectionManager.GetConfiguration(connectionId)?.CaptureExplainOnSlowQuery == true
                      && CanExplain(sql, parameters);

        if (!capture)
        {
            RecordSlow(connectionId, sql, elapsedMs);
            return;
        }

        _ = Task.Run(async () =>
        {
            string? explain = null;
            try
            {
                explain = await CaptureExplainAsync(connectionManager!, connectionId, sql, parameters)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best effort only: a diagnostic must never turn into a second failure.
            }
            RecordSlow(connectionId, sql, elapsedMs, explain);
        });
    }

    /// <summary>
    /// Whether <c>EXPLAIN FORMAT=JSON</c> can legally prefix this statement with the parameters
    /// we hold. MySQL explains SELECT/INSERT/UPDATE/DELETE/REPLACE/TABLE/WITH only — never DDL —
    /// and cannot explain a multi-statement batch. A statement that references a parameter we
    /// were not given would fail to bind, so it is skipped too.
    /// </summary>
    private static bool CanExplain(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;

        var trimmed = sql.TrimStart();
        var space = trimmed.IndexOfAny([' ', '\t', '\r', '\n', '(']);
        var verb = (space > 0 ? trimmed[..space] : trimmed).ToUpperInvariant();
        if (verb is not ("SELECT" or "INSERT" or "UPDATE" or "DELETE" or "REPLACE" or "TABLE" or "WITH"))
            return false;

        // Multi-statement batch (e.g. "INSERT ...; SELECT LAST_INSERT_ID();") — not explainable.
        var body = sql.TrimEnd().TrimEnd(';');
        if (body.Contains(';')) return false;

        // Every @parameter in the statement must be one we can bind.
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '@') continue;
            var start = i + 1;
            var end = start;
            while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] == '_')) end++;
            if (end == start) continue; // "@@version" style server variable
            var name = body[start..end];
            if (parameters is null) return false;
            if (!parameters.ContainsKey("@" + name) && !parameters.ContainsKey(name)) return false;
            i = end - 1;
        }

        return true;
    }

    private static async Task<string?> CaptureExplainAsync(
        ConnectionManager connectionManager,
        string connectionId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters)
    {
        return await connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN FORMAT=JSON " + sql.TrimEnd().TrimEnd(';');
            // A diagnostic must not hang behind the query it is diagnosing.
            cmd.CommandTimeout = 5;
            if (parameters is not null)
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);

            var raw = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return raw as string;
        }, connectionId, CancellationToken.None).ConfigureAwait(false);
    }

    // ── N+1 detection ─────────────────────────────────────────────────────────

    // Rolling window for template repetition, and the cap on how many templates are tracked.
    private static readonly TimeSpan N1Window = TimeSpan.FromSeconds(1);
    private const int MaxTrackedTemplates = 512;

    // False unless some connection configured a non-zero threshold, so the hot path costs a
    // single static bool read when the feature is off (the default).
    private static volatile bool _n1Enabled;
    private static readonly ConcurrentDictionary<string, int> _n1Thresholds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, N1Slot> _n1Slots = new(StringComparer.Ordinal);

    private sealed class N1Slot
    {
        public long WindowStartMs;
        public int Count;
        public bool Fired;
    }

    /// <summary>
    /// Sets the N+1 threshold for a connection: the number of executions of one normalized SQL
    /// template, within a one-second window, that publishes <see cref="N1QueryDetectedEvent"/>.
    /// 0 disables detection for that connection (the default). Called by <c>MySQL2Library</c>.
    /// </summary>
    public static void ConfigureN1Detection(string connectionId, int threshold)
    {
        if (threshold > 0) _n1Thresholds[connectionId] = threshold;
        else _n1Thresholds.TryRemove(connectionId, out _);

        _n1Enabled = !_n1Thresholds.IsEmpty;
        if (!_n1Enabled) _n1Slots.Clear();
    }

    /// <summary>Clears every N+1 threshold and all accumulated counters. Test / shutdown hook.</summary>
    internal static void ResetN1Detection()
    {
        _n1Thresholds.Clear();
        _n1Slots.Clear();
        _n1Enabled = false;
    }

    private static void TrackN1(string connectionId, string sql)
    {
        if (!_n1Thresholds.TryGetValue(connectionId, out var threshold) || threshold <= 0) return;

        var template = NormalizeTemplate(sql);
        var key = connectionId + "" + template;
        var now = Environment.TickCount64;

        if (_n1Slots.Count >= MaxTrackedTemplates && !_n1Slots.ContainsKey(key))
            PruneSlots(now);

        var slot = _n1Slots.GetOrAdd(key, _ => new N1Slot { WindowStartMs = now });

        int fireAt;
        lock (slot)
        {
            if (now - slot.WindowStartMs >= (long)N1Window.TotalMilliseconds)
            {
                slot.WindowStartMs = now;
                slot.Count = 0;
                slot.Fired = false;
            }
            slot.Count++;
            if (slot.Fired || slot.Count < threshold) return;
            slot.Fired = true;       // once per window, not on every further execution
            fireAt = slot.Count;
        }

        RecordN1(connectionId, template, fireAt);
    }

    /// <summary>
    /// Drops slots whose window has expired so the tracking table cannot grow without bound on
    /// a long-running process. If every slot is live (a genuine storm of distinct templates),
    /// the whole table is dropped rather than allowed past the cap.
    /// </summary>
    private static void PruneSlots(long now)
    {
        var windowMs = (long)N1Window.TotalMilliseconds;
        foreach (var kv in _n1Slots)
        {
            long start;
            lock (kv.Value) start = kv.Value.WindowStartMs;
            if (now - start >= windowMs) _n1Slots.TryRemove(kv.Key, out _);
        }
        if (_n1Slots.Count >= MaxTrackedTemplates) _n1Slots.Clear();
    }

    /// <summary>
    /// Collapses a statement to a comparison template: parameter placeholders and numeric
    /// literals (LIMIT / OFFSET, inline ids) become <c>?</c>, and runs of whitespace collapse,
    /// so the same logical query issued in a loop maps to one template.
    /// </summary>
    internal static string NormalizeTemplate(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var lastWasSpace = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            lastWasSpace = false;

            if (c == '@')
            {
                var end = i + 1;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_' || sql[end] == '@')) end++;
                sb.Append('?');
                i = end - 1;
                continue;
            }

            if (char.IsDigit(c) && (sb.Length == 0 || !char.IsLetterOrDigit(sb[^1]) && sb[^1] != '_'))
            {
                var end = i;
                while (end < sql.Length && (char.IsDigit(sql[end]) || sql[end] == '.')) end++;
                sb.Append('?');
                i = end - 1;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
