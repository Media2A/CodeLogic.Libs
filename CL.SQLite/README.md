# CodeLogic.SQLite

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SQLite)](https://www.nuget.org/packages/CodeLogic.SQLite)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)

> An embedded SQLite data-access layer for [CodeLogic 4](https://github.com/Media2A/CodeLogic) — connection pooling, WAL, attribute-driven table sync, a repository, and a fluent LINQ-shaped query builder.

Map a plain class with attributes and the library keeps the live table in shape, then read and write through a `Repository<T>` or a fluent `QueryBuilder<T>`. It builds on [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/), pools connections per database, and enables Write-Ahead Logging by default. Every fallible operation returns a `Result` / `Result<T>` — no exceptions for the expected failure paths.

## Install

```bash
dotnet add package CodeLogic.SQLite
```

## Quick start

```csharp
using CL.SQLite;

await Libraries.LoadAsync<SQLiteLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var db = Libraries.Get<SQLiteLibrary>();

// 1. Reconcile the table from the entity (CREATE / ALTER to match the class)
await db.TableSync.SyncTableAsync<NoteRecord>();

// 2. CRUD via the repository — every call returns a Result
var repo = db.GetRepository<NoteRecord>();
var insert = await repo.InsertAsync(new NoteRecord { Title = "Hello", Body = "World" });
if (insert.IsSuccess)
    Console.WriteLine($"new rowid = {insert.Value}");

// 3. Fluent queries via the query builder
var notes = await db.GetQueryBuilder<NoteRecord>()
    .Where(n => n.Title.Contains("Hello"))
    .OrderByDescending(n => n.CreatedUtc)
    .Take(20)
    .ToListAsync();

if (notes.IsSuccess)
    foreach (var note in notes.Value!)
        Console.WriteLine(note.Title);
```

The entity is plain C# annotated with `[SQLiteTable]` / `[SQLiteColumn]`:

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

Only properties marked with `[SQLiteColumn]` are mapped. The connection id defaults to `"Default"` on every entry point, so `GetRepository<NoteRecord>()` equals `GetRepository<NoteRecord>("Default")`.

## Features

- **Named connection pools** — a map of databases keyed by connection id, each with its own per-database pool (default `MaxPoolSize` 10) and a 5-minute idle timeout.
- **WAL by default** — `journal_mode=WAL` is set on every connection for better read/write concurrency.
- **Repository CRUD** — insert / upsert / update / delete, by-id and composite-key lookups, paging, LINQ `Find`, count, and raw SQL — all returning `Result`.
- **Fluent query builder** — `Where` / `OrderBy` / `ThenBy` / `Select` / `GroupBy`, aggregates, paging, and bulk predicate update / delete translated to SQL.
- **Attribute-driven schema sync** — `TableSync` creates tables, adds missing columns, and builds indexes to match the entity class; batch-sync by type set or namespace.
- **Migration ledger** — `MigrationTracker` records and inspects applied migration ids in a JSON history file.
- **Type conversion** — `bool`, `DateTime`, `DateTimeOffset`, `Guid`, and `enum` are converted automatically on read and write.

## Configuration

Auto-generated on first run as `config.sqlite.json` (section `sqlite`). The config is a `Databases` map — each key is a connection id you pass to the entry points; `Default` is created automatically.

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

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Disable a database without removing it. |
| `databasePath` | `database.db` | Absolute, or relative to the library data directory. |
| `connectionTimeoutSeconds` | `30` | Connection open timeout. |
| `commandTimeoutSeconds` | `120` | Per-command timeout. |
| `skipTableSync` | `false` | Turn off automatic schema sync for this database. |
| `cacheMode` | `Default` | `Default` / `Private` / `Shared`. |
| `useWAL` | `true` | Write-Ahead Logging — better concurrency, recommended. |
| `enableForeignKeys` | `true` | Enforce foreign-key constraints. |
| `maxPoolSize` | `10` | Maximum pooled connections per database. |
| `slowQueryThresholdMs` | `500` | Slow-query warning threshold. |

A database with `enabled: false` is skipped at startup; if no database is enabled the library initializes disabled and the health check reports healthy-but-disabled.

## Documentation

Full guide: **[CL.SQLite documentation](https://media2a.github.io/CodeLogic.Libs/libs/sqlite/index.html)**

## Requirements

- [CodeLogic 4](https://github.com/Media2A/CodeLogic) · .NET 10
- Microsoft.Data.Sqlite 9.x

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE).
