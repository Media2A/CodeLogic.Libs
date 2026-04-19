# CL.MySQL2 — New API Shape

Target shape for the fast-query rewrite. Written *before* touching code so the
API surface is agreed up front.

Everything here is strongly typed against entity records and column attributes.
No raw SQL in consumer code.

---

## Core types

### `IQuery<T>` — the fluent chain

Replaces today's `QueryBuilder<T>`. Interface so future backends can plug in.

```csharp
public interface IQuery<T>
{
    IQuery<T> Where(Expression<Func<T, bool>> predicate);
    IQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector);
    IQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector);
    IQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector);
    IQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector);
    IQuery<T> Take(int count);
    IQuery<T> Skip(int count);
    IQuery<T> WithCache(TimeSpan ttl);

    // Projection (changes T to TResult, emits real column list)
    IQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);

    // Grouping (returns grouped query, terminal Select collapses it)
    IGroupedQuery<TKey, T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);

    // Joins
    IQuery<TResult> Join<TInner, TKey, TResult>(
        IQuery<TInner> inner,
        Expression<Func<T, TKey>> outerKey,
        Expression<Func<TInner, TKey>> innerKey,
        Expression<Func<T, TInner, TResult>> result);

    // Terminals
    Task<Result<List<T>>>                ToListAsync(CancellationToken ct = default);
    IAsyncEnumerable<T>                  AsAsyncEnumerable(CancellationToken ct = default);
    Task<Result<T?>>                     FirstOrDefaultAsync(CancellationToken ct = default);
    Task<Result<T>>                      SingleAsync(CancellationToken ct = default);
    Task<Result<bool>>                   AnyAsync(CancellationToken ct = default);
    Task<Result<long>>                   CountAsync(CancellationToken ct = default);
    Task<Result<PagedResult<T>>>         ToPagedListAsync(int page, int pageSize, CancellationToken ct = default);

    // Terminal scalar aggregates (on the already-projected T if T is scalar-ish)
    Task<Result<TR>>                     SumAsync<TR>(Expression<Func<T, TR>> selector, CancellationToken ct = default);
    Task<Result<TR>>                     MaxAsync<TR>(Expression<Func<T, TR>> selector, CancellationToken ct = default);
    Task<Result<TR>>                     MinAsync<TR>(Expression<Func<T, TR>> selector, CancellationToken ct = default);
    Task<Result<double>>                 AverageAsync<TR>(Expression<Func<T, TR>> selector, CancellationToken ct = default);

    // Bulk mutations on the predicate set
    Task<Result<int>>                    DeleteAsync(CancellationToken ct = default);
    Task<Result<int>>                    UpdateAsync(Expression<Func<T, T>> setExpr, CancellationToken ct = default);
}
```

### `IGroupedQuery<TKey, T>`

The grouped query is only useful if you call `.Select` to collapse groups back
into a shaped result set. No public terminals — the only exit is `Select`.

```csharp
public interface IGroupedQuery<TKey, T>
{
    IQuery<TResult> Select<TResult>(
        Expression<Func<IGrouping<TKey, T>, TResult>> projection);
}
```

Inside the projection lambda, the visitor recognizes:

| Expression inside `.Select` | SQL |
|---|---|
| `g.Key` | `GROUP BY` key column(s) — also selected |
| `g.Key.MyProp` (for anonymous composite keys) | individual grouping column |
| `g.Sum(x => x.Foo)` | `SUM(foo)` |
| `g.Average(x => x.Foo)` | `AVG(foo)` |
| `g.Min(x => x.Foo)` | `MIN(foo)` |
| `g.Max(x => x.Foo)` | `MAX(foo)` |
| `g.Count()` | `COUNT(*)` |
| `g.Count(x => x.Foo > 3)` | `SUM(CASE WHEN foo > 3 THEN 1 ELSE 0 END)` |
| `g.Any()` / `g.Any(pred)` | same as `g.Count(…) > 0` |

### `SqlFn` — SQL function helpers

Static class whose methods throw if called outside a query expression. Same
pattern as `EF.Functions`. All signatures type-check at compile time.

