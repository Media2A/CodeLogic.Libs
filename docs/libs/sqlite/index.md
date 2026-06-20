# CL.SQLite

> An embedded SQLite data-access layer for CodeLogic 4 — connection pooling, WAL, attribute-driven table sync, a repository, and a fluent LINQ-shaped query builder.

`CL.SQLite` is the embedded-database sibling of `CL.MySQL2`. Map a plain class with attributes and the library keeps the live table in shape, then read and write through a `Repository<T>` or a fluent `QueryBuilder<T>`. It builds on [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), pools connections per database, and enables Write-Ahead Logging by default. Every fallible operation returns a `Result` / `Result<T>` — no exceptions for the expected failure paths.

| | |
|---|---|
| **Package** | [`CodeLogic.SQLite`](https://www.nuget.org/packages/CodeLogic.SQLite) |
| **Library class** | `CL.SQLite.SQLiteLibrary` |
| **Config file** | `config.sqlite.json` |
| **Dependencies** | Microsoft.Data.Sqlite 9.x |

This overview covers loading, schema sync, the repository, configuration, the migration ledger, the health check, and events. The deep query material lives on its own sub-page:

- **[Query Builder](queries.md)** — `Where` / ordering with `ThenBy` / projections / `GroupBy` aggregates / paging / terminals / bulk update & delete / raw SQL.

## Install & load

```bash
dotnet add package CodeLogic.SQLite
```

```csharp
using CL.SQLite;

await Libraries.LoadAsync<SQLiteLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var db = Libraries.Get<SQLiteLibrary>();
```

Set your databases in `config.sqlite.json` (auto-generated on first run) before `ConfigureAsync()`.

## Result type

Every data operation returns a `Result` (non-generic) or `Result<T>` from `CodeLogic.Core.Results`. Inspect the outcome before reading the value:

```csharp
Result<long> r = await repo.InsertAsync(note);
if (r.IsSuccess)
    use(r.Value);             // T? — the value on success
else
    log(r.Error?.Message);    // failure details
```

`Result<T>` exposes `IsSuccess` / `IsFailure`, `Value` (the `T?` value), and `Error` (a single `Error?` with `.Message`); construct with `Result<T>.Success(data)` / `Result<T>.Failure(error)`. The non-generic `Result` exposes `IsSuccess` / `IsFailure` and `Error`, built with `Result.Success()` / `Result.Failure(error)`. Nothing throws on a query failure — the failure path is the `Result`.

## Connection pool & WAL

Each named database has its own connection pool. Connections are reused up to `maxPoolSize` (default 10) and retired after about five minutes idle; the pool opens a fresh connection on demand when none is free. Pooling, the WAL journal mode (`useWAL`), and foreign-key enforcement (`enableForeignKeys`) are applied to every connection the pool hands out, so you never configure a raw `SqliteConnection` yourself.

The `ConnectionManager` property exposes the pool for advanced use — active / pooled counts and a connectivity test per connection id.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.SQLite.Models`. Only annotated properties are mapped — unannotated properties are ignored for schema, reads, and writes.

```csharp
using CL.SQLite.Models;

[SQLiteTable("notes")]
public sealed class NoteRecord
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "title", IsNotNull = true, IsIndexed = true)]
    public string Title { get; set; } = "";

    [SQLiteColumn(ColumnName = "body")]
    public string Body { get; set; } = "";

    [SQLiteColumn(ColumnName = "created_utc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

### Attributes

| Attribute | Purpose |
|-----------|---------|
| `[SQLiteTable("name")]` | Table name (defaults to the class name). |
| `[SQLiteColumn]` | Maps a property. Settings: `ColumnName`, `DataType`, `Size`, `IsPrimaryKey`, `IsAutoIncrement`, `IsIndexed`, `IsUnique`, `IsNotNull`, `DefaultValue`. |
| `[SQLiteIndex(cols…)]` | Class-level index (repeatable). Settings: `IsUnique`, `Name`. |
| `[SQLiteForeignKey(refTable, refColumn)]` | Foreign key with `OnDelete` / `OnUpdate`. |

`ForeignKeyAction` values: `NoAction` (default), `Restrict`, `SetNull`, `SetDefault`, `Cascade`.

`SQLiteDataType` values: `INTEGER`, `REAL`, `TEXT`, `BLOB`, `NUMERIC`, `DATETIME`, `DATE`, `BOOLEAN`, `UUID`. When `DataType` is omitted the column type is inferred from the property type; `DATETIME` / `DATE` / `BOOLEAN` / `UUID` are stored in SQLite's native affinities (`TEXT` / `INTEGER`) and converted automatically on read and write — along with `bool`, `DateTime`, `DateTimeOffset`, `Guid`, and `enum`.

```csharp
[SQLiteTable("orders")]
[SQLiteIndex("customer_id", "created_utc", Name = "ix_orders_customer")]
public sealed class OrderRecord
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "customer_id", IsNotNull = true)]
    [SQLiteForeignKey("customers", "id", OnDelete = ForeignKeyAction.Cascade)]
    public long CustomerId { get; set; }

    [SQLiteColumn(ColumnName = "total", DataType = SQLiteDataType.NUMERIC)]
    public decimal Total { get; set; }

    [SQLiteColumn(ColumnName = "created_utc")]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

## Schema sync

Entity classes are the source of truth. `TableSync` reconciles the live table to the class: it creates the table if missing, adds any missing columns, and builds declared indexes. Reconcile once, at startup.

```csharp
// One entity
Result<TableSyncResult> sync = await db.TableSync.SyncTableAsync<NoteRecord>();
if (sync.IsSuccess)
    Console.WriteLine(sync.Value!.Message);   // Created / altered summary

// A set of types in one pass
Dictionary<string, Result<TableSyncResult>> many =
    await db.TableSync.SyncTablesAsync(new[] { typeof(NoteRecord), typeof(OrderRecord) });

// Everything in a namespace
Dictionary<string, Result<TableSyncResult>> ns =
    await db.TableSync.SyncNamespaceAsync("MyApp.Entities", includeDerived: false);
```

Every `SyncTableAsync` / `SyncTablesAsync` / `SyncNamespaceAsync` overload takes an optional `connectionId` (default `"Default"`). `TableSyncResult` carries `Success`, `Message`, and an optional `Exception`. The batch overloads return a dictionary keyed by table name so you can inspect each result.

> Schema sync is **additive** — it creates tables, adds columns, and builds indexes. It never drops or retypes existing columns. For changes that aren't additive, use a migration (below) and run your own DDL.

## Repository CRUD

`GetRepository<T>()` returns a `Repository<T>` covering the common CRUD, paging, count, and raw-SQL operations. All return `Result`.

```csharp
var repo = db.GetRepository<NoteRecord>();

// Create — InsertAsync returns the new rowid and writes it back to an auto-increment PK
Result<long> id      = await repo.InsertAsync(new NoteRecord { Title = "Hello" });
Result       upsert  = await repo.UpsertAsync(note);            // INSERT OR REPLACE

// Read
Result<NoteRecord?>      byId  = await repo.GetByIdAsync(1L);
Result<List<NoteRecord>> all   = await repo.GetAllAsync(limit: 1000);
Result<List<NoteRecord>> found = await repo.FindAsync(n => n.Title.StartsWith("draft"));
Result<long>             count = await repo.CountAsync();

// Paged
Result<PagedResult<NoteRecord>> page =
    await repo.GetPagedAsync(page: 1, pageSize: 20, orderBy: "created_utc", desc: true);

// Update / delete by primary key
Result updated = await repo.UpdateAsync(note);
Result deleted = await repo.DeleteAsync(1L);
```

`PagedResult<T>` carries `Items`, `PageNumber`, `PageSize`, `TotalItems`, and `TotalPages`.

> `UpsertAsync` is implemented with `INSERT OR REPLACE`, which SQLite performs as a **delete followed by an insert** of the conflicting row. If other tables reference the row with `ON DELETE CASCADE`, replacing it will delete those child rows. When that matters, do an explicit `GetByIdAsync` + `UpdateAsync` instead.

### Composite keys

For entities with a multi-column primary key, the by-keys overloads take the cancellation token first, then the key values positionally in primary-key order:

```csharp
[SQLiteTable("memberships")]
public sealed class Membership
{
    [SQLiteColumn(ColumnName = "user_id", IsPrimaryKey = true)] public long UserId { get; set; }
    [SQLiteColumn(ColumnName = "group_id", IsPrimaryKey = true)] public long GroupId { get; set; }
    [SQLiteColumn(ColumnName = "role")] public string Role { get; set; } = "";
}

var repo = db.GetRepository<Membership>();

Result<Membership?> m = await repo.GetByKeysAsync(CancellationToken.None, 42L, 7L);
Result             d = await repo.DeleteByKeysAsync(CancellationToken.None, 42L, 7L);
```

### Repository surface

| Method | Returns | Notes |
|--------|---------|-------|
| `InsertAsync(entity)` | `Result<long>` | New rowid; auto-increment PK written back to the entity. |
| `UpsertAsync(entity)` | `Result` | `INSERT OR REPLACE`. |
| `GetByIdAsync(id)` | `Result<T?>` | Single-column primary key. |
| `GetByKeysAsync(ct, params keys)` | `Result<T?>` | Composite primary key. |
| `GetAllAsync(limit = 1000)` | `Result<List<T>>` | Capped row scan. |
| `FindAsync(predicate)` | `Result<List<T>>` | LINQ `WHERE`. |
| `UpdateAsync(entity)` | `Result` | Update by primary key. |
| `DeleteAsync(id)` | `Result` | Delete by single primary key. |
| `DeleteByKeysAsync(ct, params keys)` | `Result` | Delete by composite key. |
| `CountAsync()` | `Result<long>` | Row count. |
| `GetPagedAsync(page, pageSize, orderBy?, desc?)` | `Result<PagedResult<T>>` | Data page + total. |
| `RawQueryAsync(sql, params?)` | `Result<List<T>>` | Raw SELECT mapped to entities. |
| `RawExecuteAsync(sql, params?)` | `Result<int>` | Raw non-query, returns affected rows. |

Raw SQL takes named parameters as a `Dictionary<string, object?>` — always bind values, never interpolate user input into the SQL string:

```csharp
Result<List<NoteRecord>> rows = await repo.RawQueryAsync(
    "SELECT * FROM notes WHERE title LIKE @q",
    new() { ["@q"] = "%hello%" });

Result<int> n = await repo.RawExecuteAsync(
    "UPDATE notes SET body = '' WHERE created_utc < @cutoff",
    new() { ["@cutoff"] = cutoff });
```

For composed reads, ordering, projections, aggregates, and bulk mutations, use the [Query Builder](queries.md).

## Migration ledger

`MigrationTracker` is a **ledger**, not an Up/Down runner. It records migration ids around DDL you execute yourself, so you can make schema changes idempotent across restarts and nodes. History is written to `{dataDir}/migrations/migration_history.json`.

```csharp
var tracker = db.MigrationTracker;

if (!await tracker.HasMigrationBeenAppliedAsync("2026-06-add-notes-archived"))
{
    await repo.RawExecuteAsync("ALTER TABLE notes ADD COLUMN archived INTEGER NOT NULL DEFAULT 0");
    await tracker.RecordMigrationAsync("2026-06-add-notes-archived", "Add notes.archived flag");
}

List<MigrationRecord> applied = await tracker.GetAppliedMigrationsAsync();
// each: MigrationId, Description?, AppliedAt
```

| Method | Returns | Purpose |
|--------|---------|---------|
| `HasMigrationBeenAppliedAsync(id)` | `bool` | Has this id been recorded. |
| `RecordMigrationAsync(id, description?)` | `bool` | Record an id (after your DDL succeeds). |
| `GetAppliedMigrationsAsync()` | `List<MigrationRecord>` | All recorded migrations. |
| `RemoveMigrationRecordAsync(id)` | `bool` | Remove a record (e.g. after a manual rollback). |

## Health check

```csharp
HealthStatus status = await db.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
// data: totalDatabases, failedDatabases, connections
```

`HealthCheckAsync` probes each enabled database. When no database is enabled the library reports healthy-but-disabled rather than failing.

## Configuration

Auto-generated on first run as `config.sqlite.json` (section `sqlite`). The config is a `Databases` map — each key is a connection id you pass to `GetRepository` / `GetQueryBuilder` / `SyncTableAsync`; `Default` is created automatically.

```json
{
  "databases": {
    "Default": {
      "enabled": true,
      "databasePath": "database.db",
      "connectionTimeoutSeconds": 30,
      "commandTimeoutSeconds": 120,
      "skipTableSync": false,
      "cacheMode": "Default",
      "useWAL": true,
      "enableForeignKeys": true,
      "maxPoolSize": 10,
      "slowQueryThresholdMs": 500
    }
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `enabled` | `bool` | `true` | Per-database master switch. |
| `databasePath` | `string` | `database.db` | Absolute, or relative to the library data directory. |
| `connectionTimeoutSeconds` | `int` | `30` | Connection open timeout. |
| `commandTimeoutSeconds` | `int` | `120` | Per-command timeout. |
| `skipTableSync` | `bool` | `false` | Turn off automatic schema sync for this database. |
| `cacheMode` | `enum` | `Default` | SQLite cache mode: `Default` / `Private` / `Shared`. |
| `useWAL` | `bool` | `true` | Set `journal_mode=WAL` for better concurrency. |
| `enableForeignKeys` | `bool` | `true` | Enforce foreign-key constraints (`PRAGMA foreign_keys`). |
| `maxPoolSize` | `int` | `10` | Maximum pooled connections per database. |
| `slowQueryThresholdMs` | `int` | `500` | Threshold above which a query is logged as slow. |

A database with `enabled: false` is skipped at startup; if no database is enabled the library initializes disabled and the health check reports healthy-but-disabled.

## Events

`CL.SQLite.Events` types implement `IEvent` and publish to the CodeLogic event bus.

| Event | Published when |
|-------|----------------|
| `TableSyncedEvent` | A table is reconciled by schema sync (carries `TableName`, `Created`, `Message`, `SyncedAt`). |

## See also

- [Query Builder](queries.md) — filters, ordering, projections, aggregates, paging, bulk writes, and raw SQL.
- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.SQLite)
