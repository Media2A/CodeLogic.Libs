# CL.PostgreSQL — Performance & Caching

## Result cache

Opt in per query:

```csharp
var rows = await pg.Query<User>()
    .Where(u => u.IsActive)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToListAsync();
```

### Cache keys

A key is the SHA-256 of the connection id, table name, the table's current version, the SQL
text, and the parameters sorted by name. Including the connection id keeps tenants on
separate connections from sharing entries.

`byte[]` parameter values are hex-encoded rather than `ToString()`-ed — otherwise every
binary value would stringify to `System.Byte[]` and collapse into one key.

### Time quantization

`Where(x => x.At >= DateTime.UtcNow.AddDays(-30))` produces a different parameter value on
every call, so it would never hit cache. `DateTime` parameters within a year of now are
rounded down to a `timeQuantizeSeconds` bucket (default 60) before hashing, making the key
stable across back-to-back calls. Far-past and far-future absolute dates pass through
unrounded.

### Table-version invalidation

Every table carries a version counter that participates in the key. Any write through the
library bumps it, which makes every prior entry for that table unreachable at once — no
scanning, no per-key eviction. Stale entries are swept later by TTL.

```csharp
QueryCache.Invalidate<User>();      // or Invalidate("users")
```

### Failure results are never cached

A `Result<T>` in a failed state is not written, and a cached value that is detected as a
failure is evicted on read. A transient database error during a cold warm-up therefore
cannot poison the cache for the whole TTL.

### Stampede protection

Concurrent misses on the same cold key collapse into a single execution. The first caller
creates the in-flight task and the rest await it, so a popular key expiring does not produce
a thundering herd.

## Smart cache pools

A pool is a named, pre-warmed entry that refreshes itself in the background rather than
expiring and forcing a caller to wait.

```csharp
// The warm-up callback is a Func<Task>; queries register themselves with the pool
// through their own .SmartCache(name) decoration.
pg.RegisterCachePool("active-users", TimeSpan.FromMinutes(1), maxIdleFires: 10,
    warmUp: () => pg.Query<User>().Where(u => u.IsActive).SmartCache("active-users").ToListAsync());

var rows = await pg.Query<User>().SmartCache("active-users").ToListAsync();
```

An individual entry is dropped from the pool after `MaxIdleFires` consecutive refresh ticks
with no read (default 10), so an unread parameterisation stops being refreshed; the pool's
timer itself keeps running. `GetCachePoolStats()` reports the refresh interval, `MaxIdleFires`,
the live entry count, ticks fired and failed, and the last tick time.

## Multi-node

The default store is in-process. For several nodes, supply a shared store **and** a
coordinator:

```csharp
QueryCache.UseStore(new RedisCacheStore(...));
QueryCache.UseCoordinator(new RedisCacheCoordinator(...));
```

Both are needed. The store shares entries; the coordinator broadcasts invalidations so a
write on one node bumps the table version everywhere. A shared store **without** a
coordinator is the dangerous combination: each node's version counter advances
independently, so node B keeps serving entries that node A's write should have invalidated.

The coordinator also single-flights pool refreshes across nodes via a refresh lease, so one
expiring pool does not trigger the same query on every node at once.

## Compiled materializers

Rows are mapped by an expression-tree materializer compiled once per type and specialised to
the reader's column ordinals, not by per-row reflection. Property accessors on
`EntityMetadata<T>` are compiled getters and setters for the same reason.

Projections go further: `Select(u => new { u.Id, u.Email })` narrows the generated `SELECT`
to those columns and compiles a mapper for the projection shape, so unselected columns are
never transferred or materialized.

## Transient retry

Serialization failures and deadlocks (SQLSTATE `40001` and `40P01`) mean the transaction was
rolled back cleanly and re-running it may succeed. Those are retried
`transientRetryCount` times with exponential backoff and jitter. Lock-not-available (`55P03`)
is treated the same way.

Other errors are not retried: a constraint violation or a syntax error will fail identically
on a second attempt.

## Batch limits

PostgreSQL's extended protocol caps a statement at 65535 bound parameters. Batched inserts
and upserts chunk at `min(batchSize, (65535 - 16) / columnsPerRow)`, so a wide table
automatically gets smaller batches rather than failing at the wire. `batchSize` is the
repository's own default of 500 — the `maxBatchInsertSize` config value is not currently
plumbed through to it.

Generated `IN` lists are **not** chunked: `ids.Contains(x.Id)` emits one bound parameter per
value in a single list, and `maxInClauseValues` is not consulted. Chunk large sets yourself.

## Observability

### Slow queries

Queries at or over `slowQueryThresholdMs` are logged as a warning and raise a
`SlowQueryEvent` carrying the connection id, SQL text and elapsed milliseconds.

`SlowQueryEvent.ExplainJson` and the `captureExplainOnSlowQuery` setting are reserved for a
planned `EXPLAIN (FORMAT JSON)` capture that is **not implemented**: no call site runs
`EXPLAIN`, so the field is always null.

### N+1 detection

`N1QueryDetectedEvent` and the `n1DetectorThreshold` setting are likewise reserved. No
request-scope template counting exists yet, so the event is never published and the setting
has no effect at any value.

### Counters

```csharp
var stats = pg.GetCacheStats();        // TotalEntries, EntriesByTable, TableVersions
var pools = pg.GetCachePoolStats();    // per-pool refresh state
```

`GetCacheStats()` is a structural snapshot — how many entries exist and per table, plus the
table-version counters. It does not track hit/miss or eviction counts; subscribe to
`CacheHitEvent` / `CacheMissEvent` for those.

`QueryExecutedEvent` fires after every query with its SQL, elapsed milliseconds, row count
and cache-hit flag, which is usually the easiest hook for metrics or tracing.

## Indexing notes

- `[Index(Include = [...])]` emits a real `INCLUDE` clause, giving an index-only scan without
  widening the key.
- `[Column(Unique = true)]` and unique `[CompositeIndex]` create **constraints**, not bare
  indexes, so `ON CONFLICT` can arbitrate on them. A plain unique index cannot be a conflict
  target by name.
- Identifiers are quoted and therefore case-sensitive; an index created outside the library
  on `userId` will not be matched against a model column named `userid`.
