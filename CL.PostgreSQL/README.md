# CodeLogic.PostgreSQL

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.PostgreSQL)](https://www.nuget.org/packages/CodeLogic.PostgreSQL)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)

> A typed PostgreSQL data-access layer for [CodeLogic 4](https://github.com/Media2A/CodeLogic) — multi-database connections, an attribute-driven repository, a fluent LINQ query builder, transactions, and declarative schema sync with backups and migration tracking.

Map a plain class with attributes and the library reconciles the live table to match, then exposes a typed `Repository<T>` and a chainable `QueryBuilder<T>` over it. It builds on [Npgsql](https://www.nuget.org/packages/Npgsql) and connects to one or many PostgreSQL instances from a single config. Every fallible operation returns a framework `Result<T>` — no exceptions on the expected failure paths.

## Install

```bash
dotnet add package CodeLogic.PostgreSQL
```

## Quick start

```csharp
using CL.PostgreSQL;
using CL.PostgreSQL.Models;

await Libraries.LoadAsync<PostgreSQLLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var pg = Libraries.Get<PostgreSQLLibrary>();

// Define an entity
[Table(Name = "users", Schema = "public")]
public class User
{
    [Column(Primary = true, AutoIncrement = true)] public int Id { get; set; }
    [Column(NotNull = true)]                        public string Name { get; set; } = "";
    [Column] public bool IsActive { get; set; }
}

// 1. Reconcile the table to match the entity (creates it, or adds missing columns/indexes)
Result<SyncResult> sync = await pg.SyncTableAsync<User>();

// 2. Typed repository CRUD
var repo = pg.GetRepository<User>();
Result<User> created = await repo.InsertAsync(new User { Name = "Ada", IsActive = true });

// 3. Fluent query builder
Result<List<User>> users = await pg.Query<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.Name)
    .Limit(50)
    .ToListAsync();
```

## Features

- **Multi-database** — manage connections to several PostgreSQL instances from one config; pick the target per call with a `connectionId` (default `"Default"`), or `RegisterDatabase` one at runtime.
- **Repository pattern** — `GetRepository<T>()` for full CRUD plus bulk insert, paging, find, raw SQL, and atomic increment/decrement.
- **Fluent query builder** — `Query<T>()` with `Where`, `OrderBy`/`OrderByDescending`, `Limit`/`Offset` (aliases `Take`/`Skip`), `Join`, `Select`, `GroupBy`, aggregates, paging, and bulk update/delete.
- **Attribute-driven schema** — `[Table]`, `[Column]`, `[ForeignKey]`, `[CompositeIndex]`, `[Ignore]` map a plain class to a real table.
- **Schema sync & migrations** — create or alter tables to match entities (single, set, or whole namespace), with timestamped schema backups and a JSON migration history.
- **Transactions** — `BeginTransactionAsync()` returns an `await using` scope that auto-rolls-back if it is never committed.
- **Health checks & events** — `HealthCheckAsync()` plus events for connect/disconnect, table sync, slow query, and health changes.

## Configuration

Auto-generated on first run as `config.postgresql.json` (section `postgresql`). `Databases` is a named map keyed by connection id; `Default` is created automatically.

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

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Per-database switch; disabled databases are skipped at startup. |
| `Host` / `Port` | `localhost` / `5432` | Server endpoint. |
| `Database` / `Username` / `Password` | `""` | Connection credentials. |
| `ConnectionTimeout` | `30` | Seconds to wait when opening a connection. |
| `CommandTimeout` | `30` | Seconds before a command times out. |
| `MinPoolSize` / `MaxPoolSize` | `5` / `100` | Connection-pool bounds. |
| `MaxIdleTime` | `60` | Seconds an idle pooled connection is kept before being closed. |
| `SslMode` | `Prefer` | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, or `VerifyFull`. |
| `AllowDestructiveSync` | `false` | Dev-only; allows DROP operations during schema sync. |
| `SlowQueryThresholdMs` | `1000` | Queries at or above this duration raise a `SlowQueryEvent`. |

## Documentation

Full guide: **[CL.PostgreSQL documentation](https://media2a.github.io/CodeLogic.Libs/libs/postgresql/index.html)**

- [Overview](https://media2a.github.io/CodeLogic.Libs/libs/postgresql/index.html) — load, multi-database, repository CRUD, config, health, events.
- [Query Builder](https://media2a.github.io/CodeLogic.Libs/libs/postgresql/queries.html) — fluent methods, terminals, aggregates, bulk writes, raw SQL, transactions.
- [Schema & Sync](https://media2a.github.io/CodeLogic.Libs/libs/postgresql/schema.html) — entity attributes, table & namespace sync, backups, migration tracker.

## Requirements

- [CodeLogic 4](https://github.com/Media2A/CodeLogic) · .NET 10
- Npgsql 9.x · PostgreSQL 12+

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE).