```csharp
public static class SqlFn
{
    // Date/time
    public static int Year(DateTime d);
    public static int Month(DateTime d);
    public static int Day(DateTime d);
    public static int Hour(DateTime d);
    public static int Minute(DateTime d);
    public static int DayOfWeek(DateTime d);  // 0 = Sunday (matches .NET)
    public static DateTime Date(DateTime d);  // strips time component
    public static DateTime BucketUtc(DateTime d, int seconds); // FLOOR(UNIX_TIMESTAMP(..)/N)*N

    // Conditional / null
    public static T Coalesce<T>(params T[] values);
    public static T IfNull<T>(T value, T fallback);

    // String
    public static string Lower(string s);
    public static string Upper(string s);
    public static string Concat(params string[] parts);
    public static bool Like(string s, string pattern);

    // Math
    public static double Round(double v, int digits);
    public static double Floor(double v);
    public static double Ceiling(double v);
}
```

Example FragHunt usage:

```csharp
var cells = await mysql.Query<FragHuntServersSnapshotRecord>()
    .Where(s => s.SnapshotUtc >= since)
    .GroupBy(s => new
    {
        Dow  = SqlFn.DayOfWeek(s.SnapshotUtc),
        Hour = SqlFn.Hour(s.SnapshotUtc),
    })
    .Select(g => new HeatmapCell(
        g.Key.Dow,
        g.Key.Hour,
        g.Average(x => (double)x.PlayerCount)))
    .OrderBy(c => c.DayOfWeek)
    .ThenBy(c => c.HourOfDay)
    .ToListAsync();
```

---

## New attributes

### `[Index]` on a property (replaces `Column(Index = true)`)

Existing `Column.Index = true` stays for back-compat during the FragHunt
migration. New form gives more control and is discoverable:

```csharp
[Index(Name = "ix_snapshot_utc", Include = new[] { "PlayerCount", "IsOnline" })]
public DateTime SnapshotUtc { get; set; }
```

`Include` adds the listed columns as "covering" leaves — emitted as a composite
index where the key is the annotated column and the rest are trailing columns
in the index for covering reads (index-only scan).

### `[CompositeIndex]` (already exists — documented, unchanged)

### `[RetainDays(N)]` on a class

```csharp
[Table(Name = "fraghunt_servers_snapshot")]
[RetainDays(90, TimestampColumn = nameof(FragHuntServersSnapshotRecord.SnapshotUtc))]
public sealed class FragHuntServersSnapshotRecord { ... }
```

CL.MySQL2 runs a background purge worker that deletes rows where
`{TimestampColumn} < NOW() - INTERVAL N DAY`. Replaces manual
`PurgeOldHistoryAsync`.

### `[Countable]` on a class

```csharp
[Table(...), Countable]
public sealed class FragHuntServersSnapshotRecord { ... }
```

Enables an O(1) cached row count, kept in sync via Insert/Delete hooks.
Opt-in because it costs write-path work.

---

## Cache contract

### `ICacheStore`

```csharp
public interface ICacheStore
{
    Task<(bool Found, object? Value)> TryGetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, object value, TimeSpan ttl, CancellationToken ct);
    Task EvictAsync(string key, CancellationToken ct);
    Task EvictByPrefixAsync(string prefix, CancellationToken ct);
}
```

Default implementation: current in-process `QueryCache` (reworked behind the
interface). Redis adapter can live in a separate package.

### Cache key

`"{connectionId}|{tableVersion:long}|sha256({sql}|{sorted-params})"`

- `tableVersion` is incremented atomically on any Insert/Update/Delete to the
  table. Replaces today's "invalidate the whole table" eviction with a free,
  stable, read-path-only invalidation (old keys just become un-hittable).
- Observability: `Library.OnCache(cacheHit | cacheMiss)` event fired with
  `{key, tableName, sql}` so apps can log.

---

## Materialization

Reflection-based `MapReader` is replaced with a compiled materializer:

```csharp
internal static class Materializer<T>
{
    public static readonly Func<MySqlDataReader, T> Compile = BuildCompiled();
}
```

Built once per `T` via `Expression.Lambda(...).Compile()`. Maps by column
ordinal resolved against `ColumnAttribute`. Zero reflection on the hot path.
Extended to support:

- Anonymous types (for `Select` projections)
- `readonly record struct` targets (no heap alloc)
- Positional record types (uses ctor order)
- `Nullable<T>` fields with ordinal check

The same materializer is used for aggregate projections and for regular
`ToListAsync` — one code path.

---

