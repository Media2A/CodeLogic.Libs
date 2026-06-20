# CL.MySQL2 — Changelog

All notable changes to **CodeLogic.MySQL2** are documented here. Versions follow
[Semantic Versioning](https://semver.org/). The version listed here matches the
NuGet package version of `CodeLogic.MySQL2`.

## [4.5.3] — 2026-06-20

### Added

- **Three schema sync modes — `SyncMode`.** A new operator-facing knob on each
  database (`config.mysql.json`) replaces the lower-level `SchemaSyncLevel` /
  `AllowDestructiveSync` flags (which still work for back-compat — `SyncMode` takes
  precedence and maps onto them via `EffectiveSyncLevel`).

  | Mode | Behaviour |
  |---|---|
  | `Developer` | Aggressive rolling updates — drops removed columns/indexes/FKs on every boot (maps to `Full`). |
  | `Production` *(default)* | Additive only — adds/modifies, **never drops**. A change that needs a drop is deferred and the table is flagged `DriftPending`. |
  | `Migration` | Deliberate one-shot destructive reconcile (takes a schema backup first). Idempotent — once every model matches and no drift is pending it does nothing and logs a warning to switch back to `Production`. |

  ```json
  { "Databases": { "Default": { "SyncMode": "Production" } } }
  ```

- **CRC sentinel — `__schema_state`.** Each model's desired schema is hashed
  (CRC) into a per-table row. Sync skips a table **entirely** — no
  `information_schema` diffing, no DDL — when the stored CRC matches the model
  *and* the table still exists. New `SyncResult` fields: `Skipped`, `SchemaCrc`,
  `DriftPending`; new `SchemaSyncStatus` enum (`Synced` / `DriftPending`).
  Exposed via `mysql.SchemaState` (a `SchemaStateStore`).

- **Cross-node schema-sync lock — `SchemaSyncLock`.** A schema/migration pass
  serializes across application nodes with MySQL `GET_LOCK`. The winner runs the
  DDL; peers wait, then find the schema already reconciled (matching CRCs) and do
  nothing.

- **Batch schema sync + runtime mode override.** `mysql.SyncSchemaAsync(params
  Type[])` reconciles a whole set of entities as one pass under a single lock,
  honouring the configured `SyncMode` and the CRC fast-path — the recommended
  startup entry point. `mysql.SetSyncMode(mode, connectionId)` overrides the mode
  at runtime (e.g. to flip `Migration` back to `Production` once a pass completes).

- **Imperative migrations.** `IMigration` / `MigrationVersion` / the abstract
  `Migration` base for data transforms, seeds, and semantic changes the
  declarative sync can't express. `IMigrationContext` provides `ExecuteAsync`,
  `QueryAsync<T>`, `ScalarAsync<T>`, and a `SyncTableAsync<T>()` bridge into
  declarative sync. The `MigrationRunner` applies pending migrations in
  `MigrationVersion` order over the `__migrations` table, each in its own
  transaction, under the shared lock, gated by the app version
  (`CodeLogicEnvironment.AppVersion`), and warns when an applied migration's
  checksum has drifted. Library surface: `RegisterMigration`,
  `RegisterMigrationsFrom(assembly)`, `MigrateAsync`, `GetPendingMigrationsAsync`.

  ```csharp
  public sealed class SeedRoles() : Migration("1.4.0", 1, "Seed default roles")
  {
      public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
          await ctx.ExecuteAsync("INSERT INTO roles (name) VALUES ('admin'), ('user')", ct: ct);
  }
  ```

  > MySQL implicitly commits on DDL, so a migration that mixes `ALTER` with data
  > changes is not atomic — keep `UpAsync` steps idempotent.

- **Rollback.** `mysql.RollbackAsync(MigrationVersion target)` runs `DownAsync`
  newest-first for every applied migration above `target`, each in its own
  transaction. It pre-flights the range and aborts cleanly **before any change**
  if a migration in range has no `DownAsync` override. Declaratively,
  `mysql.RestoreSchemaAsync(tableName)` replays a `BackupManager` schema snapshot
  (DDL only — rows are lost) and clears the table's `__schema_state` row so the
  next sync reconciles from scratch.

### Fixed

- **Upsert now portable to MariaDB.** `UpsertAsync`, `UpsertManyAsync`, and
  `UpsertWithIncrementsAsync` emit the portable `... ON DUPLICATE KEY UPDATE col =
  VALUES(col)` form, which works on **both** MySQL and MariaDB, instead of the
  MySQL-8.0.19+-only `INSERT ... AS new ... ON DUPLICATE KEY UPDATE` row-alias
  syntax that MariaDB rejected.

## [4.5.2] — 2026-06-13

### Added

- **Typed JOINs.** `Query<TLeft>().Join<TRight, TKey, TResult>(leftKey, rightKey,
  resultSelector, type)` translates a strongly-typed equi-join to real SQL with
  table aliases (left `t0`, right `t1`) and a compiled, reflection-free projection
  into `TResult` — only the columns the selector references are transferred.

  ```csharp
  var views = await mysql.Query<Order>()
      .Where(o => o.Total > 100)
      .Join<Customer, long, OrderView>(
          o => o.CustomerId,                 // left key
          c => c.Id,                         // right key
          (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name })
      .OrderByDescending((o, c) => o.Total)
      .Take(20)
      .ToListAsync();
  ```

  - **Join types:** `Inner` (default), `Left`, `Right`. `Cross` is rejected for a
    keyed join (keys imply an equi-join).
  - **Composite keys:** `o => new { o.A, o.B }` matched positionally with
    `c => new { c.X, c.Y }`.
  - **Carried filters:** `.Where(...)` calls made on the left builder *before*
    `.Join` are re-qualified to the left table and preserved.
  - **Fluent surface on the join:** `.Where((l, r) => …)`, `.OrderBy` /
    `.OrderByDescending((l, r) => …)`, `.Take` / `.Skip`, and the
    `ToListAsync` / `FirstOrDefaultAsync` / `CountAsync` terminals.
  - The single-table query path and the existing raw-string
    `Join(table, condition, type)` overload are unchanged.

- **Subquery filters — `EXISTS` / `IN`.** Four new WHERE-family methods on the
  query builder translate to real SQL subqueries:

  ```csharp
  // Correlated EXISTS — correlated + non-correlated conditions in one predicate
  mysql.Query<Order>()
      .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent");

  // IN (subquery) with an optional uncorrelated inner filter
  mysql.Query<Order>()
      .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip);
  ```

  - `WhereExists<TInner>` / `WhereNotExists<TInner>` →
    `[NOT] EXISTS (SELECT 1 FROM inner WHERE …)`, correlated via the predicate.
  - `WhereIn<TInner, TKey>` / `WhereNotIn<TInner, TKey>` →
    `col [NOT] IN (SELECT innerCol FROM inner [WHERE innerFilter])`.
  - Composes with ordinary `.Where(...)` and reuses the same multi-source
    translator as joins (each source qualified by its table name).

- **Column rename — `[Column(PreviousName = "old_col")]`.** Schema sync now emits
  `CHANGE COLUMN old_col new_col …` to rename in place and **preserve the data**,
  instead of the drop-old + add-new that silently lost it (orphan column at Safe;
  data loss at Full). Works at `Safe` and above; remove `PreviousName` once every
  environment has synced.

  ```csharp
  [Column(Name = "email_address", PreviousName = "email")]
  public string EmailAddress { get; set; } = "";
  ```

- **Multi-node cache coordination — `ICacheCoordinator`.** A pluggable coordination
  seam (same model as `ICacheStore`: interface + in-process default, distributed
  adapter supplied by the consumer) that closes the single-node limitation called
  out in 4.1.2's notes. Install with `QueryCache.UseCoordinator(...)`.

  - **Cross-node invalidation** — a local mutation now fans out via
    `PublishInvalidationAsync`; a peer's broadcast bumps this node's table-version
    counter and evicts matching entries (without re-broadcasting). Previously the
    version counter was per-process, so a mutation on one node never invalidated
    the others.
  - **Single-flight pool refresh** — `SmartCachePool` ticks now acquire a refresh
    lease via `TryAcquireRefreshLeaseAsync`; only the lease holder hits the DB, so
    N nodes don't all refresh the same pool. Idle-entry retirement still runs on
    every node. Pair with a shared `ICacheStore` (e.g. Redis) so non-leaders read
    the entry the leader writes.
  - The default `NullCacheCoordinator` is single-node: no fan-out, always grants
    the lease — behaviour is identical to before off-cluster.

- **Raw SQL escape hatch.** `mysql.SqlQueryAsync<T>(sql, parameters)` materializes
  rows into `T` with the same compiled materializer as the query builder;
  `ExecuteSqlAsync(sql, parameters)` runs a non-query and returns the affected count;
  `SqlScalarAsync<T>(sql, parameters)` returns a single value. All use named
  parameters, flow through observability, and inherit the transient-retry policy.

  ```csharp
  var rows = await mysql.SqlQueryAsync<UserRecord>(
      "SELECT * FROM users WHERE country = @c", new() { ["@c"] = "DK" });
  ```

- **Transient-error auto-retry.** Single non-transactional statements that fail with
  a deadlock (1213) or lock-wait timeout (1205) are retried with exponential backoff
  + jitter. Configurable per database via `TransientRetryCount` (default 3) and
  `TransientRetryBaseDelayMs` (default 50); 0 disables. Statements inside an explicit
  transaction scope are never auto-retried — the whole transaction is the caller's
  to retry.

- **Cache stampede protection.** Concurrent cache misses on the same cold key now
  collapse to a single factory execution (single-flight) instead of a thundering
  herd of identical DB queries. Transparent — no API change.

- **Soft deletes — `[SoftDelete(nameof(DeletedUtc))]`.** Marks a nullable-`DateTime`
  column as the delete marker. `Repository.DeleteAsync` then sets it to UtcNow
  instead of issuing a physical `DELETE`, and reads via `mysql.Query<T>()` and the
  repository getters automatically exclude rows where it is set. Opt back in with
  `.IncludeDeleted()` on a query, or purge for real with `Repository.HardDeleteAsync`.

  ```csharp
  [SoftDelete(nameof(DeletedUtc))]
  public class Account { /* … */ public DateTime? DeletedUtc { get; set; } }
  ```

### Notes

- **No breaking changes.** Joins and subquery filters are new methods; the
  multi-source WHERE translator is byte-identical to the single-table translator
  when no alias map is supplied.
- **Subquery-filtered queries are not cacheable** and cannot be turned into a
  typed `.Join` — same single-table-version-stamping limitation as joins. Both
  are gated explicitly (cache silently bypassed; `.Join` throws).
- **`WhereExists` against the outer query's own table is rejected** — unqualified
  inner columns would be ambiguous.
- **Soft-delete auto-filtering applies to single-table reads only** —
  `mysql.Query<T>()` terminals and the repository getters. It does NOT apply to
  joins, subqueries, or the query builder's bulk `UpdateAsync`/`DeleteAsync` (those
  stay raw so you can target or restore deleted rows). `QueryBuilder.DeleteAsync`
  is a hard delete regardless of `[SoftDelete]`.
