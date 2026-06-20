# CL.PostgreSQL — Changelog

All notable changes to **CodeLogic.PostgreSQL** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

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
