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
pg.RegisterCachePool("active-users", TimeSpan.FromMinutes(1),
    ct => pg.Query<User>().Where(u => u.IsActive).ToListAsync());

var rows = await pg.Query<User>().SmartCache("active-users").ToListAsync();
```

Pools stop refreshing after `MaxIdleFires` consecutive ticks with no reads, so an unused pool
does not keep querying forever. `GetCachePoolStats()` reports per-pool hit counts, last read
and refresh state.

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
and upserts chunk at `min(maxBatchInsertSize, (65535 - reserved) / columnsPerRow)`, so a wide
table automatically gets smaller batches rather than failing at the wire.

Generated `IN` lists chunk at `maxInClauseValues`.

## Observability

### Slow queries

Queries at or over `slowQueryThresholdMs` raise a `SlowQueryEvent`. With
`captureExplainOnSlowQuery` the plan is attached as JSON from `EXPLAIN (FORMAT JSON)`.

The plan is estimated, not executed — `ANALYZE` is deliberately not used, since re-running
the statement would repeat any side effects.

### N+1 detection

Set `n1DetectorThreshold` above zero and the library counts query templates within a request
scope (`AsyncLocal`). When one template fires that many times in a single scope it raises
`N1QueryDetectedEvent` once, naming the template and the count — the signature of a loop
issuing one query per row.

### Counters

```csharp
var stats = pg.GetCacheStats();        // hits, misses, entries, evictions
var pools = pg.GetCachePoolStats();    // per-pool refresh state
```

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
