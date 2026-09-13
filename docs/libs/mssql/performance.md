# CL.MSSQL — Performance & Caching

> A self-invalidating result cache, warm smart-cache pools, multi-node coordination, transient retries, and the diagnostics that surface slow queries.

See the [overview](index.md) for loading, repositories, configuration, and events.

CL.MSSQL is built to be fast by default: reflection runs once per entity, projections transfer only the columns you select, and read results cache with invalidation that costs nothing. This page covers the caching model and the resilience and observability features around it.

## Result cache

`.WithCache(ttl)` caches a single-table query's result for the given TTL. The cache is a cache-aside read path keyed on the translated SQL plus its parameters; a failure `Result` is never cached.

```csharp
Result<List<Server>> servers = await mssql.Query<Server>()
    .Where(s => s.Region == "eu")
    .OrderBy(s => s.Name)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToListAsync();
```

The cache is configured in `config.mssql.cache.json`. Only `Enabled`, `MaxEntries`, and `TimeQuantizeSeconds` are applied at startup (via `QueryCache.Configure`); `MaxMemoryMb`, `DefaultTtlSeconds`, `PublishEvents`, and the per-database `CacheEnabledOverride` are declared but not yet read by the library. The static `QueryCache` facade exposes the public operations `UseStore`, `UseCoordinator`, `Invalidate`, `Clear`, `Count`, and `GetStats` — the resolved `Enabled` and `TimeQuantizeSeconds` values are internal and not readable from application code.

> Caching is available on single-table `QueryBuilder<T>` reads and on `ProjectedQuery` (single-table `Select`). It is **not** available on joined queries or subquery-filtered (`WhereExists` / `WhereIn`) queries — those stamp a single table's version and could not be invalidated when the other table mutates. It is also disabled inside a transaction scope.

### Table-version invalidation

Every cacheable table carries a version counter that is mixed into the cache key. Any mutation through the library bumps that counter and sweeps the table's now-orphaned entries from the store, so all existing entries for the table instantly become un-hittable — there is no per-key tracking on the hot path. One exception: a table that currently has live `SmartCachePool` entries is skipped entirely (no bump, no eviction), because the pool's refresh tick is the freshness mechanism for those tables.

This makes invalidation free: you never call an `Invalidate(...)` yourself for ordinary CRUD; an `InsertAsync` / `UpdateAsync` / `DeleteAsync` / bulk write bumps the version as a side effect.

### Time quantization

A naive `Where(x => x.At >= DateTime.UtcNow.AddDays(-30))` would produce a unique cache key on every call because `UtcNow` changes each tick. CL.MSSQL quantizes `DateTime` parameters that fall within 365 days of now to the nearest `TimeQuantizeSeconds` bucket (default 60s), so the same rolling-window query reuses one cache entry for the duration of the bucket.

```csharp
// Cacheable: the AddDays(-30) bound is quantized to a 60s bucket
await mssql.Query<Event>()
    .Where(e => e.At >= DateTime.UtcNow.AddDays(-30))
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();
```

### Cache stampede protection

Concurrent misses on the same cold key collapse to a single factory execution (single-flight) instead of a thundering herd of identical DB queries. This is transparent — no API change.

## Smart cache pools

A `SmartCachePool` is a named group of cached queries kept warm by a background timer. Reads after the first never block on the DB — the pool re-runs each registered query in the background and overwrites the entry.

```csharp
// Register a pool that refreshes every 30s, optionally warming it immediately
SmartCachePool pool = mssql.RegisterCachePool(
    name: "dashboard",
    refreshEvery: TimeSpan.FromSeconds(30),
    maxIdleFires: 10,
    warmUp: async () =>
    {
        await mssql.Query<Server>().SmartCache("dashboard").ToListAsync();
        await mssql.Query<Player>().SmartCache("dashboard").CountAsync();
    });

// Opt a query into the pool
Result<List<Server>> servers = await mssql.Query<Server>()
    .Where(s => s.Online)
    .SmartCache("dashboard")
    .ToListAsync();

// Force an out-of-schedule refresh (e.g. right after a deploy)
await mssql.RefreshCachePoolAsync("dashboard");
```

- `maxIdleFires` (default 10, floored at 1): an entry that goes more than that many consecutive ticks without a read is dropped from the refresh list, bounding cardinality on parameterized queries. Idle accounting runs on every node, even ones that did not win the refresh lease.
- Smart cache is mutually exclusive with `.WithCache(ttl)` — if both are set the pool wins. An unknown pool name on `.SmartCache(name)` logs a warning and falls back to non-cached execution (no exception).
- Like `.WithCache`, smart cache is disabled inside a transaction scope.

`ProjectedQuery` also exposes `.SmartCache(pool)`.

### Pool & cache diagnostics

```csharp
IReadOnlyList<SmartCachePoolStats> pools = mssql.GetCachePoolStats();   // per-pool: entries, ticks, failures, last tick
QueryCacheStats stats = mssql.GetCacheStats();                          // totals, entries by table, table versions
```

