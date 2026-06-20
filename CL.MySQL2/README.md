# CodeLogic.MySQL2

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.MySQL2)](https://www.nuget.org/packages/CodeLogic.MySQL2)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)

> A typed data-access layer for MySQL, MariaDB, and Percona on [CodeLogic 4](https://github.com/Media2A/CodeLogic) — repositories, a LINQ query builder, declarative schema sync, imperative migrations, and a self-invalidating result cache.

Built on [MySqlConnector](https://www.nuget.org/packages/MySqlConnector). Map a class with attributes and CL.MySQL2 keeps the table in shape, generates reflection-free row mappers, translates LINQ to real SQL, and caches results with version-stamped invalidation. Every fallible operation returns a `Result<T>` — no exceptions for the expected failure paths.

## Install

```bash
dotnet add package CodeLogic.MySQL2
```

## Quick start

```csharp
using CL.MySQL2;
using CL.MySQL2.Attributes;

[Table]
public class User
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 120, Index = true)]             public string Email { get; set; } = "";
    [Column] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

await Libraries.LoadAsync<MySQL2Library>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mysql = Libraries.Get<MySQL2Library>();

// Reconcile the table to the model.
await mysql.SyncSchemaAsync(typeof(User));

// Repository CRUD.
var repo = mysql.GetRepository<User>();
Result<User> created = await repo.InsertAsync(new User { Email = "ada@example.com" });

// LINQ query builder -> real SQL.
Result<List<User>> recent = await mysql.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(u => u.CreatedUtc)
    .Take(20)
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();

if (recent.IsSuccess)
    foreach (var u in recent.Value!) { /* … */ }
```

## Features

- **Repositories** — `GetRepository<T>()` for CRUD, paging, counts, upserts, atomic increments, and soft/hard deletes.
- **LINQ query builder** — `Query<T>()` translates `Where` / `OrderBy` / paging / typed joins / `GroupBy` aggregates / projections to real SQL with compiled materializers.
- **Subquery filters** — `WhereExists` / `WhereNotExists` / `WhereIn` / `WhereNotIn` compile to correlated SQL subqueries.
- **Declarative schema sync** — attribute-mapped entities reconciled to live tables with three operator-facing `SyncMode`s and a CRC fast-path that skips unchanged tables.
- **Imperative migrations** — `IMigration` with versioned up/down, run in order under a cross-node lock, with rollback and schema backups.
- **Self-invalidating cache** — `.WithCache(ttl)` with per-table version stamping, DateTime quantization, `SmartCachePool` warm pools, and pluggable multi-node coordination.
- **Resilience & observability** — transient deadlock/lock-wait retry, N+1 detection, slow-query capture with `EXPLAIN`, and a CodeLogic event-bus feed.
- **Soft deletes & retention** — `[SoftDelete]` auto-filters reads; `[RetainDays]` purges aged rows in the background.

## Configuration

Two files are auto-generated on first run: `config.mysql.json` (section `mysql`) and `config.mysql.cache.json` (section `mysql.cache`).

```json
{
  "Databases": {
    "Default": {
      "Enabled": true,
      "Host": "localhost",
      "Port": 3306,
      "Database": "myapp",
      "Username": "app",
      "Password": "",
      "SyncMode": "Production",
      "SchemaSyncLevel": "Safe",
      "MaxPoolSize": 100,
      "SlowQueryThresholdMs": 1000,
      "TransientRetryCount": 3
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Per-database master switch. |
| `Host` / `Port` | `localhost` / `3306` | Server endpoint. |
| `Database` / `Username` / `Password` | `""` | Connection credentials. |
| `SyncMode` | `Production` | `Developer` (drops on boot) · `Production` (additive, defers drops) · `Migration` (one-shot destructive reconcile). |
| `SchemaSyncLevel` | `Safe` | Low-level cap: `None` · `Safe` · `Additive` · `Full`. |
| `MaxPoolSize` | `100` | Connection pool ceiling. |
| `SlowQueryThresholdMs` | `1000` | Queries slower than this raise `SlowQueryEvent`. |
| `TransientRetryCount` | `3` | Deadlock / lock-wait retry attempts (0 disables). |

The cache file (`config.mysql.cache.json`) controls the result cache: `Enabled` (`true`), `MaxEntries` (`10000`), `MaxMemoryMb` (`256`), `DefaultTtlSeconds` (`60`), `TimeQuantizeSeconds` (`60`), `PublishEvents` (`true`).

## Documentation

Full guide: **[CL.MySQL2 documentation](https://media2a.github.io/CodeLogic.Libs/libs/mysql2/index.html)**

- [Querying](https://media2a.github.io/CodeLogic.Libs/libs/mysql2/queries.html) — the query builder, joins, projections, aggregates, transactions, raw SQL.
- [Schema & migrations](https://media2a.github.io/CodeLogic.Libs/libs/mysql2/schema-migrations.html) — entity attributes, sync modes, migrations, backups.
- [Performance & caching](https://media2a.github.io/CodeLogic.Libs/libs/mysql2/performance.html) — result cache, smart pools, multi-node, retries, diagnostics.

## Requirements

- [CodeLogic 4](https://github.com/Media2A/CodeLogic) · .NET 10
- MySqlConnector 2.x
- MySQL 5.7+ / MariaDB 10.3+ / Percona

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE).
