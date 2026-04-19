# CL.MySQL2 — Performance & Caching

This page is the speed manual. Everything below is measurable — the benchmark
numbers come from a real production workload (a gaming community site serving
a 30-day heatmap off a 10k-row snapshots table).

> **Receipt:** homepage widget rendering **3.65 seconds → 187 ms** (20×) cold,
> < 10 ms warm, **zero raw SQL in consumer code**. Same database, same data,
> same widget — just the right library choices.

## Where the time goes (before and after)

| Scenario | Baseline | v4 cold | v4 warm | Speedup |
|---|---:|---:|---:|---:|
| Homepage widget (3 calls serial) | 3 653 ms | **187 ms** | **1 ms** | 20× / 3 600× |
| 30-day heatmap aggregate | 1 767 ms | **119 ms** | **1 ms** | 15× / 1 700× |
| Site-wide 24h timeseries | 1 803 ms | **121 ms** | **1 ms** | 15× / 1 800× |
| Global dashboard stats | 1 865 ms | **95 ms** | **1 ms** | 20× / 1 800× |

Three distinct wins compound:

1. **Compiled row materializers** (1.8s → ~100ms, no cache yet)
2. **Cache actually hits** (100ms → 1ms on repeat calls)
3. **SQL-side aggregation** (returns hundreds of rows instead of tens of
   thousands — kills transfer + client-side GroupBy)

The rest of this doc explains how each lever works and how to use them.

---

## Compiled materializers

Every query reads columns out of a `MySqlDataReader` into `T`. The naïve
version walks `PropertyInfo[]` per row — ~800ns per column × 10 columns × 10k
rows = ~80ms *just to copy data*. CL.MySQL2 does this instead:

1. First row of each distinct reader shape: compile a closure that reads each
   column by ordinal and calls cached getter/setter delegates.
2. Cache the closure keyed by reader shape (set of column names in order).
3. Every subsequent row: near-zero per-column overhead.

**You don't configure this.** It's on for every query. But it's the reason
queries that look cheap actually are.

> Under the hood: [`src/Core/Materializer.cs`](../../CL.MySQL2/src/Core/Materializer.cs)
> and [`src/Core/EntityMetadata.cs`](../../CL.MySQL2/src/Core/EntityMetadata.cs).
> Compiled once per `T` per reader shape, lives for the app lifetime.

---

## The cache

Opt-in per query with `.WithCache(TimeSpan)`. Cached results skip the DB
round-trip *and* the materializer pass.

```csharp
var hot = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid")
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();
```

Inside a transaction scope the cache is bypassed (reads see uncommitted
writes; caching would hide them).

### How the cache key works

The key hashes `(connectionId, tableName, tableVersion, sql, sorted-params)`
with SHA256.

Two things that the naïve version got wrong:

**1. DateTime parameters near "now" are quantized.**

Without this, every call to `.Where(x => x.At >= DateTime.UtcNow.AddDays(-30))`
produces a *different* key (UtcNow changes every call), so the cache never
hits. With `TimeQuantizeSeconds: 60` (the default), all DateTimes within a
60-second window round to the same value before hashing:

```text
Call at  09:15:03 → since = 2026-03-20 09:15:03 → rounded to 09:15:00
Call at  09:15:27 → since = 2026-03-20 09:15:27 → rounded to 09:15:00  ✓ same key
Call at  09:16:42 → since = 2026-03-20 09:16:42 → rounded to 09:16:00  ✗ new key
```

You can tune the window in `config.mysql.cache.json`:

```json
{ "TimeQuantizeSeconds": 60 }     // 1-minute window (default)
{ "TimeQuantizeSeconds": 300 }    // 5-minute window — stickier cache
{ "TimeQuantizeSeconds": 0 }      // off — raw timestamps, cache can't hit for "since = now"
```

The window only applies to DateTimes **within a year** of `UtcNow`. Absolute
dates (birthdays, historical ranges) aren't touched.

**2. Invalidation happens via a table-version counter.**

Old caches used "evict every key associated with this table" on any write —
O(N) bookkeeping. v4 just bumps a `long` counter per table, which participates
in the cache key. Old entries still exist but are unreachable; they age out
with the normal LRU eviction.

What this means for you: **no cache configuration per query beyond the TTL.**
Writing anything to a table invalidates all reads against it. No stale reads.

### What happens on each call

```
Query.WithCache(1min).ToListAsync()
        │
        ▼
Build cache key: sha256("Default|orders|version=42|SELECT ... | @p0=paid | @p1=[quantized UtcNow]")
        │
        ▼
TryGet(key) → hit?
   yes → return cached list; fire CacheHitEvent
   no  → execute SQL, materialize, store in cache, fire CacheMissEvent
```

### Global knobs

