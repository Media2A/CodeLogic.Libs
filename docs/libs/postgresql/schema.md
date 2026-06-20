# CL.PostgreSQL — Schema & Sync

> Your entity classes are the source of truth — declarative sync keeps the live schema in shape, schema backups protect existing tables, and the migration tracker records what has run.

See the [overview](index.md) for loading, repositories, configuration, and events.

CL.PostgreSQL reconciles tables from attribute-mapped classes. Sync diffs each class against the live table and emits the DDL to converge them — creating the table when it is missing, or adding the missing columns and indexes when it exists. Before altering an existing table it writes a timestamped schema backup so you can recover the shape if a change goes wrong.

## Entity attributes

All attributes live in `CL.PostgreSQL.Models`.

```csharp
using CL.PostgreSQL.Models;

[Table(Name = "orders", Schema = "public", Comment = "Customer orders")]
[CompositeIndex("ix_orders_customer_status", "CustomerId", "Status", Unique = false)]
public class Order
{
    [Column(Primary = true, AutoIncrement = true)]
    public int Id { get; set; }

    [Column(NotNull = true, Index = true)]
    public int CustomerId { get; set; }

    [Column(DataType = DataType.Numeric, Precision = 12, Scale = 2)]
    public decimal Total { get; set; }

    [Column(Name = "status", Size = 32, DefaultValue = "'pending'")]
    public string Status { get; set; } = "pending";

    [Column(DataType = DataType.TimestampTz, OnUpdateCurrentTimestamp = true)]
    public DateTime UpdatedUtc { get; set; }

    [ForeignKey("customers", "Id", OnDelete = ForeignKeyAction.Cascade)]
    public int FkCustomer { get; set; }

    [Ignore]
    public string? TransientNote { get; set; }
}
```

### `[Table]`

| Property | Default | Purpose |
|----------|---------|---------|
| `Name` | class name | Table name. |
| `Schema` | `"public"` | Owning schema. |
| `Comment` | — | Table comment. |

### `[Column]`

| Property | Default | Purpose |
|----------|---------|---------|
| `Name` | property name | Column name. |
| `DataType` | inferred | Explicit PostgreSQL type (see `DataType` below). |
| `Size` | `0` | Length for `Char` / `VarChar`. |
| `Precision` / `Scale` | `10` / `2` | For `Numeric`. |
| `Primary` | `false` | Part of the primary key. |
| `AutoIncrement` | `false` | `GENERATED ALWAYS AS IDENTITY`. |
| `NotNull` | `false` | `NOT NULL`. |
| `Unique` | `false` | Unique constraint. |
| `Index` | `false` | Single-column index. |
| `DefaultValue` | — | Column default (SQL literal or expression). |
| `Comment` | — | Column comment. |
| `OnUpdateCurrentTimestamp` | `false` | Maintains the column via an update trigger. |

### `[ForeignKey]`, `[CompositeIndex]`, `[Ignore]`

```csharp
[ForeignKey("customers", "Id", OnDelete = ForeignKeyAction.Cascade)]
public int CustomerId { get; set; }

[CompositeIndex("ix_day_status", "Day", "Status", Unique = true)]   // class-level, repeatable
[Table]
public class DailyOrder { /* … */ }

[Ignore]                       // not persisted, not synced
public string? Transient { get; set; }
```

- `[ForeignKey(referenceTable, referenceColumn)]` with `OnDelete` / `OnUpdate` (`ForeignKeyAction`: `Restrict` *(default)*, `Cascade`, `SetNull`, `NoAction`, `SetDefault`) and an optional `ConstraintName`.
- `[CompositeIndex(indexName, columns…)]` — class-level, repeatable (`AllowMultiple`); `Unique` is optional and defaults to `false`.
- `[Ignore]` — excludes a property from reads, writes, and schema sync.

### `DataType`

The `DataType` enum maps to PostgreSQL types:

`SmallInt`, `Int`, `BigInt`, `Real`, `DoublePrecision`, `Numeric`, `Timestamp`, `Date`, `Time`, `TimeTz`, `TimestampTz`, `Char`, `VarChar`, `Text`, `Json`, `Jsonb`, `Uuid`, `Bool`, `Bytea`, and array types `IntArray`, `BigIntArray`, `TextArray`, `NumericArray`.

`SslMode` (used in configuration, not attributes) has the values `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`.

## Running sync

