# CodeLogic.PostgreSQL

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.PostgreSQL)](https://www.nuget.org/packages/CodeLogic.PostgreSQL)

PostgreSQL database access for [CodeLogic](https://github.com/Media2A/CodeLogic) applications with multi-database support, a fluent LINQ query builder, an attribute-driven repository, transactions, and automatic table sync / schema migrations with backups.

## Install

```
dotnet add package CodeLogic.PostgreSQL
```

## Quick Start

```csharp
var pgLib = new PostgreSQLLibrary();
// After library initialization via the CodeLogic framework:

// Define an entity
[Table(Name = "users", Schema = "public")]
public class User
{
    [Column(Primary = true, AutoIncrement = true)]
    public int Id { get; set; }

    [Column(NotNull = true)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; }
}

// Sync the table schema (creates or alters to match the entity)
await pgLib.SyncTableAsync<User>();

// Typed repository (CRUD)
var repo = pgLib.GetRepository<User>();
var created = await repo.InsertAsync(new User { Name = "Ada", IsActive = true });

// Fluent query builder
var users = await pgLib.Query<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.Name)
    .Limit(50)
    .ToListAsync();

// Transactions (auto-rollback on dispose if not committed)
await using var tx = await pgLib.BeginTransactionAsync();
// ... do work ...
await tx.CommitAsync();
```

All async operations return `Result<T>` / `Result<...>` — check `IsSuccess` / `IsFailure` and read `.Value` or `.Error`.

## Features

- **Multi-database support** -- manage connections to multiple PostgreSQL instances from one config; pick the connection per call with `connectionId`.
- **Fluent query builder** -- `Query<T>()` with `Where`, `OrderBy`/`OrderByDescending`, `Limit`/`Offset` (`Take`/`Skip`), `Join`, `Select`, `GroupBy`, aggregates, paging, and bulk update/delete.
- **Repository pattern** -- `GetRepository<T>()` for full CRUD plus bulk insert, paging, find, and atomic increment/decrement.
- **Attribute-driven schema** -- `[Table]`, `[Column]`, `[ForeignKey]`, `[CompositeIndex]`, `[Ignore]`.
- **Table sync and migrations** -- create or alter tables to match entities, with timestamped schema backups and JSON migration history.
- **Transactions** -- `BeginTransactionAsync()` returns an `await using` scope that auto-rolls-back if not committed.
- **Connection pooling** -- configurable pool sizes, idle timeout, command/connect timeouts, SSL mode, and slow-query logging.
- **Health checks and events** -- `HealthCheckAsync()` plus published events (table synced, slow query, connect/disconnect, health changed).

## Entities and Attributes

```csharp
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

    [ForeignKey("customers", "Id", OnDelete = ForeignKeyAction.Cascade)]
    public int FkCustomer { get; set; }

    [Ignore]
    public string? TransientNote { get; set; }
}
```

- `[Table]` — `Name` (defaults to class name), `Schema` (defaults to `public`), `Comment`.
- `[Column]` — `Name`, `DataType`, `Size`, `Precision`/`Scale`, `Primary`, `AutoIncrement` (GENERATED ALWAYS AS IDENTITY), `NotNull`, `Unique`, `Index`, `DefaultValue`, `Comment`, `OnUpdateCurrentTimestamp`.
- `[ForeignKey(referenceTable, referenceColumn)]` — `OnDelete` / `OnUpdate` (`ForeignKeyAction`: `Restrict`, `Cascade`, `SetNull`, `NoAction`, `SetDefault`), `ConstraintName`.
- `[CompositeIndex(indexName, columnNames...)]` — multi-column index; `Unique` optional; repeatable.
- `[Ignore]` — excludes a property from reads, writes, and schema sync.

`DataType` values include `SmallInt`, `Int`, `BigInt`, `Real`, `DoublePrecision`, `Numeric`, `Timestamp`/`TimestampTz`, `Date`, `Time`/`TimeTz`, `Char`, `VarChar`, `Text`, `Json`, `Jsonb`, `Uuid`, `Bool`, `Bytea`, and array types (`IntArray`, `BigIntArray`, `TextArray`, `NumericArray`).

## Repository

```csharp
var repo = pgLib.GetRepository<User>();           // optional connectionId argument

await repo.InsertAsync(user);                       // INSERT ... RETURNING *
await repo.InsertManyAsync(users);
await repo.GetByIdAsync(1);
await repo.GetByColumnAsync("Name", "Ada");
await repo.GetAllAsync();
await repo.GetPagedAsync(page: 1, pageSize: 25, orderByColumn: "Name", descending: false);
await repo.CountAsync();
await repo.UpdateAsync(user);                        // by primary key, RETURNING *
await repo.DeleteAsync(1);
await repo.FindAsync(u => u.IsActive);
await repo.IncrementAsync(1, u => u.LoginCount, 1);
await repo.DecrementAsync(1, u => u.Credits, 5);
await repo.RawQueryAsync("SELECT * FROM \"users\" WHERE \"Name\" = @n",
    new() { ["@n"] = "Ada" });
await repo.RawExecuteAsync("UPDATE \"users\" SET \"IsActive\" = false");
```

A primary key (`[Column(Primary = true)]`) is required for `GetByIdAsync`, `UpdateAsync`, `DeleteAsync`, and increment/decrement.

## Query Builder

```csharp
var page = await pgLib.Query<User>()
    .Where(u => u.IsActive)
    .OrderByDescending(u => u.Name)
    .ToPagedListAsync(page: 1, pageSize: 20);

var first = await pgLib.Query<User>()
    .Where(u => u.Id == 1)
    .FirstOrDefaultAsync();

var total   = await pgLib.Query<User>().Where(u => u.IsActive).CountAsync();
var maxId   = await pgLib.Query<User>().MaxAsync(u => u.Id);
var sum     = await pgLib.Query<User>().SumAsync(u => u.Credits);
var average = await pgLib.Query<User>().AverageAsync(u => u.Credits);

// Bulk update / delete
await pgLib.Query<User>()
    .Where(u => !u.IsActive)
    .UpdateAsync(new() { ["Status"] = "archived" });

await pgLib.Query<User>().Where(u => !u.IsActive).DeleteAsync();
```

Chain methods: `Where`, `OrderBy`/`OrderByDescending`, `Limit`/`Offset` (aliases `Take`/`Skip`), `Join(table, condition, JoinType)`, `Select(...)`, `GroupBy(...)`, `WithConnection(id)`. Terminal methods: `ToListAsync`, `FirstOrDefaultAsync`, `ToPagedListAsync`, `CountAsync`, `MaxAsync`/`MinAsync`/`SumAsync`/`AverageAsync`, `UpdateAsync`, `DeleteAsync`.

### Raw SQL

```csharp
var raw = pgLib.QueryRaw();
var rows = await raw.QueryAsync(
    "SELECT \"Name\" FROM \"users\" WHERE \"Id\" = @id",
    new() { ["@id"] = 1 });           // Result<List<Dictionary<string, object?>>>

await raw.ExecuteAsync("TRUNCATE \"users\"");   // Result<int>
```

## Transactions

```csharp
await using var tx = await pgLib.BeginTransactionAsync();   // optional connectionId
try
{
    // ... work on the same connection/transaction ...
    await tx.CommitAsync();
}
catch
{
    await tx.RollbackAsync();
}
// If neither Commit nor Rollback is called, dispose auto-rolls back.
```

## Table Sync and Migrations

```csharp
// Single entity (creates table if missing, otherwise adds missing columns/indexes)
Result<SyncResult> result = await pgLib.SyncTableAsync<User>();      // createBackup: true by default

// Lower-level service for batch operations
await pgLib.TableSync.SyncTablesAsync(new[] { typeof(User), typeof(Order) });
await pgLib.TableSync.SyncNamespaceAsync("MyApp.Entities", includeDerivedNamespaces: true);
```

`SyncResult` reports `Success`, `SchemaName`/`TableName`, the list of `Operations` applied, any `Errors`, and `Duration`.

Before altering an existing table, a timestamped schema backup is written under the library's data directory (`backups/`). Manage backups via `pgLib.BackupManager`:

```csharp
await pgLib.BackupManager.BackupTableSchemaAsync("public", "users");
await pgLib.BackupManager.CleanupOldBackupsAsync(keepCount: 10);
```

Applied migrations are tracked in `migrations/migration_history.json` via `pgLib.MigrationTracker` (`RecordMigrationAsync`, `HasMigrationBeenAppliedAsync`, `GetAppliedMigrationsAsync`, `RemoveMigrationRecordAsync`).

## Configuration

Config file: `config.postgresql.json`

```json
{
  "Databases": {
    "Default": {
      "Enabled": true,
      "Host": "localhost",
      "Port": 5432,
      "Database": "mydb",
      "Username": "postgres",
      "Password": "",
      "ConnectionTimeout": 30,
      "CommandTimeout": 30,
      "MinPoolSize": 5,
      "MaxPoolSize": 100,
      "MaxIdleTime": 60,
      "SslMode": "Prefer",
      "AllowDestructiveSync": false,
      "SlowQueryThresholdMs": 1000
    }
  }
}
```

- **`Databases`** — a named map; add more keys (e.g. `"Reporting"`) and pass that key as `connectionId`. Disabled databases (`"Enabled": false`) are skipped at startup.
- **`SslMode`** — `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, or `VerifyFull`.
- **`MaxIdleTime`** — seconds an idle pooled connection is kept before being closed.
- **`AllowDestructiveSync`** — dev-only; allows DROP operations during schema sync.
- **`SlowQueryThresholdMs`** — queries at or above this duration are logged as warnings.

You can also register a database at runtime:

```csharp
pgLib.RegisterDatabase("Reporting", new DatabaseConfig
{
    Host = "reports.internal", Database = "analytics",
    Username = "reader", Password = "***"
});
```

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- Npgsql 9.x

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
