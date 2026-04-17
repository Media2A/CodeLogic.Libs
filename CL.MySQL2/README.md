# CodeLogic.MySQL2

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.MySQL2)](https://www.nuget.org/packages/CodeLogic.MySQL2)

MySQL database library for [CodeLogic 3](https://github.com/Media2A/CodeLogic) with a LINQ-style query builder, automatic table sync, migrations, and built-in result caching. Built on [MySqlConnector](https://mysqlconnector.net/).

## Install

```bash
dotnet add package CodeLogic.MySQL2
```

## Quick Start

```csharp
// Load the library at startup
await Libraries.LoadAsync<MySQL2Library>();

// Get a repository for your entity
var mysql = Libraries.Get<MySQL2Library>();
var repo = mysql.GetRepository<UserRecord>("Default");

// Insert
await repo.InsertAsync(new UserRecord { Name = "Alice", Email = "alice@example.com" });

// Query with the LINQ-style builder
var activeAdmins = await mysql.Query<UserRecord>("Default")
    .Where(u => u.IsActive && u.Role == "admin")
    .OrderBy(u => u.Name)
    .WithCache(TimeSpan.FromMinutes(5))   // optional result caching
    .ToPagedListAsync(page: 1, pageSize: 20);
```

## Features

- **Query Builder** — fluent `.Where()`, `.OrderBy()`, `.Take()`, `.Skip()`, `.Join()`, `.GroupBy()`, `.Select()` with full expression translation to SQL
- **Expression Support** — equality, comparison (`>= <= > <`), `&& ||`, null checks, `String.Contains/StartsWith/EndsWith`, `Enumerable.Contains` (IN clause)
- **Paged Queries** — `.ToPagedListAsync(page, size)` runs a single `COUNT(*)` + `SELECT` and returns `PagedResult<T>` with `Items`, `TotalItems`, `TotalPages`, `HasNextPage`, `HasPreviousPage`
- **Result Caching** — `.WithCache(TimeSpan)` enables opt-in, TTL-based caching with automatic table-level invalidation on write
- **Table Sync** — `SyncTableAsync<T>()` creates or alters MySQL tables to match your C# record classes (uses `[Table]` and `[Column]` attributes)
- **Repository Pattern** — `GetRepository<T>()` gives you `InsertAsync`, `UpdateAsync`, `DeleteAsync`, `GetByIdAsync`, `GetByColumnAsync`, `FindAsync`
- **Bulk Operations** — `InsertManyAsync`, `IncrementAsync`, `DecrementAsync`
- **Connection Pooling** — manages connections via named connection IDs (supports multiple databases)
- **Health Checks** — `HealthCheckAsync()` pings the database and reports latency

## Entity Definition

```csharp
using CL.MySQL2.Models;

[Table(Name = "users")]
public class UserRecord
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "name", DataType = DataType.VarChar, Size = 128, NotNull = true)]
    public string Name { get; set; } = "";

    [Column(Name = "email", DataType = DataType.VarChar, Size = 256, NotNull = true, Unique = true)]
    public string Email { get; set; } = "";

    [Column(Name = "is_active", DataType = DataType.TinyInt, NotNull = true)]
    public bool IsActive { get; set; } = true;

    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime CreatedUtc { get; set; }
}
```

## Configuration

Auto-generated at `data/codelogic/Libraries/CL.MySQL2/config.mysql2.json`:

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "host": "localhost",
      "port": 3306,
      "database": "myapp",
      "username": "root",
      "password": "",
      "charset": "utf8mb4",
      "sslMode": "preferred",
      "connectionTimeout": 30,
      "poolSize": 10
    }
  ]
}
```

## Query Cache

```csharp
// Opt-in per query — cached results auto-invalidate on any write to the same table
var items = await mysql.Query<ProductRecord>()
    .Where(p => p.IsActive)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToListAsync();

// Manual invalidation
QueryCache.Invalidate<ProductRecord>();
QueryCache.Clear(); // nuclear option

// Generic cache helper (usable outside MySQL queries)
var data = await QueryCache.GetOrSetAsync("my-key", "my-table",
    () => ExpensiveComputation(), TimeSpan.FromMinutes(10));
```

## Documentation

- [Database Libraries Guide](../docs/articles/database-libraries.md)
- [API Reference](../docs-output/api/CL.MySQL2.html)

## Requirements

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic)
- .NET 10
- MySQL 5.7+ / MariaDB 10.2+ / Percona 8.x

## License

MIT — see [LICENSE](../LICENSE)
