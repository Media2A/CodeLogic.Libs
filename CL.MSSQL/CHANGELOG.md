# Changelog

## 2026-09-13

### Fixed

- **A migration registered twice ran twice.** `Register` and `RegisterFrom` both appended
  unconditionally, so pairing `RegisterMigrationsFrom(assembly)` with an explicit
  `RegisterMigration(...)` held two copies, and both passed the apply filter. Registration
  now deduplicates by migration id.
- **`HealthChangedEvent` was declared but never raised.** It is now published on a health
  state transition.

### Fixed

- **The schema-state sentinel was keyed on the bare table name**, so two entities with the
  same table name in different schemas shared one row and masked each other's CRC. The key
  is now `schema.table`; `SchemaStateStore` resolves an unqualified name against `dbo`, so
  the public diagnostic API still accepts a bare table name.
- Restoring a table from backup cleared the sentinel by the bare name, which no longer
  matches the qualified key and left a stale CRC behind.

### Added

- `RetentionWorker.RunOnceAsync()` is now public; it already existed but was internal, so
  the three libraries now expose the same retention surface.

- `GetRepository<T>(TransactionScope)` and `Query<T>(TransactionScope)`.
  `BeginTransactionAsync` returned a scope that neither accessor took, so callers had to
  construct `Repository<T>` by hand to do any work inside a transaction.

### Fixed

- `Contains()` over an empty collection emitted `IN ()`, which is a syntax error. It now
  emits `1 = 0`.
- Cancellation tokens are forwarded to connection acquisition, so opening a connection can
  be cancelled.
- `ConnectionManager` held its configuration map in a non-concurrent `Dictionary` that could
  be written by `RegisterConfiguration` while another thread read it.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## Unreleased

- Initial `CodeLogic.MSSQL` release for SQL Server 2019+, SQL Server 2022/2025, and Azure SQL Database.
- Added repository, fluent-query, projection, grouping, paging, transaction, raw SQL, cache, health, migration, retention, backup, and named-connection workflows matching `CL.MySQL2`.
- Added SQL Server-native mappings, schemas, identity output, parameter-capped batches, lock-based non-`MERGE` upserts, `sys.*` schema management, application locks, transient retry, and estimated-plan capture.