```csharp
// One table, with a schema backup first (default true)
Result<SyncResult> r = await pg.SyncTableAsync<User>(createBackup: true);
```

For batch reconciles, use the `TableSync` service on the library. The same `createBackup` flag and `connectionId` apply.

```csharp
// A specific set of model types
Result<Dictionary<string, SyncResult>> set =
    await pg.TableSync.SyncTablesAsync(new[] { typeof(User), typeof(Order) });

// Every mapped type in a namespace (optionally including derived namespaces)
Result<Dictionary<string, SyncResult>> ns =
    await pg.TableSync.SyncNamespaceAsync("MyApp.Entities", includeDerivedNamespaces: true);
```

| Method | Returns |
|--------|---------|
| `SyncTableAsync<T>(connectionId = "Default", createBackup = true, ct)` | `Result<SyncResult>` |
| `SyncTablesAsync(Type[] modelTypes, connectionId, createBackup, ct)` | `Result<Dictionary<string, SyncResult>>` |
| `SyncNamespaceAsync(namespaceName, connectionId, createBackup, includeDerivedNamespaces = false, ct)` | `Result<Dictionary<string, SyncResult>>` |

> `AllowDestructiveSync` (per database in `config.postgresql.json`, default `false`) gates DROP operations during sync. Leave it off in production — sync then only adds and modifies, never drops.

### `SyncResult`

| Field | Meaning |
|-------|---------|
| `Success` | Whether the reconcile succeeded. |
| `TableName` | The table reconciled. |
| `SchemaName` | The owning schema (default `"public"`). |
| `Operations` | `List<string>` of DDL statements applied. |
| `Errors` | Any errors encountered. |
| `Duration` | How long it took. |

```csharp
var sync = (await pg.SyncTableAsync<User>()).Value!;
foreach (var op in sync.Operations) { /* log the DDL applied */ }
```

## Schema backups

Before altering an existing table, sync writes a DDL-only backup to `{DataDirectory}/backups/{schema}_{table}_{timestamp}.sql`. The `BackupManager` lets you snapshot on demand and prune old files.

```csharp
Result<bool> ok      = await pg.BackupManager.BackupTableSchemaAsync("public", "users");
Result<int>  removed = await pg.BackupManager.CleanupOldBackupsAsync(keepCount: 10);
```

| Method | Returns | Purpose |
|--------|---------|---------|
| `BackupTableSchemaAsync(schema, table, connectionId = "Default", ct)` | `Result<bool>` | Snapshot one table's DDL. |
| `CleanupOldBackupsAsync(keepCount = 10)` | `Result<int>` | Keep the newest *N* backups, delete the rest. |

> Backups are DDL-only. They recover schema shape, not row data.

## Migration tracker

For changes sync can't express — seed data, data backfills — apply the SQL yourself (via raw SQL or a transaction; see [Query Builder](queries.md)) and record it with the `MigrationTracker` so it runs once. The history lives in `{DataDirectory}/migrations/migration_history.json`.

```csharp
await pg.MigrationTracker.EnsureMigrationsFileAsync();

if (!await pg.MigrationTracker.HasMigrationBeenAppliedAsync("2026-06-20-seed-roles"))
{
    var raw = pg.QueryRaw();
    await raw.ExecuteAsync("INSERT INTO \"roles\" (\"Name\") VALUES ('admin'), ('user')");
    await pg.MigrationTracker.RecordMigrationAsync(
        "2026-06-20-seed-roles", description: "Seed default roles");
}

List<MigrationRecord> applied = await pg.MigrationTracker.GetAppliedMigrationsAsync();
```

| Method | Returns | Purpose |
|--------|---------|---------|
| `EnsureMigrationsFileAsync(ct)` | `bool` | Create the history file if absent. |
| `HasMigrationBeenAppliedAsync(migrationId, ct)` | `bool` | Whether an id is already recorded. |
| `RecordMigrationAsync(migrationId, description?, checksum?, ct)` | `bool` | Mark a migration applied. |
| `GetAppliedMigrationsAsync(ct)` | `List<MigrationRecord>` | All recorded migrations. |
| `RemoveMigrationRecordAsync(migrationId, ct)` | `bool` | Remove a record (e.g. after a rollback). |

`MigrationRecord` carries `Id` (int), `MigrationId`, `Description?`, `AppliedAt`, and `Checksum?`.

> The tracker is a ledger, not a runner — it records that a migration ran and lets you guard against re-running it. The actual SQL is yours to execute.

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.PostgreSQL)