- **Joins are not cacheable in this version.** The result cache stamps each entry
  with a single table's version counter, so a join entry could not be invalidated
  when the *other* joined table mutates. `.WithCache` / `.SmartCache` are
  intentionally absent on `JoinedQuery` rather than risk serving stale joins;
  multi-table invalidation is on the roadmap.
- **`TRight` must be specified explicitly** (e.g. `Join<Customer, long, OrderView>`)
  — it cannot be inferred from a lambda parameter type.

## [4.5.0] — 2026-05-24

### Added

- **`StorageType` enum on `ColumnAttribute`.** Per-column physical storage
  override that takes precedence over `DataType` for DDL generation.
  Available values: `Binary`, `VarBinary`, `TinyBlob`, `Blob`, `MediumBlob`,
  `LongBlob`. When set, the column is stored as the chosen binary type and
  values are automatically converted to/from binary on read and write.

- **Guid-as-BINARY(16) support.** Set `StorageType = StorageType.Binary` on a
  `Guid` property and CL.MySQL2 stores it as `BINARY(16)` using RFC 4122
  big-endian byte layout for correct lexicographic sort order. Conversion is
  automatic in all paths: insert, update, read, WHERE clauses, and IN queries.

  ```csharp
  [Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
  public Guid Id { get; set; }
  ```

