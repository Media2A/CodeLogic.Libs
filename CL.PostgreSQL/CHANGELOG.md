# CL.PostgreSQL — Changelog

All notable changes to **CodeLogic.PostgreSQL** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## 2026-09-13

### Changed

- **Rebuilt on the `CL.MySQL2` architecture.** The library's internals were replaced with a
  dialect-swapped port of `CL.MySQL2`, the same way `CL.MSSQL` was built, taking the source
  tree from 17 files to 45 and bringing the three database libraries onto one codebase shape.

### Added

- Query cache with table-version invalidation, named smart-cache pools with background
  refresh, and a pluggable cache store / coordinator for multi-node deployments.
- Database-backed migrations: `IMigration`, `MigrationRunner`, `IMigrationContext`,
  version-ordered plans, and rollback. Migration history now lives in a table rather than a
  local JSON file, so instances of the same application no longer each keep their own copy
  and re-run everything.
- CRC-gated schema state tracking plus a `pg_advisory_lock`-based sync lock, so several
  instances starting at once no longer race on DDL.
- Joins, typed projections, grouping, cursor (keyset) pagination, `WhereIn`, `WhereNotIn`,
  `WhereExists` and `WhereNotExists`.
- `ON CONFLICT` upserts (`UpsertAsync`, `UpsertManyAsync`, `UpsertWithIncrementsAsync`),
  soft delete, a retention worker, and query observability events.
- `EntityMetadata<T>` with compiled property accessors and a compiled row materializer,
  replacing the previous per-row reflection -- the old mapper ran a linear property scan
  with a `GetCustomAttribute` call for every column of every row.
- PostgreSQL-native type support: `uuid`, `timestamptz`, `jsonb`, arrays, ranges, `inet`,
  `macaddr`, identity columns, and `INCLUDE` covering indexes.

### Security

- **Fixed SQL injection through unvalidated column names.** `GetByColumnAsync`,
  `GetPagedAsync(orderByColumn)` and the dictionary overload of `QueryBuilder.UpdateAsync`
  interpolated caller-supplied strings directly into SQL. All string-typed column APIs now
  resolve through an `EntityMetadata<T>` allow-list, and identifiers are rendered through
  `PostgreSqlDialect.Quote` and validated where they enter the metadata.
- **Fixed connection-string injection.** Connection strings were assembled by string
  concatenation, so a `;` in a password or database name could append arbitrary connection
  options. They are now built with `NpgsqlConnectionStringBuilder`.
- **`AllowDestructiveSync` is no longer a dead setting.** It was declared, surfaced in the
  configuration UI as a guard against `DROP` during schema sync, and never read anywhere in
  the library. Destructive DDL is now gated by `SyncMode` / `SchemaSyncLevel`, with
  `AllowDestructiveSync` honoured for backwards compatibility.
- LIKE metacharacters in user-supplied values are escaped, so a `%` in a search term no
  longer silently changes the result set.
- Backup filenames are sanitised rather than interpolated from schema and table names.

### Fixed

- **Schema sync no longer rewrites every table on every startup.** The analyzer compared
  `information_schema.data_type` against the generated DDL with a lowercase string compare.
  Those vocabularies never match -- PostgreSQL reports `character varying`, `numeric`,
  `timestamp with time zone`; the generator emitted `VARCHAR(255)`, `NUMERIC(10,2)`,
  `TIMESTAMPTZ` -- so every string, decimal, timestamp, time and array column was issued an
  `ALTER COLUMN ... TYPE` on each sync, taking an `ACCESS EXCLUSIVE` lock and rewriting the
  table. Types are now canonicalised through an alias table before comparison.
- Unique columns produced two unique constraints: one inline in the column definition and
  one as a separate named constraint.
- DDL scripts were split on bare `;`, which broke any statement containing a semicolon in a
  default or comment, and each fragment ran on its own connection so a table and its indexes
  were not created atomically.
- `Contains()` over an empty collection emitted `IN ()`, a syntax error. It now emits a
  false literal.
- `ToPagedListAsync` returned the first group's row count instead of the number of groups
  when combined with `GroupBy`.
- `InsertManyAsync` issued one round trip per row and was not transactional, so a failure
  part-way through left earlier rows committed. Inserts are now batched and bounded by
  PostgreSQL's 65535-parameter statement limit.
- Bitwise `&` and `|` in a predicate were translated to logical `AND` / `OR`, silently
  corrupting integer bitmask comparisons.
- The retention worker used `DELETE ... LIMIT`, which PostgreSQL does not support; batches
  are now selected by `ctid` with `FOR UPDATE SKIP LOCKED`.
- Cancellation tokens were not forwarded to connection acquisition, so opening a connection
  could not be cancelled.
- `ConnectionManager` held its configuration map in a non-concurrent `Dictionary` that could
  be written by `RegisterConfiguration` while another thread read it.
