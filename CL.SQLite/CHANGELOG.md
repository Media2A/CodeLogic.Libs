# CL.SQLite — Changelog

All notable changes to **CodeLogic.SQLite** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [4.5.2] — 2026-06-20

### Documentation

- Corrected the README to match the shipping API: the query builder is obtained
  via `GetQueryBuilder<T>()` (there is no `sqlite.Query<T>()`), all data
  operations return `Result` / `Result<T>`, and entities require
  `[SQLiteTable]` / `[SQLiteColumn]` annotations. The previous Quick Start no
  longer compiled.
- Documented the configuration as the real `databases` map (per-named-database
  `databasePath`, `useWAL`, `cacheMode`, `maxPoolSize`, `slowQueryThresholdMs`,
  timeouts, `skipTableSync`, `enableForeignKeys`), replacing the inaccurate
  `connections` array with `journalMode`/`poolSize`.
- Documented previously undocumented user-facing surface that already shipped:
  the full query builder (`Select`, `GroupBy`, `Sum`/`Max`/`Min`, predicate
  `DeleteAsync`/`UpdateAsync`, `ToPagedListAsync`), repository `UpsertAsync`,
  composite-key (`GetByKeysAsync`/`DeleteByKeysAsync`), `GetPagedAsync`, raw SQL
  (`RawQueryAsync`/`RawExecuteAsync`), attribute-driven schema sync
  (`SyncTableAsync`/`SyncTablesAsync`/`SyncNamespaceAsync` with
  `[SQLiteIndex]`/`[SQLiteForeignKey]`), and the `MigrationTracker`. No code
  changes — documentation only.

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.3] — 2026-04-16

### Fixed

- Added missing `<param name="connectionId">` XML doc tags so the public API
  no longer trips doc-warning gates.

## [4.0.2] — 2026-04-09

### Changed

- Annotated SQLite configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. Embedded-DB sibling of CL.MySQL2 with the same
repository pattern and attribute-driven schema sync.

### Notes

- The MySQL2 4.0 query-builder rewrite (projection pushdown, SQL aggregation,
  smart-cache pools) has not been ported to CL.SQLite yet — repository
  CRUD only.
- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.SQLite).