## Streaming

```csharp
IAsyncEnumerable<T> AsAsyncEnumerable(CancellationToken ct = default);
```

Yields rows as they arrive. Opens and holds the reader for the enumerator's
lifetime, releases on dispose. For exports / migrations where a `List<T>`
would OOM.

---

## Bulk writes

On `Repository<T>`:

```csharp
Task<Result<int>> InsertManyAsync(IEnumerable<T> entities, int batchSize = 500, CancellationToken ct = default);
Task<Result<int>> UpsertManyAsync(IEnumerable<T> entities, string[] matchColumns, int batchSize = 500, CancellationToken ct = default);
```

`InsertManyAsync` emits real batched `INSERT ... VALUES (...), (...), ...`
per `batchSize` chunk. `UpsertManyAsync` uses
`INSERT ... ON DUPLICATE KEY UPDATE`. Both invalidate cache once at the end,
not per row.

On `IQuery<T>`:

```csharp
Task<Result<int>> UpdateAsync(Expression<Func<T, T>> setExpr, CancellationToken ct = default);
Task<Result<int>> DeleteAsync(CancellationToken ct = default);
```

Single SQL statement for the whole predicate set. Replaces fetch-then-delete-
per-row patterns like the current `PurgeOldHistoryAsync`.

---

## Observability

- `SlowQueryEvent` — existing, unchanged.
- `QueryExecutedEvent(sql, elapsedMs, rowCount, cacheHit, connectionId)` —
  always fires. Apps subscribe for dashboards.
- On slow fire, automatically run `EXPLAIN FORMAT=JSON` and attach to the
  event. One-time cost only when the query is already slow.
- `N+1 detector` — per-request scope (ambient `AsyncLocal<QueryContext>`),
  counts identical SQL templates with different parameters. Logs a warning
  above a threshold. Off by default; on in development.

---

## Design rules

1. **No raw SQL on the public surface.** If a consumer would need to write a
   string of SQL, we've failed — fix the translator or add a `SqlFn`.
2. **Strongly typed or don't ship.** Every public method is generic or typed
   against `T` / `TKey` / `TResult`. No `object` in signatures except the
   cache's low-level `ICacheStore`.
3. **One way to do each thing.** Scalar aggregates are terminals
   (`SumAsync(selector)`); grouped aggregates are inside
   `GroupBy(...).Select(g => ...)`. No overlap.
4. **Slow is loud.** Cache misses log in dev, slow queries log always, N+1
   patterns log on detection.
5. **Materializer is the bottom layer.** Compiled once per `T`, used by every
   path that maps a row.
6. **Everything tested against the real FragHunt benchmark.** Homepage widget
   cold < 200ms is the ship gate.

---

## FragHunt changes implied

Minimal — the point of the design is that existing FragHunt code compiles with
near-zero edits. The three slow methods become one-liners on the query:

```csharp
public async Task<GlobalDashboardStats> GetGlobalDashboardStatsAsync()
{
    var servers = await GetAllServersAsync();
    var now = DateTime.UtcNow;
    var since24h = now.AddHours(-24);
    var since30d = now.AddDays(-30);

    var peak24h = await Snapshots.Query()
        .Where(s => s.SnapshotUtc >= since24h)
        .GroupBy(s => s.SnapshotUtc)
        .Select(g => g.Sum(x => x.PlayerCount))
        .MaxAsync();

    var avgUptime = await Snapshots.Query()
        .Where(s => s.SnapshotUtc >= since30d)
        .GroupBy(s => s.ServerId)
        .Select(g => g.Average(x => x.IsOnline ? 100.0 : 0.0))
        .AverageAsync();

    return new GlobalDashboardStats(
        servers.Count(s => s.IsOnline), servers.Count,
        servers.Sum(s => s.PlayerCount),
        peak24h.Value, Math.Round(avgUptime.Value, 1));
}
```

Retention becomes:

```csharp
[Table(Name = "fraghunt_servers_snapshot")]
[RetainDays(90, TimestampColumn = nameof(SnapshotUtc))]
public sealed class FragHuntServersSnapshotRecord { ... }
```

— and the manual `PurgeOldHistoryAsync` disappears.

---

## Cache configuration

### New global config section — `CacheConfiguration`

Lives in `DatabaseConfiguration` alongside `Databases`. Controls the process-
wide cache store. All values are hot-reloadable — changes apply on next read.

