# Changelog

## 2026-09-13

### Documentation

- Corrected the `SqlFn` XML documentation, which had been copied from the MySQL library and
  named functions T-SQL does not have (`HOUR`, `MINUTE`, `DATE`, `DAYOFWEEK`, `IFNULL`,
  `FROM_UNIXTIME`). It now describes the `DATEPART`/`CONVERT`/`COALESCE` SQL actually emitted.
- Documented `SqlFn` and the transaction-scoped `GetRepository<T>(tx)` / `Query<T>(tx)`
  accessors in the queries guide, and added the missing soft-delete and retention sections to
  the schema guide — the index page had linked to them all along.
- Corrected more comments carried over from the MySQL port: `[Column(PreviousName = ...)]`
  renames via `EXEC sys.sp_rename`, not `CHANGE COLUMN`; the LIKE escaper escapes `%`, `_`
  and `[`, not a backslash; column references are bracket-quoted, not backtick-quoted; and
  `UpsertAsync` does not use any `AS new` alias syntax — it stages the row in a table
  variable and matches it with `UPDLOCK`/`HOLDLOCK` under a `SERIALIZABLE` transaction.
- **Raw SQL cannot join a `TransactionScope`.** The queries guide showed `ExecuteSqlAsync`
  calls inside an open scope being committed by `tx.CommitAsync()`; they in fact open their
  own connection and run outside the transaction. The example is replaced with an explicit
  warning and the supported alternatives.
- Flagged the configuration settings that are declared but not yet read by the library, so
  they are no longer documented as working knobs: `QueryTimeoutMs`, `MaxInClauseValues`
  (no cap is applied to generated `IN (...)` lists), `PreparedStatementCacheSize`,
  `N1DetectorThreshold`, `DefaultStringSize`, `CacheEnabledOverride`, `DefaultTtlSeconds`
  and `PublishEvents` — plus `MaxBatchInsertSize`, which `GetRepository<T>()` does not pass
  to the repository it builds.
- Marked the N+1 detector as not wired up. The setting, the event and
  `QueryObservability.RecordN1` all exist, but nothing counts repeats or publishes the
  event, so `N1QueryDetectedEvent` never fires today.
- Fixed the `MinPoolSize` default in the configuration table: it is `0`, not `1`.
- Fixed the imperative-migration example, which would not compile — the `Migration` base
  supplies `Version` and `Description` from its `(appVersion, order, description)`
  constructor and neither is virtual.
- Corrected the retention description: each pass loops until a batch deletes fewer rows than
  `BatchSize`, not until it deletes zero; and the background worker only starts if an entity
  carrying `[RetainDays]` is already registered when the library starts, so a purge for an
  entity synced later must be driven through `RetentionWorker.RunOnceAsync()`.
- Clarified that table-version invalidation is skipped for tables with live `SmartCachePool`
  entries, that `QueryCache.Enabled` / `TimeQuantizeSeconds` are internal rather than part of
  the public facade, and that `MaxMemoryMb` is advisory (eviction is by entry count).
- Noted that composition-time guard errors (unsupported expressions, `.Join` after
  `.OrderBy`, `WhereExists` on the outer table) throw rather than returning a `Result`.

### Added

- `GetRepository<T>(TransactionScope)` and `Query<T>(TransactionScope)`.
  `BeginTransactionAsync` returned a scope that neither accessor took, so callers had to
  construct `Repository<T>` by hand to do any work inside a transaction.
- `RetentionWorker.RunOnceAsync()` is now public; it already existed but was internal, so
  the three libraries now expose the same retention surface.

### Fixed

- **`SqlFn.Like` in a projection, `GROUP BY` key or `UPDATE ... SET` was a syntax error.**
  T-SQL has no boolean expression type, so the emitted `a LIKE b` is a predicate and is
  rejected anywhere a value is expected (`Incorrect syntax near the keyword 'LIKE'`). It
  now materializes as `CAST(CASE WHEN a LIKE b THEN 1 ELSE 0 END AS bit)`. `Where(...)`
  was never affected — it takes a different translation path.
- **A migration registered twice ran twice.** `Register` and `RegisterFrom` both appended
  unconditionally, so pairing `RegisterMigrationsFrom(assembly)` with an explicit
  `RegisterMigration(...)` held two copies, and both passed the apply filter. Registration
  now deduplicates by migration id.
- **`HealthChangedEvent` was declared but never raised.** It is now published on a health
  state transition.
- **The schema-state sentinel was keyed on the bare table name**, so two entities with the
  same table name in different schemas shared one row and masked each other's CRC. The key
  is now `schema.table`; `SchemaStateStore` resolves an unqualified name against `dbo`, so
  the public diagnostic API still accepts a bare table name.
- Restoring a table from backup cleared the sentinel by the bare name, which no longer
  matched the qualified key and left a stale CRC behind.
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
