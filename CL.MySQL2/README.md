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
| Subquery | `.WhereExists<TInner>((o, i) => …)`, `.WhereIn<TInner, TKey>(col, innerCol, filter?)` |

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

### Subquery filters — EXISTS / IN _(new in 4.5.2)_

Correlated and uncorrelated subqueries in the WHERE clause, without dropping to raw SQL:

```csharp
// Orders that have at least one sent shipment (correlated EXISTS)
await mysql.Query<Order>()
    .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent")
    .ToListAsync();

// Orders whose customer is a VIP (IN over a filtered subquery)
await mysql.Query<Order>()
    .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip)
    .ToListAsync();
```

| Method | SQL |
|---|---|
| `WhereExists<TInner>((o, i) => …)` | `EXISTS (SELECT 1 FROM inner WHERE …)` |
| `WhereNotExists<TInner>(…)` | `NOT EXISTS (…)` |
| `WhereIn<TInner, TKey>(col, innerCol, filter?)` | `col IN (SELECT innerCol FROM inner [WHERE …])` |
| `WhereNotIn<TInner, TKey>(…)` | `col NOT IN (…)` |

Subquery filters compose with ordinary `.Where(...)`. They are **not cacheable**
(the cache can't track the inner table for invalidation) and can't be combined
with a typed `.Join`. `WhereExists` against the outer query's own table is rejected.

### Raw SQL escape hatch _(new in 4.5.2)_

For the rare query the translator can't express, drop to SQL without losing the
compiled materializer, observability, or the transient-retry policy:

```csharp
var rows = await mysql.SqlQueryAsync<UserRecord>(
    "SELECT * FROM users WHERE country = @c", new() { ["@c"] = "DK" });   // materialized rows
var n   = await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM users"); // single value
var hit = await mysql.ExecuteSqlAsync("UPDATE users SET active = 0 WHERE last_seen < @t",
                                      new() { ["@t"] = cutoff });          // affected count
```

Always pass values via named parameters — never interpolate user input into the SQL.
Raw queries are not cached.

### Soft deletes _(new in 4.5.2)_

Mark a nullable-`DateTime` column with `[SoftDelete]` and deletes become timestamp
updates; reads hide the deleted rows automatically.

```csharp
[Table(Name = "accounts")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class Account
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "deleted_utc", DataType = DataType.DateTime)] public DateTime? DeletedUtc { get; set; }
}

var repo = mysql.GetRepository<Account>();
await repo.DeleteAsync(id);                 // soft: sets deleted_utc = UtcNow
await mysql.Query<Account>().ToListAsync(); // excludes soft-deleted rows
await mysql.Query<Account>().IncludeDeleted().ToListAsync();  // includes them
await repo.HardDeleteAsync(id);             // physical DELETE
```

Auto-filtering covers single-table `mysql.Query<T>()` reads and the repository getters.
It does **not** apply to joins, subqueries, or the query builder's bulk
`UpdateAsync`/`DeleteAsync` — those stay raw so you can restore or hard-purge deleted
rows. To restore: `Query<Account>().Where(a => a.Id == id).UpdateAsync(a => new Account { DeletedUtc = null })`.

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

**Stampede protection** _(new in 4.5.2)_ — concurrent misses on the same cold key
collapse to a single DB hit (single-flight); the rest await that one execution.

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
- **Multi-node coordination** _(new in 4.5.2)_ — install an `ICacheCoordinator`
  via `QueryCache.UseCoordinator(...)` so mutations fan out to peers and pool
  refreshes run single-flight (only the lease-holding node hits the DB). Pair it
  with a shared `ICacheStore` (Redis). Without a coordinator the default is
  single-node — every node runs its own refresh timer, which is safe but
  wasteful at high node counts.

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

// Upsert — INSERT ... ON DUPLICATE KEY UPDATE
await repo.UpsertAsync(row);
await repo.UpsertManyAsync(rows);
await repo.UpsertWithIncrementsAsync(
    seed,
    incrementProperties: new[] { nameof(Counter.Hits) });  // col = col + new.col on conflict
```

> **Portable upserts** _(fixed in 4.5.3)_ — the upsert methods emit the
> `VALUES(col)` conflict form, which works on **both MySQL and MariaDB** (the older
> `INSERT ... AS new` row-alias syntax was MySQL-8.0.19+ only and MariaDB rejected it).

### Schema sync — three modes + CRC fast-path _(new in 4.5.3)_

Record classes are the source of truth. `SyncTableAsync<T>()` creates / alters
MySQL tables to match; `SyncSchemaAsync(params Type[])` reconciles a whole set in
one pass under a single cross-node lock (the recommended startup entry point).

```csharp
await mysql.SyncSchemaAsync(typeof(UserRecord), typeof(OrderRecord), typeof(SnapshotRecord));
```

A new operator-facing **`SyncMode`** (per database) governs how aggressively sync
reconciles:

| Mode | Behaviour |
|---|---|
| `Developer` | Aggressive rolling updates — drops removed columns/indexes/FKs every boot. |
| `Production` *(default)* | Additive only — adds/modifies, **never drops**. A change needing a drop is deferred and the table flagged `DriftPending`. |
| `Migration` | One-shot destructive reconcile (backup first). Idempotent; once everything matches it logs a warning to switch back to `Production`. |

`SyncMode` supersedes the legacy `SchemaSyncLevel` / `AllowDestructiveSync` knobs
(still honoured for back-compat). Flip the mode at runtime with
`mysql.SetSyncMode(SyncMode.Production)`.

**CRC sentinel — `__schema_state`.** Each model's desired schema is hashed (CRC)
into a per-table row. Sync **skips a table entirely** — no `information_schema`
diffing, no DDL — when the stored CRC matches the model and the table still
exists. `SyncResult` carries `Skipped`, `SchemaCrc`, and `DriftPending`; inspect
state via `mysql.SchemaState`.

**Cross-node lock.** Schema/migration passes serialize across nodes via MySQL
`GET_LOCK` — booting a cluster, only one node runs the DDL while the rest wait and
then no-op on matching CRCs.

### Imperative migrations _(new in 4.5.3)_

For data transforms, seeds, and semantic changes the declarative sync can't
express, write an `IMigration` (or subclass the `Migration` base):

```csharp
public sealed class SeedRoles() : Migration("1.4.0", order: 1, "Seed default roles")
{
    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("INSERT INTO roles (name) VALUES ('admin'), ('user')", ct: ct);

    // Override DownAsync to make the migration reversible (default throws).
    public override async Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("DELETE FROM roles WHERE name IN ('admin','user')", ct: ct);
}

mysql.RegisterMigration(new SeedRoles());        // or RegisterMigrationsFrom(assembly)
await mysql.MigrateAsync();                       // apply all pending, in order
var pending = await mysql.GetPendingMigrationsAsync();
await mysql.RollbackAsync(new MigrationVersion("1.3.0", 0));  // undo everything newer
```

- Migrations run in `MigrationVersion` (app-version, then order) sequence, each in
  its own transaction, under the shared schema-sync lock, gated by the app version.
- `IMigrationContext` exposes `ExecuteAsync`, `QueryAsync<T>`, `ScalarAsync<T>`,
  plus `SyncTableAsync<T>()` to bring a table to its model shape inside the step.
- Tracked in the `__migrations` table; an edited-after-apply body is detected by
  checksum drift and warned about.
- `RollbackAsync` runs `DownAsync` newest-first and aborts cleanly before any
  change if a migration in range has no `DownAsync` override. To roll back a
  declarative table instead, `mysql.RestoreSchemaAsync(tableName)` replays a schema
  backup (DDL only) and clears its `__schema_state` row.

> MySQL implicitly commits on DDL — a migration mixing `ALTER` with data changes
> is not atomic, so keep `UpAsync` steps idempotent.

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
| `[Column]` | type, size, nullability, default, PK/AI/Unique/Index, `PreviousName` (rename) |
| `[Index]` | **new** — named, unique, covering (`Include`) |
| `[CompositeIndex]` | multi-column named index on the class |
| `[ForeignKey]` | FK with ON DELETE/UPDATE actions |
| `[RetainDays]` | **new** — background purge job for time-series tables |
| `[Ignore]` | skip property for schema / read / write |

**Sync modes** (`SyncMode`, per database — see above): `Production` (default,
additive) · `Developer` (drops freely) · `Migration` (one-shot destructive). These
map onto the legacy lower-level `SchemaSyncLevel` (`None` · `Safe` · `Additive` ·
`Full`), which is retained for back-compat — `SyncMode` takes precedence.

**Renaming a column** _(new in 4.5.2)_ — set `PreviousName` so the rename preserves data:

```csharp
[Column(Name = "email_address", PreviousName = "email")]   // → CHANGE COLUMN email email_address …
public string EmailAddress { get; set; } = "";
```

Without `PreviousName`, a rename looks like a new column plus an orphaned old one
(and a data-losing `DROP` at `Full`). Drop the `PreviousName` once every environment
has synced.

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
| `SyncMode` | `Production` | Developer / Production / Migration — primary schema-sync knob |
| `SchemaSyncLevel` | `Safe` | Legacy. None / Safe / Additive / Full (superseded by `SyncMode`) |
| `SlowQueryThresholdMs` | 1000 | slow query logging |
| `QueryTimeoutMs` | 30000 | default per-query timeout |
| `MaxBatchInsertSize` | 500 | `InsertManyAsync` chunk size |
| `MaxInClauseValues` | 1000 | IN-clause safety cap |
| `PreparedStatementCacheSize` | 256 | per-connection |
| `TransientRetryCount` | 3 | auto-retry deadlock/lock-wait on single statements (0 = off) |
| `TransientRetryBaseDelayMs` | 50 | base backoff for transient retries (exponential + jitter) |
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

## Testing

A console test runner lives at `CL.MySQL2/tests/CL.MySQL2.Tests`. It boots the
library through the full CodeLogic lifecycle against a local MySQL/MariaDB and
exercises queries, schema sync, the sync modes, migrations, and rollback.

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- .NET 10
- MySQL 5.7+ / MariaDB 10.2+ / Percona 8.x

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
