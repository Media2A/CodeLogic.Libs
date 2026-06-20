# CL.PostgreSQL

> A typed PostgreSQL data-access layer for CodeLogic 4 — multi-database connections, an attribute-driven repository, a fluent LINQ query builder, transactions, and declarative schema sync with backups and migration tracking.

`CL.PostgreSQL` is the PostgreSQL sibling of the flagship [CL.MySQL2](../mysql2/index.md). Map a plain class with attributes and the library reconciles the live table to match, then exposes a typed `Repository<T>` and a chainable `QueryBuilder<T>` over it. It builds on [Npgsql](https://www.nuget.org/packages/Npgsql) and connects to one or many PostgreSQL instances from a single config. Every fallible operation returns a framework `Result<T>` — check `IsSuccess` / `IsFailure` and read `.Value` or `.Error?.Message`.

| | |
|---|---|
| **Package** | [`CodeLogic.PostgreSQL`](https://www.nuget.org/packages/CodeLogic.PostgreSQL) |
| **Library class** | `CL.PostgreSQL.PostgreSQLLibrary` |
| **Config file** | `config.postgresql.json` (section `postgresql`) |
| **Dependencies** | Npgsql 9.x |
| **Engines** | PostgreSQL 12+ |

This overview covers loading, the entry points, multi-database, repository CRUD, configuration, health, and events. The deep material lives on two sub-pages:

- **[Query Builder](queries.md)** — fluent `Where` / ordering / paging / joins / projections / `GroupBy` aggregates / terminals / bulk update & delete / raw SQL / transactions.
- **[Schema & Sync](schema.md)** — entity attributes, the `DataType` enum, table / set / namespace sync, `SyncResult`, schema backups, and the migration tracker.

## Install & load

```bash
dotnet add package CodeLogic.PostgreSQL
```

```csharp
using CL.PostgreSQL;

await Libraries.LoadAsync<PostgreSQLLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var pg = Libraries.Get<PostgreSQLLibrary>();
```

Set your connection in `config.postgresql.json` (auto-generated on first run) before `ConfigureAsync()`.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.PostgreSQL.Models`. The full attribute set is documented on the [Schema & Sync](schema.md) page.

```csharp
using CL.PostgreSQL.Models;

[Table(Name = "users", Schema = "public")]
public class User
{
    [Column(Primary = true, AutoIncrement = true)] public int Id { get; set; }
    [Column(Size = 120, Unique = true)]            public string Email { get; set; } = "";
    [Column(Size = 80, Index = true, NotNull = true)] public string Name { get; set; } = "";
    [Column] public int LoginCount { get; set; }
    [Column] public bool IsActive { get; set; }
}
```

Reconcile it to the database once, at startup:

```csharp
Result<SyncResult> sync = await pg.SyncTableAsync<User>();   // createBackup: true by default
```

## Repository basics

`GetRepository<T>()` returns a `Repository<T>` covering common CRUD, paging, count, find, raw SQL, and atomic increment/decrement. Almost everything returns `Result<…>`.

```csharp
var repo = pg.GetRepository<User>();

// Create
Result<User> created = await repo.InsertAsync(new User { Email = "ada@example.com", Name = "Ada" });
Result<int>  many    = await repo.InsertManyAsync(batch);

// Read
Result<User?>      byId  = await repo.GetByIdAsync(1);
Result<List<User>> byCol = await repo.GetByColumnAsync(nameof(User.Name), "Ada");
Result<List<User>> all   = await repo.GetAllAsync();
Result<List<User>> found = await repo.FindAsync(u => u.LoginCount > 10);
Result<long>       count = await repo.CountAsync();

// Paged
Result<PagedResult<User>> page =
    await repo.GetPagedAsync(page: 1, pageSize: 25, orderByColumn: nameof(User.Name), descending: false);

// Update / delete
Result<User> updated = await repo.UpdateAsync(created.Value!);   // by primary key, RETURNING *
Result<bool> deleted = await repo.DeleteAsync(1);

// Atomic counter adjustments (single UPDATE … SET col = col ± delta)
Result<int> inc = await repo.IncrementAsync(1, u => u.LoginCount, 1);
Result<int> dec = await repo.DecrementAsync(1, u => u.LoginCount, 1);

// Raw SQL materialized into T / executed as a non-query
Result<List<User>> raw = await repo.RawQueryAsync(
    "SELECT * FROM \"users\" WHERE \"Name\" = @n", new() { ["@n"] = "Ada" });
Result<int> affected = await repo.RawExecuteAsync("UPDATE \"users\" SET \"IsActive\" = false");
```

A primary key (`[Column(Primary = true)]`) is required for `GetByIdAsync`, `UpdateAsync`, `DeleteAsync`, and the increment/decrement helpers. `InsertAsync` / `UpdateAsync` issue `INSERT … RETURNING *` so the returned entity reflects database-generated values.

Both the repository's `CountAsync` and the query builder's `CountAsync` return `Result<long>` — see [Query Builder](queries.md) for filtered counts.

`PagedResult<T>` carries `Items`, `PageNumber`, `PageSize`, `TotalItems`, `TotalPages`, `HasPreviousPage`, and `HasNextPage`.

## Entry points

Everything flows through a handful of methods on the library.

| Member | Returns | Purpose |
|--------|---------|---------|
| `GetRepository<T>(connectionId = "Default")` | `Repository<T>` | CRUD / paging / find / raw / increment. |
| `Query<T>(connectionId = "Default")` | `QueryBuilder<T>` | Fluent LINQ-to-SQL queries. See [Query Builder](queries.md). |
| `QueryRaw(connectionId = "Default")` | `QueryBuilder` | Parameterized raw SQL (dictionary rows). See [Query Builder](queries.md). |
| `BeginTransactionAsync(connectionId, ct)` | `Task<TransactionScope>` | Explicit transaction (`IAsyncDisposable`, auto-rollback). |
| `SyncTableAsync<T>(createBackup = true, connectionId = "Default")` | `Task<Result<SyncResult>>` | Reconcile one table. See [Schema & Sync](schema.md). |
| `RegisterDatabase(connectionId, config)` | `void` | Add a named database at runtime. |

Library properties expose the underlying machinery for advanced use: `ConnectionManager`, `TableSync`, `BackupManager`, and `MigrationTracker`.

## Multi-database

Every entry point takes an optional `connectionId` (default `"Default"`) that selects one of the databases configured under `Databases`. Add more keys in `config.postgresql.json` and pass the key:

```csharp
var reportRepo = pg.GetRepository<Sale>("Reporting");
var query      = pg.Query<Sale>("Reporting").Where(s => s.Year == 2026);
```

On the query builder, `.WithConnection("Reporting")` does the same fluently. You can also register a database at runtime:

```csharp
pg.RegisterDatabase("Reporting", new DatabaseConfig
{
    Host = "reports.internal", Database = "analytics",
    Username = "reader", Password = "***"
});
```

## Configuration

`config.postgresql.json` (section `postgresql`) holds a `Databases` dictionary keyed by connection id — `Default` is created automatically with the defaults below.

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

| Setting | Type | Default | Notes |
|---------|------|---------|-------|
| `Enabled` | `bool` | `true` | Per-database switch; disabled databases are skipped at startup. |
| `Host` / `Port` | `string` / `int` | `localhost` / `5432` | Server endpoint. |
| `Database` / `Username` / `Password` | `string` | `""` | Connection credentials. |
| `ConnectionTimeout` | `int` | `30` | Seconds to wait when opening a connection. |
| `CommandTimeout` | `int` | `30` | Seconds before a command times out. |
| `MinPoolSize` / `MaxPoolSize` | `int` | `5` / `100` | Connection-pool bounds. |
| `MaxIdleTime` | `int` | `60` | Seconds an idle pooled connection is kept before being closed. |
| `SslMode` | `string` | `Prefer` | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, or `VerifyFull`. |
| `AllowDestructiveSync` | `bool` | `false` | Dev-only; allows DROP operations during schema sync. See [Schema & Sync](schema.md). |
| `SlowQueryThresholdMs` | `int` | `1000` | Queries at or above this duration raise a `SlowQueryEvent`. |

## Health check

```csharp
HealthStatus status = await pg.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

`HealthCheckAsync` tests every configured connection and aggregates the result; when no databases are enabled it reports *disabled* rather than failing.

## Events

All events implement `IEvent` (namespace `CL.PostgreSQL.Events`) and publish to the CodeLogic event bus.

| Event | Published when |
|-------|----------------|
| `DatabaseConnectedEvent` | A database connection is established. |
| `DatabaseDisconnectedEvent` | A connection is closed or lost. |
| `TableSyncedEvent` | A table is reconciled by schema sync. |
| `SlowQueryEvent` | A query exceeds `SlowQueryThresholdMs`. |
| `HealthChangedEvent` | The health status transitions. |

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.PostgreSQL)
