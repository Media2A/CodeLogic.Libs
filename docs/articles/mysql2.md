# CL.MySQL2 — Overview & Quick Start

CL.MySQL2 is a typed, attribute-driven data access library for MySQL, MariaDB,
and Percona. It replaces hand-rolled SQL with strongly-typed LINQ-shape
expressions, compiles fast row materializers so you don't pay reflection per
row, and ships with a working result cache, SQL-side aggregation, and covering
indexes declared right on your record classes.

> **If you've used an ORM before, this will feel familiar. If you haven't —
> the mental model is: *describe your tables with attributes on records, write
> queries against those records, and CL.MySQL2 turns them into SQL.*** No
> magic strings, no `SELECT *` by accident, and the compiler catches your
> typos.

## The one-minute tour

```csharp
using CL.MySQL2;
using CL.MySQL2.Core;           // SqlFn, attribute helpers
using CL.MySQL2.Models;         // [Table], [Column], [Index], [RetainDays], …

// 1. Declare a table as a record. This is the source of truth.
[Table(Name = "users")]
public sealed class UserRecord
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "email", DataType = DataType.VarChar, Size = 256, NotNull = true, Unique = true)]
    public string Email { get; set; } = "";

    [Column(Name = "display_name", DataType = DataType.VarChar, Size = 128, NotNull = true)]
    public string DisplayName { get; set; } = "";

    [Column(Name = "is_active", DataType = DataType.TinyInt, NotNull = true)]
    public bool IsActive { get; set; } = true;

    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime CreatedUtc { get; set; }
}

// 2. Load the library and sync the table. CREATE/ALTER runs once, idempotent.
await Libraries.LoadAsync<MySQL2Library>();
var mysql = Libraries.Get<MySQL2Library>()!;
await mysql.SyncTableAsync<UserRecord>();

// 3. Write. Repositories cover the boring CRUD.
var repo = mysql.GetRepository<UserRecord>();
await repo.InsertAsync(new UserRecord
{
    Email = "alice@example.com",
    DisplayName = "Alice",
    CreatedUtc = DateTime.UtcNow
});

// 4. Read. The Query builder is for anything beyond GetById/GetAll.
var recentActive = await mysql.Query<UserRecord>()
    .Where(u => u.IsActive && u.CreatedUtc >= DateTime.UtcNow.AddDays(-30))
    .OrderByDescending(u => u.CreatedUtc)
    .Take(50)
    .ToListAsync();
```

That's it. Everything else in the docs is about doing *more* with the same shape.

---

## Installation

```bash
dotnet add package CodeLogic.MySQL2
```

Requirements:

- .NET 10
- [CodeLogic](https://github.com/Media2A/CodeLogic) 3.x or 4.x
- MySQL 5.7+ / MariaDB 10.2+ / Percona 8.x

## Loading

CL.MySQL2 is a CodeLogic library. Load it during your app's initialization pass:

```csharp
await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();
```

CodeLogic writes two config skeletons on first run under
`data/codelogic/Libraries/CL.MySQL2/`:

| File | Purpose |
|---|---|
| `config.mysql.json` | Connection + per-database knobs |
| `config.mysql.cache.json` | Process-wide cache settings |

---

## Configuration

### `config.mysql.json` (connections)

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
      "EnablePooling": true,
      "MinPoolSize": 1,
      "MaxPoolSize": 20,
      "CommandTimeout": 30,
      "SchemaSyncLevel": "Safe",
      "SlowQueryThresholdMs": 1000,
      "MaxBatchInsertSize": 500,
      "MaxInClauseValues": 1000,
      "CaptureExplainOnSlowQuery": true,
      "DefaultStringSize": 255
    },
    "reporting": {
      "Enabled": true,
      "Host": "replica.internal",
      "Port": 3306,
      "Database": "myapp",
      "Username": "ro_app",
      "Password": "",
      "SchemaSyncLevel": "None"
    }
  }
}
```

You can define any number of named databases. The key (`"Default"`, `"reporting"`)
is the *connection ID* you pass to `GetRepository<T>("reporting")` and
`Query<T>("reporting")`.

**Important fields:**

| Field | Default | What it controls |
|---|---|---|
| `SchemaSyncLevel` | `Safe` | How aggressive `SyncTableAsync` is. See [Schema docs](mysql2-schema.md). |
| `SlowQueryThresholdMs` | 1000 | Queries slower than this log a warning + fire `SlowQueryEvent`. |
| `MaxBatchInsertSize` | 500 | Rows per batched `INSERT` in `InsertManyAsync`. |
| `MaxInClauseValues` | 1000 | Safety cap on `IN (...)` parameter count. |
| `CaptureExplainOnSlowQuery` | true | Attaches `EXPLAIN FORMAT=JSON` to slow query events. |
| `DefaultStringSize` | 255 | VARCHAR length when a string column has no explicit `Size`. |
| `CacheEnabledOverride` | null (inherit) | Per-DB cache on/off — overrides the global switch. |
| `N1DetectorThreshold` | 0 (off) | Non-zero N warns if the same query fires N× in one request scope. |

### `config.mysql.cache.json` (cache)

```json
{
  "Enabled": true,
  "MaxEntries": 10000,
  "DefaultTtlSeconds": 60,
  "TimeQuantizeSeconds": 60,
  "PublishEvents": true
}
```

`TimeQuantizeSeconds` is the one most people tune — it controls how `DateTime`
parameters near "now" round when forming cache keys, so
`.Where(x => x.At >= UtcNow.AddDays(-30))` actually hits the cache across
back-to-back calls. See the [Performance doc](mysql2-performance.md#how-the-cache-key-works)
for the full story.

---

## Accessing the library

Inside CodeLogic, you typically hold a `MySQL2Library` reference resolved from
your application context:

```csharp
var mysql = Libraries.Get<MySQL2Library>()!;
```

Everything else hangs off that instance:

```csharp
Repository<T>       repo       = mysql.GetRepository<T>();
QueryBuilder<T>     query      = mysql.Query<T>();
ConnectionManager   conn       = mysql.ConnectionManager;
TableSyncService    sync       = mysql.TableSync;
BackupManager       backups    = mysql.BackupManager;
MigrationTracker    migrations = mysql.MigrationTracker;
TransactionScope    tx         = await mysql.BeginTransactionAsync();
```

---

## Entity records

Map a C# class to a MySQL table by decorating it with `[Table]` + per-property
`[Column]` attributes. Record classes are the natural fit but any class with
parameterless construction works.

```csharp
[Table(Name = "orders", Engine = TableEngine.InnoDB, Charset = Charset.Utf8mb4)]
public sealed class OrderRecord
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "user_id", DataType = DataType.BigInt, NotNull = true, Index = true)]
    public long UserId { get; set; }

    [Column(Name = "total_cents", DataType = DataType.Int, NotNull = true)]
    public int TotalCents { get; set; }

    [Column(Name = "status", DataType = DataType.VarChar, Size = 32, NotNull = true)]
    public string Status { get; set; } = "pending";

    [Column(Name = "placed_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime PlacedUtc { get; set; }

    [Column(Name = "shipped_utc", DataType = DataType.DateTime)]  // nullable in MySQL; nullable in CLR too
    public DateTime? ShippedUtc { get; set; }

    // Not persisted. Use this for computed/derived data.
    [Ignore]
    public bool IsShipped => ShippedUtc.HasValue;
}
```

Full attribute catalog (table, column, index, retention, FK, composite index,
ignore) lives in [Schema docs](mysql2-schema.md).

---

## Repository — routine CRUD

`Repository<T>` is the front door for row-at-a-time work. It hides parameters,
manages connections, and uses the compiled materializer under the hood.

```csharp
var orders = mysql.GetRepository<OrderRecord>();

// Create
var placed = await orders.InsertAsync(new OrderRecord
{
    UserId = userId,
    TotalCents = 4999,
    Status = "pending",
    PlacedUtc = DateTime.UtcNow
});
// placed.Id is populated from LAST_INSERT_ID().

// Read by primary key
var found = await orders.GetByIdAsync(placed.Value!.Id);

// Read by column
var alicesOrders = await orders.GetByColumnAsync("user_id", aliceId);

// Read all (use sparingly — this is an un-bounded SELECT *)
var everything = await orders.GetAllAsync();

// Paged read
var page = await orders.GetPagedAsync(page: 1, pageSize: 25,
                                      orderByColumn: "placed_utc",
                                      descending: true);

// Count
var total = await orders.CountAsync();

// Update (whole entity by PK)
placed.Value!.Status = "paid";
await orders.UpdateAsync(placed.Value);

// Delete by PK
await orders.DeleteAsync(placed.Value!.Id);

// Ad-hoc find by predicate (no LINQ chain needed)
var pendingOrders = await orders.FindAsync(o => o.Status == "pending");

// Atomic numeric adjustment — single UPDATE, no read-modify-write.
await orders.AdjustAsync(placed.Value!.Id, o => o.TotalCents, +100);
await orders.IncrementAsync(placed.Value!.Id, o => o.TotalCents, 100); // same thing
await orders.DecrementAsync(placed.Value!.Id, o => o.TotalCents, 50);
```

Every method returns `Result<T>` — check `IsSuccess` / `Error`.

### Bulk insert

```csharp
var many = Enumerable.Range(0, 50_000)
    .Select(i => new OrderRecord { /* ... */ })
    .ToList();

await orders.InsertManyAsync(many);   // real batched INSERT, chunked by MaxBatchInsertSize
```

`InsertManyAsync` emits a real `INSERT ... VALUES (...), (...), ...` per chunk —
not a `foreach InsertAsync`. 500-row batches by default, tunable per-DB via
`MaxBatchInsertSize`. One cache invalidation at the end, not per row.

---

## The Query builder

Anything beyond GetById/GetAll/FindAsync goes through `mysql.Query<T>()`. It's
a fluent chain that compiles to a single SQL statement on the terminal method.

```csharp
var recent = await mysql.Query<OrderRecord>()
    .Where(o => o.Status == "paid" && o.PlacedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(o => o.PlacedUtc)
    .Take(100)
    .WithCache(TimeSpan.FromMinutes(1))
    .ToListAsync();
```

Full coverage — filtering, projection, aggregation, `SqlFn`, joins, bulk
update/delete — is in [Query Builder docs](mysql2-queries.md).

---

## Transactions

```csharp
await using var tx = await mysql.BeginTransactionAsync();

var repo = mysql.GetRepository<OrderRecord>(tx);   // scoped to the transaction
await repo.InsertAsync(new OrderRecord { /* ... */ });
await repo.UpdateAsync(otherOrder);

await tx.CommitAsync();  // if you don't call Commit, DisposeAsync rolls back
```

The transaction scope is an `IAsyncDisposable` that auto-rolls-back when
disposed without a commit. Inside a transaction, `WithCache` is suppressed —
reads see uncommitted writes, which caching would hide.

---

## Multiple databases

Any named connection in `config.mysql.json` is addressable by its key:

```csharp
var readWrite = mysql.GetRepository<OrderRecord>();                 // "Default"
var reporting = mysql.GetRepository<OrderRecord>("reporting");       // replica
var longRunningReport = await mysql.Query<OrderRecord>("reporting")
    .Where(o => o.PlacedUtc >= since)
    .ToListAsync();
```

The read/write separation is a common pattern — point `Default` at primary,
`reporting` at a read replica.

---

## Health

```csharp
var health = await mysql.HealthCheckAsync();
Console.WriteLine($"{health.Status}: {health.Message}");
```

Pings every enabled connection in parallel. The `Data` dictionary carries
per-DB open connection counts and status. Wire this into whatever dashboard
CodeLogic's health system reports to.

---

## What's next

- **Querying more deeply** → [Query Builder](mysql2-queries.md)
- **Going fast** → [Performance & Caching](mysql2-performance.md)
- **Table design, indexes, retention** → [Schema & Migrations](mysql2-schema.md)