`config.mysql.cache.json`:

| Field | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch. False skips all `WithCache` logic. |
| `MaxEntries` | 10000 | Soft cap; lazy eviction (oldest 25% dropped when exceeded). |
| `MaxMemoryMb` | 256 | Advisory (entry-count eviction is current behavior). |
| `DefaultTtlSeconds` | 60 | TTL used when `.WithCache()` has no argument. |
| `TimeQuantizeSeconds` | 60 | Window for rounding DateTime parameters. |
| `PublishEvents` | true | Fire `CacheHitEvent` / `CacheMissEvent`. |

Per-DB override in `config.mysql.json`:

```json
{ "CacheEnabledOverride": false }  // turn off cache for one connection
```

### Swapping in Redis / distributed cache

`ICacheStore` is the backing interface. The default implementation is
`InProcessCacheStore`. Wire a different store during startup:

```csharp
QueryCache.UseStore(new RedisCacheStore(redis));
```

A Redis adapter isn't in the core package — ship it in a separate library so
the core stays dependency-free.

### Bypassing the cache

Four ways:

```csharp
// 1. Omit .WithCache — the cache never sees it.
.Where(...).ToListAsync();

// 2. Inside a transaction scope: WithCache is silently suppressed.
await using var tx = await mysql.BeginTransactionAsync();
mysql.Query<X>(tx).Where(...).WithCache(TimeSpan.FromMinutes(5)).ToListAsync();
// → ran against DB

// 3. Turn off globally: config.mysql.cache.json { "Enabled": false }

// 4. Invalidate manually
QueryCache.Invalidate<OrderRecord>();  // all cached reads for `orders`
QueryCache.Clear();                    // nuclear
```

---

## Benchmarking recipe

CL.MySQL2 has no magic numbers — every claim in this doc is reproducible. Use
this template to measure your own queries:

```csharp
using System.Diagnostics;
using CL.MySQL2;

await Libraries.LoadAsync<MySQL2Library>();
var mysql = Libraries.Get<MySQL2Library>()!;

async Task<long> MeasureAsync(string label, int n, Func<Task> body)
{
    // Warmup: prime the compiled materializer + first-query JIT.
    await body();

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < n; i++) await body();
    sw.Stop();

    var avg = sw.ElapsedMilliseconds / n;
    Console.WriteLine($"{label,-40} avg {avg,5} ms over {n} runs");
    return avg;
}

await MeasureAsync("Heatmap — cold (no cache)", 5, async () =>
{
    var since = DateTime.UtcNow.AddDays(-30);
    await mysql.Query<SnapshotRecord>()
        .Where(s => s.SnapshotUtc >= since)
        .GroupBy(s => new { Dow = SqlFn.DayOfWeek(s.SnapshotUtc),
                            Hour = SqlFn.Hour(s.SnapshotUtc) })
        .Select(g => new HeatmapCell(g.Key.Dow, g.Key.Hour,
                                     g.Average(x => (double)x.PlayerCount)))
        .ToListAsync();
});

await MeasureAsync("Heatmap — warm (cached)", 5, async () =>
{
    var since = DateTime.UtcNow.AddDays(-30);
    await mysql.Query<SnapshotRecord>()
        .Where(s => s.SnapshotUtc >= since)
        .WithCache(TimeSpan.FromMinutes(30))
        .GroupBy(s => new { Dow = SqlFn.DayOfWeek(s.SnapshotUtc),
                            Hour = SqlFn.Hour(s.SnapshotUtc) })
        .Select(g => new HeatmapCell(g.Key.Dow, g.Key.Hour,
                                     g.Average(x => (double)x.PlayerCount)))
        .ToListAsync();
});
```

**Interpreting the numbers:**

| If you see | It probably means |
|---|---|
| Cold > 500ms on < 100k rows | Check `EXPLAIN` — likely missing/unused index |
| Cold ≈ warm for cached queries | Cache isn't hitting — DateTime params near now? Different every call? |
| Cold improves but warm doesn't | `TimeQuantizeSeconds` window too narrow for your call cadence |
| Everything ~network-latency | You're done. Celebrate. |

---

## Hunting slow queries

### Slow query log

Any query over `SlowQueryThresholdMs` (default 1000ms) logs a warning and
fires `SlowQueryEvent`:

```
[MySQL2] [Default] Slow query (1 423 ms): SELECT * FROM `orders` WHERE ...
```

Subscribe via CodeLogic's event bus to ship these to your observability stack:

```csharp
events.Subscribe<SlowQueryEvent>(e =>
{
    metrics.Increment("mysql.slow_query", new { db = e.ConnectionId });
    logger.LogWarning("Slow query on {Db}: {Query} ({Ms}ms)\nEXPLAIN:\n{Plan}",
        e.ConnectionId, e.Query, e.ElapsedMs, e.ExplainJson);
    return Task.CompletedTask;
});
```

