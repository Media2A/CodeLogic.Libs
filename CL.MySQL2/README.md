# CodeLogic.MySQL2

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.MySQL2)](https://www.nuget.org/packages/CodeLogic.MySQL2)

> **v4.0.0** — major rewrite. Typed LINQ translated to SQL, compiled row
> materializers, working result cache (time-quantized keys + table-version
> invalidation), SQL-side aggregation (`GroupBy` + aggregating `Select`),
> projection pushdown, covering indexes, attribute-driven retention. See the
> [Performance docs](../docs/articles/mysql2-performance.md) for benchmark numbers.

MySQL / Percona / MariaDB library for [CodeLogic](https://github.com/Media2A/CodeLogic).
Typed LINQ-shaped queries translated to SQL, compiled row materializers, working
result cache, server-side aggregation, and covering indexes driven by attributes.
Built on [MySqlConnector](https://mysqlconnector.net/).

## Install

```bash
dotnet add package CodeLogic.MySQL2
```

## Quick start

```csharp
await Libraries.LoadAsync<MySQL2Library>();

var mysql = Libraries.Get<MySQL2Library>();

// CRUD via the repository
var repo = mysql.GetRepository<UserRecord>();
await repo.InsertAsync(new UserRecord { Name = "Alice", Email = "alice@example.com" });

// Typed LINQ — translated to SQL
var activeAdmins = await mysql.Query<UserRecord>()
    .Where(u => u.IsActive && u.Role == "admin")
    .OrderBy(u => u.Name)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToPagedListAsync(page: 1, pageSize: 20);
```

## What's in the box

### Typed query builder

Full expression translation to SQL — no magic strings in consumer code.

| Capability | Shape |
|---|---|
| Filter | `.Where(x => x.Status == "active" && x.Age >= 18)` |
| Sort | `.OrderBy`, `.OrderByDescending`, `.ThenBy`, `.ThenByDescending` |
| Paging | `.Take`, `.Skip`, `.ToPagedListAsync` |
| String ops | `Contains` / `StartsWith` / `EndsWith` → `LIKE` (escaped) |
| IN | `list.Contains(x.Col)` → `x.Col IN (...)` |
| Null | `x.Col == null` → `IS NULL`; `string.IsNullOrEmpty(x.Col)` too |
| Nullable | `x.NullableCol.Value` passthrough |
| Join | `.Join<TRight, TKey, TResult>(leftKey, rightKey, resultSelector)` → typed equi-join |

### Typed JOINs _(new in 4.5.2)_

`Join<TRight, TKey, TResult>` translates a strongly-typed equi-join to real SQL
with table aliases and a compiled projection — only the columns the result
selector references cross the wire.

```csharp
var views = await mysql.Query<Order>()
    .Where(o => o.Total > 100)                  // carried over, re-qualified to the left table
    .Join<Customer, long, OrderView>(
        o => o.CustomerId,                       // left key
        c => c.Id,                               // right key
        (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name })
    .OrderByDescending((o, c) => o.Total)
    .Take(20)
    .ToListAsync();
```

- **Join types:** `Inner` (default), `Left`, `Right`.
- **Composite keys:** `o => new { o.A, o.B }` matched positionally with `c => new { c.X, c.Y }`.
- **Fluent surface on the join:** `.Where((l, r) => …)`, `.OrderBy` / `.OrderByDescending`,
  `.Take` / `.Skip`, and `ToListAsync` / `FirstOrDefaultAsync` / `CountAsync`.
- **`TRight` is explicit** (`Join<Customer, long, OrderView>`) — it can't be inferred
  from a lambda parameter.
- **Not cacheable yet** — the result cache versions entries by a single table, so a
  join can't be safely invalidated when the *other* table mutates. `.WithCache` /
  `.SmartCache` are intentionally absent on the join. Apply ordering/paging *after*
  `.Join`. The raw-string `Join(table, condition, type)` overload is still available
  for hand-written joins.

### Projection pushdown

`.Select<TResult>(x => new Foo(x.A, x.B))` emits a real column list — only the
columns referenced are transferred from the DB, not `SELECT *`.

```csharp
var lean = await mysql.Query<PostRecord>()
    .Where(p => p.PublishedUtc >= since)
    .Select(p => new { p.Id, p.Title, p.Slug })   // ships 3 columns, not 15
    .ToListAsync();
```

### SQL aggregation (GroupBy → Select)

`GroupBy` + aggregating `Select` translate to real `GROUP BY` + `SUM` / `AVG` /
`COUNT` / `MIN` / `MAX` / `ANY` on the server. No rows materialize client-side.

```csharp
var heatmap = await mysql.Query<SnapshotRecord>()
    .Where(s => s.SnapshotUtc >= since)
    .GroupBy(s => new { Dow  = SqlFn.DayOfWeek(s.SnapshotUtc),
                        Hour = SqlFn.Hour(s.SnapshotUtc) })
    .Select(g => new HeatmapCell(
        g.Key.Dow,
        g.Key.Hour,
        g.Average(x => (double)x.PlayerCount)))
    .ToListAsync();
```

Inside `Select(g => ...)` you can call:
`g.Key`, `g.Key.Member`, `g.Sum(x => ...)`, `g.Average(x => ...)`, `g.Min`, `g.Max`,
`g.Count()`, `g.Count(x => pred)`, `g.Any()`, `g.Any(pred)`.

Ternary inside aggregates becomes `CASE WHEN`:
`g.Sum(x => x.IsOnline ? 1 : 0)` → `SUM(CASE WHEN is_online THEN 1 ELSE 0 END)`.

### `SqlFn` helpers

SQL function markers recognised by the translator (same pattern as EF.Functions):

| Group | Methods |
|---|---|
| Date/time | `Year`, `Month`, `Day`, `Hour`, `Minute`, `DayOfWeek` (0=Sun..6=Sat), `Date`, `BucketUtc(d, seconds)` |
| Conditional | `Coalesce(...)`, `IfNull(v, fallback)` |
| String | `Lower`, `Upper`, `Concat(...)`, `Like(s, pattern)` |
| Math | `Round(v, digits)`, `Floor`, `Ceiling` |

### Result cache (now actually working)

```csharp
.WithCache(TimeSpan.FromMinutes(5))
```

Two things that weren't right before and now are:

1. **DateTime closures near "now" are time-quantized** — a `.Where(x => x.At >= UtcNow.AddDays(-30))`
   predicate no longer produces a unique cache key per call. The window is
   configurable (`CacheConfiguration.TimeQuantizeSeconds`, default 60s).
2. **Table-version invalidation** — mutations bump a per-table counter baked into
   the key. No eviction loop; old keys simply become un-hittable.

Cache hits and misses publish `CacheHitEvent` / `CacheMissEvent` on the
CodeLogic event bus.

### Smart cache pools — kept warm in the background _(new in 4.2)_

For pages where a small set of queries should stay hot regardless of read
traffic, register a **named pool** with a refresh interval and opt queries
into it. The pool's background timer re-runs every registered query and
overwrites the cache entry — readers never block on the DB after the first
populate.

```csharp
// Declare the pool once at startup (typically in OnInitializeAsync).
mysql.RegisterCachePool("dashboard", refreshEvery: TimeSpan.FromSeconds(30));

// Opt queries into the pool. First call: cold DB hit, result cached, query
// registered with the pool. Subsequent calls: cache hit. Every 30s the
// pool's timer re-runs the query and refreshes the entry.
var top10 = await mysql.Query<PlayerRecord>()
    .Where(p => p.IsActive)
    .OrderByDescending(p => p.Score)
    .Take(10)
    .SmartCache("dashboard")
    .ToListAsync();

// Out-of-schedule refresh — useful right after a deploy to prime the cache.
await mysql.RefreshCachePoolAsync("dashboard");

// Warm the cache on startup — readers never see a cold pool. The warm-up
// callback just calls the queries that should be hot; they auto-register
// with the pool via .SmartCache("dashboard") as usual. Runs as a fire-and-
// forget task so startup doesn't block.
mysql.RegisterCachePool("dashboard", TimeSpan.FromSeconds(30),
    warmUp: async () =>
    {
        await statsService.GetDashboardTop10Async();
        await statsService.GetRecentMatchesAsync();
    });

// Diagnostic snapshot
foreach (var s in mysql.GetCachePoolStats())
    Console.WriteLine($"{s.Name}: {s.EntryCount} entries, {s.TicksFired} ticks fired");
```

How it behaves:

- **TTL is derived from the pool** — `refreshEvery * 2`. Cache entries
  outlive a missed refresh by one cycle before falling back to cache-aside.
- **Bounded cardinality** — an entry that has not been read for
  `MaxIdleFires` (default 3) consecutive ticks is dropped from the refresh
  list. Parameterized queries (per-user keys, etc.) don't spawn unbounded
  background work — they auto-retire when nobody's looking.
- **Mutually exclusive with `.WithCache(TimeSpan)`** — if both are set, the
  pool wins.
- **Falls back gracefully** — an unknown pool name on `.SmartCache(name)`
  logs a warning and the query runs uncached. No exception.
- **Skipped inside transactions** — same rule as `.WithCache`.
- **Single-node only** — coordination across multiple app instances is on
  the roadmap. With a Redis-backed `ICacheStore` today, every node will run
  its own refresh timer; that's safe but wasteful at high node counts.

### Bulk writes / predicate mutations

```csharp
// Real batched INSERT ... VALUES (...), (...), ... (configurable chunk size)
await repo.InsertManyAsync(rows);

// Typed bulk update — one UPDATE statement, values or column expressions
await mysql.Query<TicketRecord>()
    .Where(t => t.Status == "open" && t.CreatedUtc < cutoff)
    .UpdateAsync(t => new TicketRecord { Status = "stale", Counter = t.Counter + 1 });

// Bulk delete by predicate
await mysql.Query<SnapshotRecord>()
    .Where(s => s.SnapshotUtc < cutoff)
    .DeleteAsync();
```

### Schema sync driven by attributes

Record classes are the source of truth. `SyncTableAsync<T>()` creates / alters
MySQL tables to match.

```csharp
[Table(Name = "servers_snapshot")]
[RetainDays(90, nameof(SnapshotUtc))]                    // daily background purge
[CompositeIndex("ix_server_snapshot", "server_id", "snapshot_utc")]
public sealed class SnapshotRecord
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "server_id", DataType = DataType.BigInt, NotNull = true)]
    public long ServerId { get; set; }

    [Column(Name = "snapshot_utc", DataType = DataType.DateTime, NotNull = true)]
    [Index(Name = "ix_snapshot_utc_covering",
           Include = new[] { nameof(ServerId), nameof(PlayerCount) })]  // covering index
    public DateTime SnapshotUtc { get; set; }

    [Column(Name = "player_count", DataType = DataType.Int, NotNull = true)]
    public int PlayerCount { get; set; }
}
```

Attributes supported:

| Attribute | Purpose |
|---|---|
| `[Table]` | table name, engine, charset, collation, comment |
| `[Column]` | type, size, nullability, default, PK/AI/Unique/Index |
| `[Index]` | **new** — named, unique, covering (`Include`) |
| `[CompositeIndex]` | multi-column named index on the class |
| `[ForeignKey]` | FK with ON DELETE/UPDATE actions |
| `[RetainDays]` | **new** — background purge job for time-series tables |
| `[Ignore]` | skip property for schema / read / write |

### Observability

The library publishes events to CodeLogic's event bus:

- `QueryExecutedEvent` — every query: SQL, elapsed ms, row count, cache hit flag
- `SlowQueryEvent` — queries over the threshold (logged too)
- `CacheHitEvent` / `CacheMissEvent` — per-call
- `N1QueryDetectedEvent` — when the detector trips
- `TableSyncedEvent`, `DatabaseConnected` / `Disconnected` — lifecycle

### Background retention worker

Entities with `[RetainDays(N, nameof(TimestampCol))]` are picked up automatically
when registered via `SyncTableAsync<T>()`. A daily pass runs batched
`DELETE WHERE {col} < NOW() - INTERVAL N DAY LIMIT batchSize` until drained.

## Configuration

Two config sections are auto-generated on first run under
`data/codelogic/Libraries/CL.MySQL2/`.

`config.mysql.json` — per-database settings. Highlights:

| Field | Default | Purpose |
|---|---|---|
| `Host`, `Port`, `Database`, `Username`, `Password` | — | connection |
| `EnablePooling`, `MinPoolSize`, `MaxPoolSize`, `ConnectionLifetime` | true/1/100/300s | pool |
| `SchemaSyncLevel` | `Safe` | None / Safe / Additive / Full |
| `SlowQueryThresholdMs` | 1000 | slow query logging |
| `QueryTimeoutMs` | 30000 | default per-query timeout |
| `MaxBatchInsertSize` | 500 | `InsertManyAsync` chunk size |
| `MaxInClauseValues` | 1000 | IN-clause safety cap |
| `PreparedStatementCacheSize` | 256 | per-connection |
| `N1DetectorThreshold` | 0 (off) | warn on repeated query in request scope |
| `CaptureExplainOnSlowQuery` | true | attach EXPLAIN to `SlowQueryEvent` |
| `DefaultStringSize` | 255 | VARCHAR size when no `[Column(Size)]` |
| `CacheEnabledOverride` | null (inherit) | per-DB cache on/off |

`config.mysql.cache.json` — global cache settings. Highlights:

| Field | Default | Purpose |
|---|---|---|
| `Enabled` | true | master switch |
| `MaxEntries` | 10000 | soft cap |
| `DefaultTtlSeconds` | 60 | when `.WithCache()` has no TTL |
| `TimeQuantizeSeconds` | 60 | round DateTime params in cache keys |
| `PublishEvents` | true | emit hit/miss events |

## Transactions

```csharp
await using var tx = await mysql.BeginTransactionAsync();
var repo = mysql.GetRepository<AccountRecord>(tx);
// ... work ...
await tx.CommitAsync();  // auto-rolls back on dispose if not committed
```

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- .NET 10
- MySQL 5.7+ / MariaDB 10.2+ / Percona 8.x

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