```csharp
public sealed class CacheConfiguration : ConfigModelBase
{
    [ConfigField(Label = "Enabled", ...)]
    public bool Enabled { get; set; } = true;

    [ConfigField(Label = "Max Entries", Min = 0, ...)]
    public int MaxEntries { get; set; } = 10_000;

    [ConfigField(Label = "Max Memory (MB)", Min = 0, ...)]
    public int MaxMemoryMb { get; set; } = 256;

    [ConfigField(Label = "Default TTL (seconds)", Min = 1, ...)]
    public int DefaultTtlSeconds { get; set; } = 60;

    [ConfigField(Label = "Time-Quantize TTL (seconds)", Min = 0, ...)]
    /// <summary>
    /// Captured closure values of type DateTime near "now" are rounded to this
    /// window when forming the cache key. Value 0 disables quantization.
    /// Fixes the "every call is a cache miss because DateTime.UtcNow.AddDays(-30)
    /// is different every invocation" pattern.
    /// </summary>
    public int TimeQuantizeSeconds { get; set; } = 60;

    [ConfigField(Label = "Publish Cache Events", ...)]
    public bool PublishEvents { get; set; } = true;
}
```

### Per-database overrides — added to `MySqlDatabaseConfig`

```csharp
[ConfigField(Label = "Cache Enabled (override)", ...)]
public bool? CacheEnabledOverride { get; set; } = null;  // null = inherit global

[ConfigField(Label = "Query Timeout (ms)", Min = 0, ...)]
public int QueryTimeoutMs { get; set; } = 30_000;

[ConfigField(Label = "Max Batch Insert Size", Min = 1, Max = 10_000, ...)]
public int MaxBatchInsertSize { get; set; } = 500;

[ConfigField(Label = "Max IN-Clause Values", Min = 1, Max = 65_000, ...)]
public int MaxInClauseValues { get; set; } = 1_000;

[ConfigField(Label = "Prepared Statement Cache Size", Min = 0, ...)]
public int PreparedStatementCacheSize { get; set; } = 256;

[ConfigField(Label = "N+1 Detection Threshold", Min = 0, ...)]
/// <summary>0 disables detection. Non-zero: warn if the same query template
/// fires this many times within one request scope.</summary>
public int N1DetectorThreshold { get; set; } = 0;

[ConfigField(Label = "Capture EXPLAIN On Slow Queries", ...)]
public bool CaptureExplainOnSlowQuery { get; set; } = true;

[ConfigField(Label = "Default String Size", Min = 1, Max = 65_535, ...)]
/// <summary>Default VARCHAR length when a string column has no [Column(Size=...)].</summary>
public int DefaultStringSize { get; set; } = 255;
```

### Per-query overrides on `IQuery<T>`

```csharp
IQuery<T> WithCache(TimeSpan ttl);
IQuery<T> WithCache(TimeSpan ttl, CacheKeyStrategy strategy);
IQuery<T> WithTimeout(TimeSpan timeout);
IQuery<T> AsNoTracking();      // skip materializer pipeline — raw reader
IQuery<T> WithExplain();       // attach EXPLAIN plan to QueryExecutedEvent
IQuery<T> WithConnection(string connectionId);
```

```csharp
public enum CacheKeyStrategy
{
    /// <summary>Hash SQL + all parameter values verbatim. Use for queries
    /// whose parameters are stable across calls.</summary>
    Exact,

    /// <summary>
    /// Default. Hash SQL + parameter values, but quantize DateTime parameters
    /// derived from UtcNow/Now to the nearest TimeQuantizeSeconds window.
    /// Makes "since = UtcNow.AddDays(-30)" share a cache key within a minute.
    /// </summary>
    QuantizeTime,

    /// <summary>Hash SQL only. Parameters ignored. Caller promises same shape
    /// = same result (rare — dashboard-style fixed-query widgets).</summary>
    SqlOnly,
}
```

---

## Updates from full-codebase review (since NEWSHAPE v1)

These don't change the public API shape above, but they do tighten it:

### Consolidate reflection into `EntityMetadata<T>`

One internal class owns all reflected info per entity type, populated once:

```csharp
internal static class EntityMetadata<T>
{
    public static readonly string TableName;
    public static readonly IReadOnlyList<ColumnMetadata> Columns;
    public static readonly IReadOnlyDictionary<string, ColumnMetadata> ColumnsByCol;   // by column name
    public static readonly IReadOnlyDictionary<string, ColumnMetadata> ColumnsByProp;  // by property name
    public static readonly ColumnMetadata? PrimaryKey;
    public static readonly IReadOnlyList<CompositeIndexAttribute> CompositeIndexes;
    public static readonly Func<MySqlDataReader, T> CompiledMaterializer;
}
```

Replaces the three private caches in `Repository<T>`, `QueryBuilder<T>`,
`SchemaAnalyzer`. Zero reflection on the hot path after first use.

### Compiled materializer supports projections too

Not just `T`. Projections from `IQuery<T>.Select<TResult>(...)` compile a
second `Func<MySqlDataReader, TResult>` specific to that projection's column
ordering. Cached by the expression tree's hash.

### Fix `TypeConverter.FromDbValue` silent fallthrough

Today it returns the raw DB value if conversion throws — silent type
corruption. Throw with a helpful error message: "cannot convert column X
from MySQL `{dbType}` to CLR `{targetType}`".

### Expression visitor — closure reads without `Lambda.Compile`

Today: `Expression.Lambda(node).Compile().DynamicInvoke()` per closure
member — ~100µs each, delegate allocation. Replace with direct reads:

- `ConstantExpression.Value`
- `MemberExpression` over a `ConstantExpression` → `FieldInfo.GetValue` /
  `PropertyInfo.GetValue`
- Nested member chains recurse the same way

Fallback to Lambda.Compile only for genuinely dynamic cases. Expect
measurable wins on queries with several captured variables.

### Param re-keying removed

Visitor emits final `@qb_{n}` parameter names on the first pass. Builder
passes its counter to the visitor, gets back a dict already using it. No
more post-hoc `string.Replace`.

### Delete unused code (confirmed no callers)

- `Models/QueryModels.cs`: `WhereCondition`, `OrderByClause`, `JoinClause`,
  `AggregateFunction`, `AggregateType` — all unused by the fluent builder
- `Models/DataTypes.cs`: `OperationType` enum — unused
- `Models/Attributes.cs`: `ManyToManyAttribute` — unused, joins (#7) cover it
- Legacy `MySqlDatabaseConfig.AllowDestructiveSync` — auto-migrate on load
  to `SchemaSyncLevel.Full`, then remove the field
- Redundant `QueryBuilder.Join(string, string, JoinType)` — kept only the
  typed join form

### One fix surfaced by the review — connection ID tracking

`ConnectionManager.CloseConnectionAsync` currently linear-scans configs to
guess which connection ID a closing connection belongs to by matching the
connection string. Replaced with an owning `TrackedConnection` record that
carries `connectionId` directly. Fixes #43/#44.

### Migration tracker uses the normal entity path

`__migrations` becomes a proper `[Table] MigrationRecord` entity synced
through `TableSyncService` like everything else. Removes the hand-written
`CREATE TABLE IF NOT EXISTS` in `MigrationTracker`.

### Slow query log includes connectionId

`_logger?.Warning($"[MySQL2] [{connId}] Slow query ({ms}ms): {sql}")` — so
you can tell which DB is slow when multi-DB.

---

## Resolved open questions

1. **Version bump** → **v4.0.0**. Confirmed: breaking API change, clean
   rewrite, no back-compat shim. FragHunt migrates in the same PR.
2. **Result<T> vs exceptions** → **keep `Result<T>` everywhere**. Consistent
   with the rest of CodeLogic; callers already handle `.IsSuccess`.
3. **Back-compat shim** → **no**. Full break; benchmark is the gate.
4. **Cache store default** → **in-process**, interface-ready (`ICacheStore`)
   so a Redis/memcached adapter can be dropped in later as a separate
   package. Not part of v4.0.
5. **Cache key strategy** → **QuantizeTime by default** (60s window matches
   default TTL). Fixes the "every call is unique" bug without consumers
   thinking about it. `Exact` and `SqlOnly` available as opt-in.
6. **Cache config** → confirmed. Two new knob groups: global
   `CacheConfiguration` section + per-database overrides on
   `MySqlDatabaseConfig`. Generated on startup by CodeLogic's config system
   (`[ConfigSection]` + `[ConfigField]` attributes).