The threshold is per-DB (`SlowQueryThresholdMs` in `config.mysql.json`). Drop
it to 200ms in dev, keep 1000ms in prod.

### Automatic EXPLAIN capture

Set `CaptureExplainOnSlowQuery: true` (the default) and every `SlowQueryEvent`
arrives with `ExplainJson` — MySQL's `EXPLAIN FORMAT=JSON` plan for the query
that just ran slow. Machine-readable, dashboard-friendly. No more "can you
re-run that with EXPLAIN for me".

### N+1 detector

Turn on by setting `N1DetectorThreshold` > 0 in `config.mysql.json`:

```json
{ "N1DetectorThreshold": 10 }
```

If the same query *template* (SQL text, different parameter values) fires
10 times within an AsyncLocal request scope, you get a `N1QueryDetectedEvent`
and a log warning. Classic smell of a loop-over-collection that should have
been one query.

---

## Query event stream

Every query publishes `QueryExecutedEvent` regardless of speed:

```csharp
public record QueryExecutedEvent(
    string ConnectionId,
    string Query,
    long ElapsedMs,
    int RowCount,
    bool CacheHit,
    DateTime CompletedAt);
```

Use it to build a Grafana / Honeycomb / Datadog pipeline:

```csharp
events.Subscribe<QueryExecutedEvent>(e =>
{
    metrics.Histogram("mysql.query_ms", e.ElapsedMs,
        tags: new { db = e.ConnectionId, cache_hit = e.CacheHit });
    return Task.CompletedTask;
});
```

Plus the pair:

```csharp
events.Subscribe<CacheHitEvent>(e => metrics.Increment("cache.hit"));
events.Subscribe<CacheMissEvent>(e => metrics.Increment("cache.miss"));
```

---

## Index strategy — covering indexes

The `[Index]` attribute with `Include` is how you avoid the "index seek then
primary-key lookup per row" anti-pattern. Here's the story with numbers from
the FragHunt workload:

**Without a covering index:**

```csharp
[Column(Name = "snapshot_utc", DataType = DataType.DateTime, Index = true)]
public DateTime SnapshotUtc { get; set; }
```

```text
SELECT * FROM snapshots WHERE snapshot_utc >= ?
→ index seek on snapshot_utc to find 10k matching row ids
→ 10k primary-key lookups to fetch the actual rows
→ 10k VARCHAR(256) hostname columns transferred
```

**With a covering index:**

```csharp
[Column(Name = "snapshot_utc", DataType = DataType.DateTime, NotNull = true)]
[Index(Name = "ix_snapshot_utc_covering",
       Include = new[] { nameof(ServerId), nameof(IsOnline), nameof(PlayerCount) })]
public DateTime SnapshotUtc { get; set; }
```

```text
SELECT server_id, is_online, player_count
FROM snapshots
WHERE snapshot_utc >= ?
→ index-only scan — all three output columns are at the leaf
→ no PK lookups, no row-body reads
```

This is the single biggest MySQL-level tuning knob once your queries are
SQL-aggregated. Pair it with `.Select(s => new { s.ServerId, s.IsOnline, s.PlayerCount })`
— projection pushdown + covering index = fully index-resident query.

See [Schema docs](mysql2-schema.md#indexes-index-and-compositeindex) for full
attribute syntax.

---

## Checklist — "my query feels slow"

In order of cheapness to diagnose:

1. **Is it cached?** `WithCache(TimeSpan.FromMinutes(1))` + second run. If the
   second run is instant, caching is fine — the cold cost is your actual
   concern.
2. **Is it `SELECT *` on a wide table?** Project what you need:
   `.Select(x => new { x.A, x.B })`.
3. **Are you materializing to LINQ the work?**
   If you see `ToListAsync` followed by `.GroupBy` / `.Sum`, move the group
   and aggregate server-side: `.GroupBy(...).Select(g => …).ToListAsync()`.
4. **Is the index being used?** Enable slow-query log + EXPLAIN capture. If
   the plan is a full table scan, add an index or check that the predicate
   references the indexed column unmodified (no `WHERE YEAR(col) = …`).
5. **Is the index covering?** If you see "index seek, then PK lookups", add an
   `[Index(Include = ...)]` for the columns you actually read.
6. **Is MySQL itself tuned?** InnoDB buffer pool large enough to hold the hot
   working set? Disk IO reasonable? These are out of scope here — check the
   server log.

---

## What's next

- **Query shapes** → [Query Builder](mysql2-queries.md)
- **Index & retention attributes** → [Schema & Migrations](mysql2-schema.md)
- **Basics** → [Overview](mysql2.md)