## Multi-node coordination

By default the table-version counter is per-process, which is correct for a single node. For a cluster, plug in an `ICacheStore` (shared store such as Redis) and an `ICacheCoordinator` (cross-node invalidation seam):

```csharp
QueryCache.UseStore(myRedisStore);
QueryCache.UseCoordinator(myCoordinator);
```

- **`ICacheStore`** — the pluggable backing store for cache entries.
- **`ICacheCoordinator`** — `PublishInvalidationAsync` (a local mutation fans out so peers bump their version counters and evict matching entries without re-broadcasting), `OnInvalidation` (receive peers' broadcasts), and `TryAcquireRefreshLeaseAsync` (single-flight pool refresh — only the lease holder hits the DB; idle-entry retirement still runs on every node).
- The default `NullCacheCoordinator` is single-node: no fan-out, always grants the lease — identical behaviour off-cluster.

Pair a coordinator with a shared `ICacheStore` so non-leader nodes read the entry the leader writes.

## Transient-error retry

Complete non-caller-transaction operations that fail with a deadlock (`1205`), lock timeout (`1222`), or a recognized Azure SQL transient error are auto-retried with exponential backoff plus jitter.

| Setting | Default | Purpose |
|---------|---------|---------|
| `TransientRetryCount` | `3` | Retries after the initial attempt (0 disables). |
| `TransientRetryBaseDelayMs` | `50` | Base backoff; grows exponentially with jitter. |

> Statements inside an explicit transaction scope are **never** auto-retried — the whole transaction is the caller's to retry, since an inner statement can't be replayed in isolation.

## N+1 detection (not yet wired)

> The pieces are declared — the `N1DetectorThreshold` setting, the `N1QueryDetectedEvent`, and
> `QueryObservability.RecordN1` — but nothing in the query pipeline counts repeats or calls
> `RecordN1`, so setting `N1DetectorThreshold` currently has no effect and the event never
> fires. To spot the classic one-query-per-row loop today, subscribe to `QueryExecutedEvent`
> and group by SQL text. The fix is usually a single `WhereIn` / join instead of the loop —
> see [Query Builder](queries.md).

## Slow-query capture and estimated plans

Queries slower than `SlowQueryThresholdMs` publish a `SlowQueryEvent`. When enabled, estimated-plan capture reads the just-compiled ShowPlan XML from `sys.dm_exec_query_plan` on a separate connection, without executing user SQL a second time. This is the same plan document exposed by `SET SHOWPLAN_XML` and also works for parameterized `sp_executesql` commands. Permission, cache-eviction, and unsupported-query failures are logged without failing the original query.

```csharp
// Subscribe on the CodeLogic event bus
events.Subscribe<SlowQueryEvent>(e =>
{
    logger.Warn($"Slow query {e.ElapsedMs:n0}ms\n{e.ExplainJson}");
});
```

Every query also publishes a `QueryExecutedEvent` carrying a `CacheHit` flag, so you can measure cache effectiveness in aggregate.

## Compiled materializers & projection pushdown

- **Compiled materializers** — reflection runs once per entity at first use to build `EntityMetadata<T>` and a compiled reader-to-entity function. Subsequent reads map rows with no per-row reflection.
- **Projection pushdown** — `.Select(...)` emits a real `SELECT col1, col2, …` column list rather than `SELECT *`, transferring only the columns referenced. Combined with compiled mapping this often cuts row-transfer bandwidth substantially.

Both apply automatically to the builder, projections, joins, and raw `SqlQueryAsync<T>`.

## Batch sizes & limits

These per-database knobs live in `config.mssql.json`. Of them, only `CommandTimeout` currently reaches the code; the rest are declared but not yet read, so batching falls back to the built-in 500-row default:

| Setting | Default | Purpose |
|---------|---------|---------|
| `MaxBatchInsertSize` | `500` | Intended rows per chunk in `InsertManyAsync` / `UpsertManyAsync`. **Not yet plumbed through `GetRepository<T>()`**, which leaves the `Repository<T>` constructor's own `maxBatchInsertSize` default of 500 in place. |
| `MaxInClauseValues` | `1000` | Reserved — the expression visitor applies no cap to generated `IN (...)` lists. |
| `PreparedStatementCacheSize` | `256` | Reserved — not read by the current code. |
| `QueryTimeoutMs` | `30000` | Reserved — not read by the current code. |
| `CommandTimeout` | `30` | Command timeout in seconds; applied via the built connection string. |

Whatever chunk size is in force, it is reduced further per statement so a batch stays under
SQL Server's 2,100-parameter limit for the entity's column count (`SqlServerDialect.MaxBatchRows`).

For large inserts, prefer `InsertManyAsync` (chunked) over a loop of `InsertAsync` — fewer round trips, and each chunk is eligible for transient retry.

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MSSQL)
