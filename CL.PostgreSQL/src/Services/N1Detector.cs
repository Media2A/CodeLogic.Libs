using System.Collections.Concurrent;
using System.Text;
using CL.PostgreSQL.Core;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Detects N+1 query patterns: the same normalized SQL template executing many times in
/// quick succession on one connection.
/// <para>
/// Driven by the per-database <c>N1DetectorThreshold</c>. <b>0 disables it</b>, which is
/// the default — and a disabled detector costs exactly one volatile bool read per query,
/// with no allocation and no dictionary touch.
/// </para>
/// <para>
/// Counting is per <c>(connection id, template)</c> over a rolling one-second window.
/// Crossing the threshold publishes <see cref="Events.N1QueryDetectedEvent"/> once for
/// that window; further executions inside the same window stay silent, and the next
/// window starts fresh. Bookkeeping is capped at <see cref="Capacity"/> templates and
/// pruned by age, so a long-running process cannot grow it without bound.
/// </para>
/// </summary>
internal static class N1Detector
{
    /// <summary>Rolling counting window.</summary>
    private static readonly long WindowTicks = TimeSpan.FromSeconds(1).Ticks;

    /// <summary>Maximum number of tracked templates before a prune pass runs.</summary>
    private const int Capacity = 512;

    private sealed class Slot
    {
        public long WindowStartTicks;
        public int Count;
        public bool Fired;
    }

    private static readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private static long _lastPruneTicks;

    /// <summary>
    /// Records one execution. Returns immediately when the detector is disabled for every
    /// configured connection.
    /// </summary>
    public static void Record(string connectionId, string sql)
    {
        // Fast path: a single volatile read when nothing enabled the detector.
        if (!PostgreSqlRuntimeOptions.AnyN1Enabled) return;

        var threshold = PostgreSqlRuntimeOptions.For(connectionId).N1DetectorThreshold;
        if (threshold <= 0) return;
        if (string.IsNullOrEmpty(sql)) return;

        var template = Normalize(sql);
        var key = string.Concat(connectionId, "", template);
        var now = DateTime.UtcNow.Ticks;

        PruneIfNeeded(now);

        var slot = _slots.GetOrAdd(key, _ => new Slot { WindowStartTicks = now });

        var fireCount = 0;
        lock (slot)
        {
            if (now - slot.WindowStartTicks > WindowTicks)
            {
                slot.WindowStartTicks = now;
                slot.Count = 0;
                slot.Fired = false;
            }

            slot.Count++;
            if (!slot.Fired && slot.Count >= threshold)
            {
                slot.Fired = true;
                fireCount = slot.Count;
            }
        }

        // Publish outside the lock — a subscriber must never serialize the query path.
        if (fireCount > 0)
            QueryObservability.RecordN1(connectionId, template, fireCount);
    }

    /// <summary>Drops every tracked template. Used when the library stops, and by tests.</summary>
    public static void Reset()
    {
        _slots.Clear();
        Interlocked.Exchange(ref _lastPruneTicks, 0);
    }

    /// <summary>
    /// Keeps the map bounded. Runs at most once per window, and only once the map is over
    /// capacity: expired slots go first, and if that is not enough the map is cleared
    /// outright rather than allowed to grow.
    /// </summary>
    private static void PruneIfNeeded(long now)
    {
        if (_slots.Count <= Capacity) return;

        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now - last < WindowTicks) return;
        if (Interlocked.CompareExchange(ref _lastPruneTicks, now, last) != last) return;

        foreach (var kv in _slots)
        {
            if (now - Volatile.Read(ref kv.Value.WindowStartTicks) > WindowTicks * 2)
                _slots.TryRemove(kv.Key, out _);
        }

        if (_slots.Count > Capacity) _slots.Clear();
    }

    /// <summary>
    /// Folds a statement into a template: digit runs collapse to <c>?</c> (so inline
    /// LIMIT/OFFSET values and the per-builder parameter counter in <c>@qb_0</c> do not
    /// split one logical query into many templates) and whitespace runs collapse to a
    /// single space.
    /// </summary>
    internal static string Normalize(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var lastDigit = false;
        var lastSpace = false;

        foreach (var ch in sql)
        {
            if (char.IsAsciiDigit(ch))
            {
                if (!lastDigit) sb.Append('?');
                lastDigit = true;
                lastSpace = false;
                continue;
            }

            lastDigit = false;

            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }

            lastSpace = false;
            sb.Append(ch);
        }

        return sb.ToString().Trim();
    }
}
