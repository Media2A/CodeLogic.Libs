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

MIT — see [LICENSE](../LICENSE)
