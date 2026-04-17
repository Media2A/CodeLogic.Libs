# CodeLogic.SQLite

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SQLite)](https://www.nuget.org/packages/CodeLogic.SQLite)

SQLite database library for [CodeLogic 3](https://github.com/Media2A/CodeLogic) with connection pooling, LINQ query builder, and automatic table sync. Built on [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/).

## Install

```bash
dotnet add package CodeLogic.SQLite
```

## Quick Start

```csharp
await Libraries.LoadAsync<SQLiteLibrary>();

var sqlite = Libraries.Get<SQLiteLibrary>();
var repo = sqlite.GetRepository<NoteRecord>("Default");

await repo.InsertAsync(new NoteRecord { Title = "Hello", Body = "World" });

var notes = await sqlite.Query<NoteRecord>("Default")
    .Where(n => n.Title.Contains("Hello"))
    .OrderByDescending(n => n.CreatedUtc)
    .ToListAsync();
```

## Features

- **LINQ Query Builder** — same fluent API as CodeLogic.MySQL2 (Where, OrderBy, Take, Skip, Join, GroupBy)
- **Table Sync** — creates or alters SQLite tables to match C# record classes
- **Connection Pooling** — manages named database connections
- **Repository Pattern** — Insert, Update, Delete, GetById, GetByColumn, Find
- **Health Checks** — verifies database file accessibility

## Configuration

Auto-generated at `data/codelogic/Libraries/CL.SQLite/config.sqlite.json`:

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "databasePath": "data/app/database.db",
      "journalMode": "wal",
      "poolSize": 5
    }
  ]
}
```

## Documentation

- [Database Libraries Guide](../docs/articles/database-libraries.md)

## Requirements

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](../LICENSE)
