# Database Libraries

CodeLogic Libraries provides three database libraries sharing a consistent `Repository<T>` and `QueryBuilder<T>` API. Use the one that matches your infrastructure — or mix them in the same application.

| Library | Database | Driver |
|---------|----------|--------|
| CL.MySQL2 | MySQL 5.7+ / MariaDB 10.5+ | MySqlConnector |
| CL.PostgreSQL | PostgreSQL 13+ | Npgsql |
| CL.SQLite | SQLite 3 | Microsoft.Data.Sqlite |

---

## CL.SQLite

SQLite is ideal for embedded storage, local caches, and development environments.

### Registration

```csharp
await Libraries.LoadAsync<CL.SQLite.SQLiteLibrary>();
```

### Configuration (`config.sqlite.json`)

```json
{
  "DatabasePath": "data/myapp.db",
  "MaxConnections": 10,
  "TimeoutSeconds": 30,
  "EnableWAL": true,
  "MigrationsPath": "migrations/"
}
```

### Entity Definition

```csharp
[Table("users")]
public class User
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Column("email"), Unique]
    public string Email { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
```

### Repository Pattern

```csharp
// In OnInitializeAsync:
var sqlite = context.GetLibrary<CL.SQLite.SQLiteLibrary>();
var userRepo = sqlite.GetRepository<User>();

// Create
var user = await userRepo.InsertAsync(new User
{
    Email = "alice@example.com",
    Name  = "Alice"
});

// Read
var alice = await userRepo.FindAsync(u => u.Email == "alice@example.com");

// Query
var activeUsers = await userRepo.QueryAsync(u => u.IsActive);

// Update
alice!.Name = "Alice Smith";
await userRepo.UpdateAsync(alice);

// Delete
await userRepo.DeleteAsync(u => u.Email == "old@example.com");

// Count
var total = await userRepo.CountAsync();
var active = await userRepo.CountAsync(u => u.IsActive);
```

### QueryBuilder

```csharp
var results = await sqlite.Query<User>()
    .Where(u => u.IsActive)
    .Where(u => u.CreatedAt > DateTime.UtcNow.AddDays(-30))
    .OrderBy(u => u.Name)
    .Limit(20)
    .Offset(0)
    .ToListAsync();
```

### Migrations

Place SQL files in the `migrations/` directory within the library's data directory:

```
CodeLogic/Libraries/CL.SQLite/data/migrations/
  001_create_users.sql
  002_add_roles.sql
  003_add_user_index.sql
```

Migrations run in numeric order on startup, tracking applied migrations in a `_migrations` table.

---

## CL.MySQL2

MySQL library with connection pooling, repository pattern, and table sync.

### Registration

```csharp
await Libraries.LoadAsync<CL.MySQL2.MySqlLibrary>();
```

### Configuration (`config.mysql.json`)

```json
{
  "ConnectionString": "Server=localhost;Port=3306;Database=myapp;Uid=app;Pwd=secret;",
  "MaxPoolSize": 20,
  "MinPoolSize": 2,
  "ConnectionTimeoutSeconds": 30,
  "CommandTimeoutSeconds": 60
}
```

### Repository Usage

Same `IRepository<T>` interface as CL.SQLite:

```csharp
var mysql = context.GetLibrary<CL.MySQL2.MySqlLibrary>();
var orderRepo = mysql.GetRepository<Order>();

var pendingOrders = await orderRepo.QueryAsync(o => o.Status == "Pending");

foreach (var order in pendingOrders)
{
    order.Status = "Processing";
    await orderRepo.UpdateAsync(order);
}
```

### Raw Queries

For complex SQL not covered by the query builder:

```csharp
var results = await mysql.QueryRawAsync<OrderSummary>(
    "SELECT customer_id, COUNT(*) as count, SUM(total) as total FROM orders WHERE status = @status GROUP BY customer_id",
    new { status = "Completed" }
);
```

### Table Sync

CL.MySQL2 can synchronize table schemas to match your entity definitions:

```csharp
await mysql.SyncTableAsync<User>();   // CREATE TABLE IF NOT EXISTS + ALTER TABLE for new columns
```

---

## CL.PostgreSQL

PostgreSQL library with multi-database support.

### Registration

```csharp
await Libraries.LoadAsync<CL.PostgreSQL.PostgreSqlLibrary>();
```

### Configuration (`config.postgresql.json`)

```json
{
  "Databases": {
    "main": "Host=localhost;Database=myapp;Username=app;Password=secret;",
    "reporting": "Host=replica.internal;Database=myapp_ro;Username=ro_app;Password=secret;"
  },
  "DefaultDatabase": "main",
  "MaxPoolSize": 20,
  "CommandTimeoutSeconds": 30
}
```

### Multi-Database Usage

```csharp
var pg = context.GetLibrary<CL.PostgreSQL.PostgreSqlLibrary>();

// Default database
var userRepo = pg.GetRepository<User>();

// Specific database
var reportRepo = pg.GetRepository<ReportRow>("reporting");

// Query from reporting replica
var monthlyStats = await reportRepo.QueryAsync(r => r.Month == DateTime.UtcNow.Month);
```

### Migrations

PostgreSQL migrations work identically to SQLite migrations — numbered SQL files in the library's `migrations/` folder, applied in order.

---

## Shared Interface Summary

All three database libraries implement the same interfaces:

```csharp
// Available on all three:
IRepository<T> GetRepository<T>() where T : class, new();
IQueryBuilder<T> Query<T>() where T : class, new();
Task<IEnumerable<T>> QueryRawAsync<T>(string sql, object? parameters = null);
Task ExecuteAsync(string sql, object? parameters = null);
Task<HealthStatus> HealthCheckAsync();
```

This means you can swap database backends by changing the registered library type and updating config — your application code stays the same.
