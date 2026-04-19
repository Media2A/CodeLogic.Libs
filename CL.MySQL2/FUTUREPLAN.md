# CL.MySQL2 — Future Plan

Roadmap for turning CL.MySQL2 into a "fast by default" ORM that stays
strongly-typed and lets app code stay declarative. Driven by real-world
hotspots surfaced in FragHunt (homepage widget was 3.6s on a 10k-row table
because aggregation ran in C# instead of SQL).

Items are grouped by area and ranked by impact/effort. See **Priority**
section at the bottom for the suggested implementation order.

---

## Query shape & translation

### 1. SQL-side aggregation in the expression visitor
Translate `GroupBy` + `Sum/Avg/Count/Min/Max/Any/All` into real `GROUP BY` +
aggregate SQL. Add terminal scalar awaits: `SumAsync`, `MaxAsync`,
`MinAsync`, `AverageAsync`, `CountAsync`.

**Why:** biggest single win. Currently app code pulls every row back and
aggregates in LINQ — multi-megabyte transfers to compute one number.

### 2. Projection pushdown (`.Select(...)` before materialize)
`.Select(s => new { s.A, s.B })` should emit `SELECT a, b` instead of
`SELECT *`. Kills VARCHAR/TEXT transfer cost when only a few columns are
needed.

### 3. SQL function helpers
`SqlFn.Hour`, `SqlFn.DayOfWeek`, `SqlFn.Date`, `SqlFn.BucketUtc(col, seconds)`,
`SqlFn.Coalesce`, `SqlFn.IfNull`. Emit verbatim in SQL, best-effort fallback
in-memory for unit tests.

### 4. Ternary in aggregate → `CASE WHEN`
`g.Sum(x => x.IsOnline ? 1 : 0)` → `SUM(CASE WHEN is_online THEN 1 ELSE 0 END)`.
Bool-to-int is idiomatic and needed for uptime / availability math.

### 5. Nested grouping / subquery `FROM (SELECT ...)`
`.GroupBy(...).Select(...).GroupBy(...)` emits `SELECT ... FROM (SELECT ...) t`.
Needed for "sum across dimension A per instant, then avg per bucket" patterns.

### 6. `Any()` / `Count() > 0` → `EXISTS` / `LIMIT 1`
Cheap existence checks shouldn't pull rows. `First()` / `Single()` → `LIMIT 1`.

### 7. Join support (`.Join(...)` / navigation includes)
Today multi-table lookups get written as N+1 in services. A proper `JOIN`
translator removes a whole class of slowness.

### 8. Batched `In(list)` → parameterized IN
`Enumerable.Contains(list, field)` already works. With very large lists,
auto-chunk or fall back to a temp table so we don't hit MySQL's parameter
limit.

---

## Caching

### 9. Fix `WithCache` on parameterized queries
Benchmark showed `WithCache` doesn't hit on snapshot queries. Likely the cache
key hashes the lambda (including `DateTime.UtcNow.AddDays(-30)`, which is
unique per call) rather than the final SQL + parameter values. Fix: derive
key from emitted SQL text + parameter dict.

### 10. Cache key observability
Log mode that prints `cache hit/miss on key=X sql=Y`. Makes debugging
deterministic.

### 11. Distributed cache hook
Add `ICacheStore` interface so Redis / memcached can be plugged in —
required for multi-node deploys.

### 12. Automatic invalidation by table write
After `InsertAsync<T>`/`UpdateAsync<T>`/`DeleteAsync<T>`, bump a table version
counter that participates in cache keys. Eliminates stale-read footguns
without TTL guesswork.

---

## Materialization (row → object)

### 13. Compiled materializer per type
Reflection-based mapping costs hundreds of ms for 10k rows. One-time
`Expression`-compiled delegate per `T`, cached. Drop-in; no API change.

### 14. Streaming `IAsyncEnumerable<T>`
For huge result sets, yield rows as they arrive instead of building a
`List<T>`. Bounded memory for exports / migrations.

### 15. Struct / read-only record support
Allow `readonly record struct` DTOs from `.Select(...)` — zero heap
allocation per row.

---

## Indexes & schema

### 16. Composite index declaration
Today `[Column(Index = true)]` → one index per column. Need
`[CompositeIndex("ServerId", "SnapshotUtc")]` on the class so schema sync
creates `(server_id, snapshot_utc)`. Lets per-entity range scans go
index-only.

### 17. Covering-index hints
`[Column(Include = true)]` to carry extra columns at the leaf — avoids PK
lookup per row. The "index fixes SELECT * latency" trick.

### 18. Index advisor / `EXPLAIN` integration
On slow query, run `EXPLAIN FORMAT=JSON` and surface advice: "full scan on
table X, consider index on Y". Turns the slow log from passive → actionable.

### 19. Retention / TTL declarations
`[RetainDays(90)]` on a record class → library-run background purge worker.
Manual `PurgeOldHistoryAsync` becomes metadata.

---

## Connection & transport

### 20. Prepared statement cache
Prepare + reuse for repeated SQL text + param shape. Meaningful win on
high-QPS paths.

### 21. Server-side `COUNT` cache for dashboards
InnoDB `COUNT(*)` is a scan. Maintain atomic counters updated via Insert/
Delete hooks for tables flagged `[Countable]`.

### 22. Connection pool tuning surfaced in config
Live metrics (pool exhaustion, wait time). Makes "why is everything slow
under load" debuggable.

---

## Observability

### 23. Per-method timing events
`SlowQueryEvent` exists. Add `QueryExecutedEvent` (always fires with SQL +
elapsed) so apps can build Grafana / dashboards.

### 24. Query plan snapshot on slow
When `SlowQueryEvent` fires, attach `EXPLAIN FORMAT=JSON` automatically.

### 25. N+1 detector
If the same query template fires N times within a request scope with only
parameter changes, log a warning. Would have caught FragHunt's old
`GetGlobalDashboardStatsAsync` on day one.

---

## Writes

### 26. Bulk `InsertManyAsync` / `UpsertManyAsync`
Today `foreach InsertAsync` is per-row round-trips. One
`INSERT ... VALUES (...), (...)` is ~100× faster for batch writes.

### 27. Batched updates / bulk delete by predicate
`Query<T>().Where(x => x.SnapshotUtc < cutoff).DeleteAsync()` → single
`DELETE WHERE ...`. Current `PurgeOldHistoryAsync` is fetch-then-delete-per-row.

---

---

## Additions discovered during full-codebase review

The items above were identified from FragHunt hotspots. A pass over all 18
source files surfaced these additional issues, dead wood, and lift-ups.

### Repository hot-path

### 28. `Repository.InsertManyAsync` is not bulk
Currently loops `InsertAsync` per row → N round-trips. Needs real batched
`INSERT ... VALUES (...), (...)` with configurable chunk size and a single
cache invalidation at the end. Folds into #26.

### 29. `Repository.IncrementAsync` / `DecrementAsync` are almost identical
Collapse to one `AdjustAsync(selector, delta)` — a negative `delta` decrements.
Also: MySqlConnector `AddWithValue("@amount", amount)` boxes the generic
`TProperty` and discards type metadata. Emit typed parameters.

### 30. `Repository.MapReaderToEntity` does a linear scan per column per row
O(columns²). With the compiled materializer (#13) this collapses to O(columns)
lookup at compile-time. In the meantime, at minimum cache a
`Dictionary<string, PropertyInfo>` per type.

### 31. `Repository` duplicates `GetCachedProperties`/`GetColumnName`/`GetTableName`
Three files have private copies (`Repository`, `QueryBuilder`,
`SchemaAnalyzer`). Move to one `EntityMetadata<T>` class with all cached
reflection, shared across the library.

### Query builder / expression visitor

### 32. `WhereCondition` / `OrderByClause` / `JoinClause` / `AggregateFunction` models are unused
`QueryModels.cs` defines them but nothing in the library consumes them — the
fluent builder takes expressions directly. Delete or actually wire them into
a programmatic query API (decide which).

### 33. `QueryBuilder.Join(string, string, JoinType)` takes raw strings
Two string args for table + condition. With proper typed join (#7) this
whole overload becomes unneeded. Keep only the typed form.

### 34. `QueryBuilder.Select(Expression<Func<T, object?>>)` doesn't change the return type
The method writes `SELECT col1, col2` into the SQL but `ToListAsync` still
materializes the full `T`. Projection pushdown (#2) needs to return
`IQuery<TResult>` so the reader knows what shape to hydrate.

### 35. Param re-keying in `Where(...)` is O(n) string replacement per param
For each predicate, the visitor emits `@p0, @p1, ...` and then the builder
replaces each with `@qb_0, @qb_1, ...`. On a long `WHERE` this is a
quadratic string churn. Emit with the final counter directly from the visitor.

### 36. `MySqlExpressionVisitor.GetValue` compiles+invokes an expression lambda for every closure member
`Expression.Lambda(...).Compile().DynamicInvoke()` is ~100µs each time and
GCs a delegate. Walk the expression tree and read `FieldInfo` /
`PropertyInfo` / `ConstantExpression` directly for the common cases.

### 37. Expression visitor doesn't handle many common shapes
- `string.IsNullOrEmpty(col)` → `(col IS NULL OR col = '')`
- `col.Length`, `list.Count` on materialized collections
- `x.Col1 == x.Col2` (cross-column compare within same row)
- Nullable `x.Col.Value` unwraps
- `DateTime.UtcNow` / `DateTime.Now` used inside a lambda (treat as parameter, not invalid)

Add them one by one with tests.

### 38. No support for `UNION` / `UNION ALL`
Mostly comes up in dashboards combining two queries of the same shape.
Nice-to-have, not on the critical path.

### Cache

### 39. `QueryCache` is a static singleton
Can't run two isolated instances (e.g. in tests). Make it instance-based,
stored on the library. Keep static convenience wrappers for back-compat.

### 40. `QueryCache` cache key includes the literal SQL text plus params
Correct in principle. But the `sql` is rebuilt from the expression tree each
call and may contain timing-sensitive data — e.g. `.Where(s => s.X >= DateTime.UtcNow.AddDays(-30))`
bakes a fresh timestamp into the closure every time, which makes every call
unique. Fix: detect `DateTime.UtcNow`-derived closures and skip them from the
key, or document that such predicates defeat the cache. Probably the root
cause of the FragHunt cache misses we saw.

### 41. Cache capacity is hardcoded (1000)
Surfacing via `DatabaseConfiguration` so ops can tune. Also needs a memory-
weighted eviction (current "oldest 25%" ignores entry size).

### 42. Cache doesn't publish events
Silent. Add `CacheHitEvent` / `CacheMissEvent` (or fold into
`QueryExecutedEvent`) so observability works end-to-end.

### Connection manager

### 43. `ConnectionManager.CloseConnectionAsync` finds the connection ID by string-matching the connection string
Linear scan of `_configs` per close, comparing entire connection strings.
Replace with either (a) wrapping `MySqlConnection` in a small owning record
that knows its `connectionId`, or (b) tracking the id via `AsyncLocal` during
`OpenConnectionAsync`.

### 44. `_openCounts` is incremented in both `OpenConnectionAsync` and
`ExecuteWith*Async`, then decremented in both close paths
If the caller awaits `OpenConnectionAsync` directly and also uses
`using var conn = ...`, the counter increments once but decrements twice.
Clean this up when refactoring #43 — single ownership.

### 45. `TestConnectionAsync` opens a connection just to `SELECT 1`
Add a ~5s timeout override so a broken DB doesn't make startup hang on the
default 30s connection timeout.

### 46. `GetAllConnectionCounts` returns a fresh dictionary every call
Hot path if surfaced on `/health`. Either a lock-free immutable snapshot, or
expose the live `ConcurrentDictionary` read-only.

### Schema / sync

### 47. `TableSyncService.SyncTablesAsync` calls `SyncTableAsync<T>` via reflection per type
`typeof(TableSyncService).GetMethod(...).MakeGenericMethod(type).Invoke(...)`
per type — slow and GC-heavy. Refactor: split sync into a non-generic core
(`Type` arg) used by both, and make the generic wrapper a one-liner.

### 48. Schema diff fires one round-trip per entity type to
`information_schema.COLUMNS`, `.STATISTICS`, `.KEY_COLUMN_USAGE`
With a dozen tables that's ~36 queries. Batch: `WHERE TABLE_NAME IN (...)` and
cache results for the duration of a sync pass.

### 49. `ColumnNeedsModify` comment diff triggers `ALTER` for any char difference
Including trailing whitespace / casing. Use a tolerant comparison (`Trim`,
`OrdinalIgnoreCase`). Small quality-of-life fix.

### 50. `SchemaAnalyzer` has no DDL for generated columns, check constraints, or fulltext
Not critical, but document that they're unsupported so users don't silently
lose them on sync.

### 51. No integration with `[Index]` name collision checking
Two columns with `Column(Index = true)` and the same column name produce
the same `idx_{tbl}_{col}` — harmless, but a composite index named
`idx_foo_bar` would collide with a column index on `bar` if someone names it
poorly. Validate at startup, log if collisions occur.

### Config

### 52. `AllowDestructiveSync` is documented as legacy but still in the config UI
Remove on 4.0 release. Bump instruction: if `AllowDestructiveSync = true`,
auto-upgrade to `SchemaSyncLevel = Full` on first load, then drop the field.

### 53. `MySqlDatabaseConfig` has no toggles for prepared statements or query-plan capture
Add `UsePreparedStatements` (bool, default true) and
`CaptureExplainOnSlowQuery` (bool, default true in dev).

### 54. `SlowQueryThresholdMs` is per-database but logged without a connection ID
Slow query log says "Slow query …" with no idea which DB. Add connectionId to
the log line and to `SlowQueryEvent` (already in the event — just the log
line misses it).

### Backup / migration

### 55. `BackupManager.CleanupOldBackupsAsync` is synchronous but wrapped in `await Task.CompletedTask`
Cosmetic: either make it sync or actually async via `File.Delete` on a
threadpool (`Task.Run`). Small.

### 56. `BackupDatabaseSchemaAsync` builds the entire SQL string in memory
Fine today, but for a large schema dump consider streaming to disk.

### 57. No rolling backup count cap
Today's cleanup is days-based only. Add `keepLatestN` option.

### 58. `MigrationTracker` creates its own table outside the normal sync path
It's a hand-written `CREATE TABLE IF NOT EXISTS`. Convert `__migrations` into
a proper entity record with `[Table]`/`[Column]` attributes, let the sync
service handle it.

### 59. No migration script runner
`MigrationTracker` only *records*. There's no way to declare a migration
script and have the library apply it. Either say "out of scope — use external
tools" or add a simple `IMigration` interface.

### Events / observability

### 60. `SlowQueryEvent.Query` field is the raw SQL with parameter placeholders
Fine for dashboards, but for `EXPLAIN` hook (#24) you need the parameter
values too. Add a `Parameters` dict.

### 61. `HealthChangedEvent` is declared but never published
Either wire it up in the start/stop and degraded-connection paths or delete.

### 62. No log output redaction for password fields
`_logger?.Debug` lines include connection strings and param values. Password-
shaped parameters should be masked. Low severity.

### Type converter

### 63. `TypeConverter.FromDbValue` falls back to returning the raw db value when `Convert.ChangeType` throws
Silent data corruption risk. Throw with a helpful message instead ("cannot
convert DB value 'foo' (string) to property X (int)"). The materializer
rewrite (#13) is the natural place.

### 64. `InferDataType` maps `Guid` to `DataType.Char` (no size)
Becomes `CHAR(1)` in the DDL builder's default path — wrong. Emit
`CHAR(36)` explicitly or store Guids as `BINARY(16)` (configurable).

### 65. `InferDataType` maps `string` to `VarChar` with no size
Becomes `VARCHAR(255)`. Fine default, but callers can't raise it without
adding `[Column(Size = …)]`. Accept a `DefaultStringSize` in config.

### Misc / dead code

### 66. `ManyToManyAttribute` is defined but no code uses it
Either implement nav-resolution for it or drop. Probably drop — joins (#7)
cover the use case.

### 67. `ForeignKeyAttribute` is used by sync but never by the query builder
The builder doesn't know about relationships, so cross-table joins still
require manual keys. Once joins (#7) land, have the builder peek at FK
metadata to auto-infer the ON clause.

### 68. `OperationType` enum is never used
Delete.

### 69. `AggregateType` / `AggregateFunction` are never used
Delete (the fluent builder uses method names, not enum dispatch).

### 70. `MySQL2Strings` has ~25 localized strings
Some are logged at debug only and don't need translation. Audit and keep
only user-visible ones (health-check labels, startup errors).

### 71. `MySQL2Library.Manifest.Version = "3.0.0"`
Should be driven from `.csproj` `<Version>`, not hardcoded — they can drift.

---

## Priority (suggested implementation order)

Top items with biggest impact on real-world app code:

1. **#13 + #30 + #31** — Compiled materializer + one shared `EntityMetadata<T>`. Free speedup on every read; unlocks projection/aggregation pipelines below.
2. **#1 + #4 + #5** — SQL-side aggregation + `CASE WHEN` + nested subquery `FROM`. Unblocks FragHunt dashboards.
3. **#2 + #34** — Projection pushdown that actually changes the return type to `TResult`. Cheap, helps every query.
4. **#9 + #40 + #42** — Fix `WithCache` keys (don't hash stale timestamps), publish cache hit/miss events. Current cache is silently broken.
5. **#16 + #17** — Composite + covering indexes. Schema-level; compounds everything above.
6. **#28 + #26** — Real bulk `InsertManyAsync` + bulk delete by predicate (#27). Background poll and purge jobs get faster by 10–100×.
7. **#35 + #36** — Quadratic param re-keying and repeated `Expression.Compile()` for closures. Small surface, outsized wins on every query.

Rest is incremental on top.

---

## Design principle

Across every change: **app code stays typed against record classes and
column attributes**. Expression trees are the public surface; raw SQL is
an implementation detail. If a feature requires consumers to hand-write
SQL strings, rethink it.
