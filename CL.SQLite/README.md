# CodeLogic.SQLite

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SQLite)](https://www.nuget.org/packages/CodeLogic.SQLite)

SQLite database library for [CodeLogic](https://github.com/Media2A/CodeLogic) with connection pooling, a fluent LINQ-shaped query builder, attribute-driven table sync, and migration tracking. Built on [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/).

## Install

```bash
dotnet add package CodeLogic.SQLite
```

## Quick start

```csharp
await Libraries.LoadAsync<SQLiteLibrary>();

var sqlite = Libraries.Get<SQLiteLibrary>();

// 1. Sync the table from the entity (CREATE / ALTER to match the class)
await sqlite.TableSync.SyncTableAsync<NoteRecord>("Default");

// 2. CRUD via the repository — every call returns a Result<T>
var repo = sqlite.GetRepository<NoteRecord>("Default");
var insert = await repo.InsertAsync(new NoteRecord { Title = "Hello", Body = "World" });
if (insert.IsSuccess)
    Console.WriteLine($"new rowid = {insert.Value}");

// 3. Fluent queries via the query builder
var notes = await sqlite.GetQueryBuilder<NoteRecord>("Default")
    .Where(n => n.Title.Contains("Hello"))
    .OrderByDescending(n => n.CreatedUtc)
    .Take(20)
    .ToListAsync();

foreach (var note in notes.Value)
    Console.WriteLine(note.Title);
```

The entity is plain C# annotated with `[SQLiteTable]` / `[SQLiteColumn]`:

```csharp
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

> Only properties marked with `[SQLiteColumn]` are mapped — unannotated properties are ignored for schema, reads, and writes. The connection id defaults to `"Default"` on every entry point, so `GetRepository<NoteRecord>()` is equivalent to `GetRepository<NoteRecord>("Default")`.

## What's in the box

### Library entry points

| Member | Purpose |
|---|---|
| `GetRepository<T>(connectionId = "Default")` | CRUD + raw SQL for an entity |
| `GetQueryBuilder<T>(connectionId = "Default")` | fluent query builder |
| `TableSync` | `SyncTableAsync<T>`, `SyncTablesAsync`, `SyncNamespaceAsync` |
| `MigrationTracker` | inspect applied schema migrations |
| `ConnectionManager` | named connection pool (active/pooled counts, test) |
| `HealthCheckAsync()` | per-database connectivity check (Healthy / Degraded / Unhealthy) |

All data operations return CodeLogic `Result` / `Result<T>` — check `.IsSuccess` and read `.Value`, or inspect `.Error`. They do not throw on query failure.

### Repository

`GetRepository<T>()` exposes:

| Method | Result |
|---|---|
| `InsertAsync(entity)` | `Result<long>` — new rowid; auto-increment PK is written back to the entity |
| `UpsertAsync(entity)` | `INSERT OR REPLACE` |
| `UpdateAsync(entity)` | update by primary key |
| `DeleteAsync(id)` | delete by single PK |
| `GetByIdAsync(id)` | `Result<T?>` |
| `GetByKeysAsync(ct, params keys)` | composite-PK lookup |
| `DeleteByKeysAsync(ct, params keys)` | composite-PK delete |
| `GetAllAsync(limit = 1000)` | `Result<List<T>>` |
| `FindAsync(predicate)` | `Result<List<T>>` from a LINQ `WHERE` |
| `CountAsync()` | `Result<long>` |
| `GetPagedAsync(page, pageSize, orderBy?, desc?)` | `Result<PagedResult<T>>` |
| `RawQueryAsync(sql, params?)` | `Result<List<T>>` — raw SELECT mapped to entities |
| `RawExecuteAsync(sql, params?)` | `Result<int>` — raw non-query, rows affected |

```csharp
var repo = sqlite.GetRepository<NoteRecord>();

var page  = await repo.GetPagedAsync(page: 1, pageSize: 20, orderBy: "created_utc", desc: true);
var byTag = await repo.FindAsync(n => n.Title.StartsWith("draft"));
var raw   = await repo.RawQueryAsync(
    "SELECT * FROM notes WHERE title LIKE @q",
    new() { ["@q"] = "%hello%" });
```

Always bind values via named parameters — never interpolate user input into the SQL.

### Fluent query builder

`GetQueryBuilder<T>()` chains LINQ-shaped clauses and translates them to SQL.

| Capability | Shape |
|---|---|
| Filter | `.Where(x => x.Status == "active" && x.Age >= 18)` (multiple calls AND together) |
| Sort | `.OrderBy`, `.OrderByDescending`, `.ThenBy`, `.ThenByDescending` |
| Paging | `.Limit` / `.Take`, `.Offset` / `.Skip`, `.ToPagedListAsync` |
| Projection | `.Select(x => new { x.Id, x.Title })` — restricts the SELECT column list |
| Grouping | `.GroupBy(x => x.Category)` |

Terminal operations (each returns a `Result`):

| Method | Result |
|---|---|
| `ToListAsync()` | `Result<List<T>>` |
| `FirstOrDefaultAsync()` | `Result<T?>` |
| `ToPagedListAsync(page, pageSize)` | `Result<PagedResult<T>>` |
| `CountAsync()` | `Result<long>` over the current WHERE |
| `SumAsync(x => x.Col)` / `MaxAsync` / `MinAsync` | `Result<TResult>` aggregate |
| `DeleteAsync()` | `Result<int>` — bulk delete by predicate |
| `UpdateAsync(Dictionary<string, object?>)` | `Result<int>` — bulk update by predicate |

```csharp
var qb = sqlite.GetQueryBuilder<NoteRecord>();

var total = await qb.Where(n => n.Title.Contains("hello")).CountAsync();

var page = await sqlite.GetQueryBuilder<NoteRecord>()
    .Where(n => n.CreatedUtc >= since)
    .OrderByDescending(n => n.CreatedUtc)
    .ToPagedListAsync(page: 1, pageSize: 20);

// Bulk predicate mutations
await sqlite.GetQueryBuilder<NoteRecord>()
    .Where(n => n.CreatedUtc < cutoff)
    .DeleteAsync();

await sqlite.GetQueryBuilder<NoteRecord>()
    .Where(n => n.Title == "")
    .UpdateAsync(new() { ["title"] = "(untitled)" });
```

### Attribute-driven schema sync

Entity classes are the source of truth. `TableSync.SyncTableAsync<T>()` creates or alters the SQLite table to match the class; `SyncTablesAsync` and `SyncNamespaceAsync` batch-sync many types.

| Attribute | Purpose |
|---|---|
| `[SQLiteTable("name")]` | table name (defaults to the class name) |
| `[SQLiteColumn]` | `ColumnName`, `DataType`, `Size`, `IsPrimaryKey`, `IsAutoIncrement`, `IsIndexed`, `IsUnique`, `IsNotNull`, `DefaultValue` |
| `[SQLiteIndex(cols...)]` | class-level named index; `IsUnique`, `Name` |
| `[SQLiteForeignKey(table, column)]` | FK with `OnDelete` / `OnUpdate` (`ForeignKeyAction`: `NoAction`, `Restrict`, `SetNull`, `SetDefault`, `Cascade`) |

`SQLiteDataType` values: `INTEGER`, `REAL`, `TEXT`, `BLOB`, `NUMERIC`, `DATETIME`, `DATE`, `BOOLEAN`, `UUID`. When omitted the type is inferred from the property type. `bool`, `DateTime`, `DateTimeOffset`, `Guid`, and `enum` are converted automatically on read and write.

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

### Observability

Queries are logged when the runtime is in development mode, and any query that runs longer than the per-database `SlowQueryThresholdMs` is logged as a warning.

## Configuration

Auto-generated on first run under `data/codelogic/Libraries/CL.SQLite/config.sqlite.json`. The config is a map of named databases — each key is a connection id you pass to `GetRepository` / `GetQueryBuilder` / `SyncTableAsync`.

```json
{
  "databases": {
    "Default": {
      "enabled": true,
      "databasePath": "database.db",
      "connectionTimeoutSeconds": 30,
      "commandTimeoutSeconds": 120,
      "skipTableSync": false,
      "cacheMode": "default",
      "useWAL": true,
      "enableForeignKeys": true,
      "maxPoolSize": 10,
      "slowQueryThresholdMs": 500
    }
  }
}
```

| Field | Default | Purpose |
|---|---|---|
| `enabled` | `true` | disable a database without removing it |
| `databasePath` | `database.db` | absolute, or relative to the library data directory |
| `connectionTimeoutSeconds` | `30` | connection open timeout |
| `commandTimeoutSeconds` | `120` | per-command timeout |
| `skipTableSync` | `false` | turn off automatic schema sync for this database |
| `cacheMode` | `default` | `default` / `private` / `shared` |
| `useWAL` | `true` | Write-Ahead Logging — better concurrency, recommended |
| `enableForeignKeys` | `true` | enforce FK constraints |
| `maxPoolSize` | `10` | max pooled connections |
| `slowQueryThresholdMs` | `500` | slow-query warning threshold |

A database with `enabled: false` is skipped at startup; if no database is enabled the library initializes in a disabled state and the health check reports healthy-but-disabled.

## Documentation

- [Database Libraries Guide](../docs/articles/database-libraries.md)

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
