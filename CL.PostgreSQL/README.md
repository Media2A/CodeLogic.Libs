# CL.PostgreSQL

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.PostgreSQL)](https://www.nuget.org/packages/CodeLogic.PostgreSQL)

PostgreSQL database access for CodeLogic 3 applications with multi-database support, a LINQ query builder, table sync, and migrations.

## Install

```
dotnet add package CodeLogic.PostgreSQL
```

## Quick Start

```csharp
var pgLib = new PostgreSQLLibrary();
// After library initialization via CodeLogic framework:

// Typed repository
var repo = pgLib.GetRepository<User>();

// Fluent query builder
var users = await pgLib.Query<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.Name)
    .ToListAsync();

// Transactions
await using var tx = await pgLib.BeginTransactionAsync();

// Table sync (schema migrations)
var result = await pgLib.SyncTableAsync<User>();
```

## Features

- **Multi-database support** -- manage connections to multiple PostgreSQL instances from one config
- **LINQ query builder** -- fluent `Query<T>()` API with `Where`, `OrderBy`, and typed results
- **Repository pattern** -- `GetRepository<T>()` for standard CRUD operations
- **Table sync and migrations** -- automatic schema synchronization with backup support
- **Connection pooling** -- configurable pool sizes, idle timeouts, and slow-query thresholds

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
      "SslMode": "Prefer",
      "SlowQueryThresholdMs": 1000
    }
  }
}
```

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- CodeLogic 3.0.0+
- Npgsql 9.x
## License

MIT -- see [LICENSE](../LICENSE) for details.