- **Automatic binary conversion for all CLR types.** Any property can be
  stored as binary by setting `StorageType`. Supported types and their binary
  sizes (auto-detected when `Size` is not explicit):

  | CLR type | Binary size | Byte order |
  |---|---|---|
  | `Guid` | 16 | RFC 4122 big-endian |
  | `long` / `ulong` | 8 | big-endian |
  | `int` / `uint` | 4 | big-endian |
  | `short` / `ushort` | 2 | big-endian |
  | `double` | 8 | big-endian |
  | `float` | 4 | big-endian |
  | `decimal` | 16 | big-endian |
  | `DateTime` / `DateTimeOffset` | 8 | ticks, big-endian |
  | `bool` / `byte` / `sbyte` | 1 | — |
  | `string` | explicit | UTF-8 |
  | `byte[]` | passthrough | as-is |

- **LINQ WHERE support for binary-stored columns.** Expressions like
  `repo.Where(x => x.Id == someGuid)` and `list.Contains(x.Id)` correctly
  convert parameter values to binary when the column uses `StorageType`.

- **`SequentialGuid.NewId()` helper.** Generates time-ordered UUIDv7 values
  optimized for `BINARY(16)` primary keys. Sequential inserts append to the
  B-tree instead of causing random page splits — dramatically reducing index
  fragmentation compared to random UUIDv4.

  ```csharp
  [Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
  public Guid Id { get; set; } = SequentialGuid.NewId();
  ```

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt`. AssemblyVersion is derived automatically.

### Notes

- **No breaking changes.** `StorageType` defaults to `StorageType.Default`
  (the zero-value), so all existing entities and schemas are unaffected.
  Guid inference still returns `Char(36)` unless you explicitly opt in.

## [4.2.3] — 2026-05-15

### Fixed

- **Cache orphan accumulation on mutations.** `QueryCache.Invalidate(tableName)`
  previously only bumped the per-table version counter — old cache entries
  (now unreachable via the read path because the cache key changed) lingered
  in the underlying store until TTL or LRU swept them. On a busy app this
  produced unbounded memory growth. Invalidate now also calls
  `ICacheStore.EvictByTableAsync` to sweep matching entries in the same step.
- **SmartCachePool orphan tracking.** Each pool entry now remembers the
  cache key it last wrote. If the next tick computes a different key
  (because a mutation bumped the table version between ticks), the
  previous key is evicted explicitly. Works on any `ICacheStore`
  implementation including ones that can't enumerate (Redis without
  SCAN, memcached).

### Added

- `ICacheStore.EvictByTableAsync(tableName)` — bulk eviction by table.
  Default in-process implementation is an O(n) scan over current entries.
  Distributed adapters can override (e.g. Redis tag-set or key prefix).
- `ICacheStore.CountByTable()` — entries grouped by tableName for diagnostics.
- `QueryCache.GetStats()` → `QueryCacheStats(TotalEntries, EntriesByTable,
  TableVersions)`. Surfaced on the library API via `MySQL2Library.GetCacheStats()`
  so admin tools can render "what's in the cache right now" without
  dumping values.

## [4.2.2] — 2026-05-15

### Fixed

- **SmartCache pool no longer corrupts `ToListAsync` results.** The pool's
  refresh factory stored the unwrapped `List<T>` instead of the
  `Result<List<T>>` that the cache-aside read path expects. After the first
  background refresh tick, every subsequent read failed the `(Result<List<T>>)`
  cast inside `GetOrSetAsync`, the outer try/catch turned it into a Failure
  Result, and callers saw an empty list (manifested as "No servers configured"
  / empty leaderboards roughly one refresh interval after warm-up). `FirstOrDefaultAsync`
  and `CountAsync` already cached the full Result and were unaffected; only
  `ToListAsync` was wrong.

## [4.2.1] — 2026-05-15

### Fixed

- **Failure Results no longer poison the cache.** Previously, a query that
  failed (e.g. transient connection error during a cold warm-up) had its
  `Result<T>.Failure` value cached just like a successful one — subsequent
  reads served the failure until the entry's TTL expired or a pool refresh
  overwrote it. Now `QueryCache.GetOrSetAsync` skips writing failure Results,
  evicts any pre-existing failure entry on read, and `SetDirectAsync` (the
  smart-cache pool's refresh path) refuses to write failures too. Empty
  server lists / leaderboards on first request after a deploy are gone.

## [4.2.0] — 2026-05-15

### Added

- **Smart-cache pool warm-up on registration.** `RegisterCachePool` now
  accepts an optional `warmUp: Func<Task>` callback that fires as a
  fire-and-forget task right after the pool starts. The callback just
  calls the queries that should be warm — they auto-register with the
  pool via their normal `.SmartCache(name)` decoration — so the cache
  is hot before the first user request hits it. Exceptions are caught
  and logged; the pool stays lazy if warm-up fails.
- `SmartCachePool.WarmUp(Func<Task>)` — public method exposing the same
  behaviour for callers that want to warm a pool independently of
  registration.

## [4.1.2] — 2026-05-15

### Added

- **Smart cache pools** — named groups of cached queries kept warm by a
  background timer (`mysql.RegisterCachePool("dashboard", refreshEvery: 30s)`,
  opt in per-query with `.SmartCache("dashboard")`). Reads after the first
  populate the cache never block on the DB — the pool's timer re-runs every
  registered query in the background and overwrites the entry.
- `SmartCachePool.RefreshNowAsync()` — out-of-schedule refresh, useful right
  after a deploy to prime the cache before the first user hits the page.
- `MySQL2Library.GetCachePoolStats()` — diagnostic snapshot per pool
  (entry count, ticks fired, ticks failed, last tick UTC).
- `QueryCache.SetDirectAsync(...)` — internal cache write API used by pools.

### Notes

- Smart cache is mutually exclusive with `.WithCache(TimeSpan)` — if both are
  set, the pool wins and the TTL comes from `refreshEvery * 2`.
- Unknown pool name on `.SmartCache(name)` logs a warning and falls back to
  non-cached execution (no exception).
- Per-pool eviction policy: an entry that has not been read for
  `MaxIdleFires` (default 3) consecutive ticks is dropped from the refresh
  list. Bounds cardinality on parameterized queries.
- Smart cache is disabled inside a transaction scope (same as `.WithCache`).
- Single-node only in v4.2. Multi-node coordination is on the roadmap.

## [4.1.1] — 2026-04-17

### Fixed

- Qualify LHS columns in upsert SET clauses so `UpsertAsync` no longer
  generates ambiguous column references when the table has columns whose
  names clash with parameter placeholders.

## [4.1.0] — 2026-04-17

### Added

- **Typed upsert** — `UpsertAsync` + `UpsertWithIncrementsAsync` on the
  repository. Compiles to `INSERT ... ON DUPLICATE KEY UPDATE ...` with
  full LINQ-shaped value/increment expressions on the update side.

### Changed

- `LibraryManifest.Version` now reads from the assembly's `AssemblyVersion`
  attribute at runtime instead of being a hard-coded string. Keeps the
  manifest honest across rebuilds.

## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh across every CodeLogic library for the v4 baseline.
- No functional changes vs 4.0.3.

## [4.0.1] — 2026-04-09

### Fixed

- Drop the `Expression.Compile().DynamicInvoke()` fast-path inside the SQL
  expression visitor — it broke on closures over generic types. The visitor
  now always walks the tree.

## [4.0.0] — 2026-04-09

Major rewrite. Breaking.

### Added

- **Projection pushdown** — `.Select<TResult>(x => new { ... })` emits a
  real `SELECT col1, col2, ...` column list instead of `SELECT *`. Combined
  with compiled materializers this often cuts row-transfer bandwidth by 80%+.
- **SQL-side aggregation** — `.GroupBy(...).Select(g => new { g.Key,
  g.Sum(...), g.Average(...), ... })` translates to real `GROUP BY` +
  aggregate functions. No client-side row materialization.
- **`SqlFn` helpers** — server-side function markers (`SqlFn.DayOfWeek`,
  `SqlFn.Hour`, `SqlFn.BucketUtc`, `SqlFn.Coalesce`, `SqlFn.Round`, etc.)
  recognized by the translator — mirrors EF's `EF.Functions` pattern.
- **`[Index]` attribute** — declare named, unique, and covering indexes
  (with `Include = new[] { ... }`) at the column level.
- **`[RetainDays]` attribute** — opt entities into a daily background purge
  worker that runs batched `DELETE` until drained.
- **Working result cache** — `.WithCache(TimeSpan)` with two correctness
  fixes from prior versions:
  - DateTime closures near `UtcNow` are time-quantized to a configurable
    window (default 60s) so `.Where(x => x.At >= UtcNow.AddDays(-30))`
    stops producing a unique cache key per call.
  - Mutations bump a per-table version that participates in the cache
    key — invalidation is free (old keys become un-hittable, no eviction
    loop).
- **`EntityMetadata<T>` + compiled `Materializer<T>`** — reflection runs
  once per entity at first use; subsequent reads use a compiled
  reader-to-entity function.
- **Observability events** — `QueryExecutedEvent`, `SlowQueryEvent`,
  `CacheHitEvent`, `CacheMissEvent`, `N1QueryDetectedEvent`,
  `TableSyncedEvent` publish to the CodeLogic event bus.
- **`MaxBatchInsertSize`, `MaxInClauseValues`, `PreparedStatementCacheSize`,
  `N1DetectorThreshold`, `CaptureExplainOnSlowQuery`, `DefaultStringSize`,
  `CacheEnabledOverride`** — per-database config knobs.
- **`CacheConfiguration`** — global cache settings (`Enabled`,
  `MaxEntries`, `DefaultTtlSeconds`, `TimeQuantizeSeconds`,
  `PublishEvents`).

### Changed

- Republished as v4.0.0 to reset the version line with the new package shape.
- All public APIs refreshed under the v4 baseline.

## Earlier releases

Pre-4.0 history is retained in the
[git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.MySQL2)
but is not documented in detail here — the library shape changed
significantly in the v4 rewrite.