- `ExecuteWithConnectionAsync` disposed the connection twice.

### Migration notes

This release is **not source-compatible**. Renames and behaviour changes:

| Before | After |
|--------|-------|
| `PostgreSQLConfig` | `DatabaseConfiguration` |
| `DatabaseConfig` | `PostgreSqlDatabaseConfig` |
| config section `mysql` (a port leftover) | `postgresql` |
| `SslMode` | `PostgreSqlSslMode` |
| default port `3306` | `5432` |
| `QueryRaw()` | `SqlQueryAsync<T>()` / `ExecuteSqlAsync()` |
| `Models/Configuration.cs` | `Configuration/DatabaseConfiguration.cs` |

- The `DataType` enum is now PostgreSQL's type set. `DataType.Unspecified` is the default
  and infers from the CLR property type; `Guid` infers `uuid` rather than `CHAR(36)`, and
  `DateTime` infers `timestamptz`.
- Upserts need a conflict target. `ON CONFLICT` arbitrates on one named unique key rather
  than MySQL's "any duplicate key". It is inferred when the entity has exactly one candidate
  and must otherwise be passed as `conflictTarget`.
- Non-nullable CLR value types now generate `NOT NULL` columns.
- Identifiers are emitted double-quoted and are therefore case-sensitive.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## 2026-06-20

### Fixed

- Query-builder parameter re-keying could corrupt SQL when a predicate emitted
  11+ parameters (`@p1` substring-collided with `@p10`/`@p11`); parameters are
  now renamed longest-name-first.
- The expression translator wiped the entire WHERE buffer for a `null == x.Prop`
  comparison (it called `_sql.Clear()`), producing malformed SQL when combined
  with other clauses; null comparisons in both operand orders now translate to
  `IS [NOT] NULL` without discarding accumulated SQL.

### Documentation

- Full README rewrite to the unified house style: concise NuGet + MIT badges,
  one-line tagline, `Install` / `Quick start` / `Features` / `Configuration`
  (table + JSON) / `Documentation` / `Requirements` / `License`, with the API
  detail moved to the docs site (no full API dump in the README).
- Replaced the single `docs/libs/postgresql.md` guide with a three-page docs set
  mirroring CL.MySQL2's depth model: **Overview** (load, multi-database,
  repository CRUD, entry points, config, health, events), **Query Builder**
  (fluent methods, terminals, aggregates, bulk update/delete, raw SQL via
  `QueryRaw`/repository raw, transactions), and **Schema & Sync** (entity
  attributes, the `DataType` enum, table/set/namespace sync, `SyncResult`,
  schema backups, the migration tracker).
- The old `docs/libs/postgresql.md` is now a thin redirect to the new Overview.
- No API changes — documentation only.

## [4.5.2] — 2026-06-20

### Documentation

- Documented the full **query builder** surface: `OrderByDescending`, `Limit`/`Offset`
  (and `Take`/`Skip` aliases), `Join`, `Select`, `GroupBy`, `WithConnection`,
  `ToPagedListAsync`, `FirstOrDefaultAsync`, the `CountAsync`/`MaxAsync`/`MinAsync`/
  `SumAsync`/`AverageAsync` aggregates, and bulk `UpdateAsync`/`DeleteAsync`. Earlier
  docs listed only `Where`/`OrderBy`/`ToListAsync`.
- Documented raw SQL access via `QueryRaw()` (`QueryAsync`/`ExecuteAsync`).
- Documented the **repository** beyond basic CRUD: `InsertManyAsync`, `GetByColumnAsync`,
  `GetPagedAsync`, `FindAsync`, `IncrementAsync`/`DecrementAsync`, and
  `RawQueryAsync`/`RawExecuteAsync`.
- Documented the schema attributes `[Table]`, `[Column]`, `[ForeignKey]`,
  `[CompositeIndex]`, and `[Ignore]`, plus the `DataType` enum.
- Documented **table sync / migrations**: `SyncTablesAsync`, `SyncNamespaceAsync`,
  `SyncResult`, the `BackupManager` (schema backups + cleanup), and the
  `MigrationTracker` JSON history.
- Documented **transactions** via `BeginTransactionAsync` (auto-rollback on dispose).
- Documented previously-omitted configuration: `MaxIdleTime`, `AllowDestructiveSync`,
  multi-database `connectionId` selection, and runtime `RegisterDatabase`.

### Notes

- The 4.0.0 "repository CRUD only" note is superseded — the query builder
  (joins, aggregation, paging, bulk update/delete) is present and now documented.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Changed

- Annotated PostgreSQL configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Repository pattern + attribute-driven schema sync,
mirroring the CL.MySQL2 surface.

### Notes

- The MySQL2 4.0 query-builder rewrite (projection pushdown, SQL aggregation,
  smart-cache pools) has not been ported to CL.PostgreSQL yet — repository
  CRUD only.
- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.PostgreSQL).
