# Database Libraries

CodeLogic Libraries includes three database libraries with a similar repository and query-builder workflow. Use the backend that fits your infrastructure, or mix them in the same application.

| Library | Database | Driver |
|---------|----------|--------|
| CL.MySQL2 | MySQL / MariaDB | MySqlConnector |
| CL.PostgreSQL | PostgreSQL | Npgsql |
| CL.SQLite | SQLite | Microsoft.Data.Sqlite |

---

## CL.SQLite

SQLite is a good fit for embedded storage, local tools, and lightweight deployments.

### Registration

```csharp
await Libraries.LoadAsync<CL.SQLite.SQLiteLibrary>();
```

### Configuration (`config.sqlite.json`)

```json
{
  "Databases": {
    "Default": {
      "Enabled": true,
      "DatabasePath": "database.db",
      "ConnectionTimeoutSeconds": 30,
      "CommandTimeoutSeconds": 120,
      "UseWAL": true,
      "EnableForeignKeys": true,
      "MaxPoolSize": 10
    },
    "cache": {
      "Enabled": true,
      "DatabasePath": "cache.db",
      "UseWAL": true,
      "EnableForeignKeys": false,
      "MaxPoolSize": 5
    }
  }
}
```

### Repository Usage

```csharp
var sqlite = context.GetLibrary<CL.SQLite.SQLiteLibrary>();
var userRepo = sqlite.GetRepository<User>();
var cacheRepo = sqlite.GetRepository<CachedItem>("cache");

var inserted = await userRepo.InsertAsync(new User { Email = "alice@example.com", Name = "Alice" });
var users = await userRepo.FindAsync(u => u.Email == "alice@example.com");
```

### Query Builder

```csharp
var results = await sqlite.GetQueryBuilder<User>("Default")
    .Where(u => u.IsActive)
    .OrderBy(u => u.Name)
    .Limit(20)
    .ToListAsync();
```

---

## CL.MySQL2

MySQL library with repository access, query building, schema sync, and health checks.

### Registration

```csharp
await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();
```

### Configuration (`config.mysql.json`)

```json
{
  "Databases": {
    "Default": {
      "Enabled": true,
      "Host": "localhost",
      "Port": 3306,
      "Database": "myapp",
      "Username": "app",
      "Password": "secret",
      "EnablePooling": true,
      "MinPoolSize": 1,
      "MaxPoolSize": 20,
      "ConnectionTimeout": 30,
      "CommandTimeout": 30
    },
    "reporting": {
      "Enabled": true,
      "Host": "replica.internal",
      "Port": 3306,
      "Database": "myapp_reporting",
      "Username": "ro_app",
      "Password": "secret",
      "EnablePooling": true,
      "MinPoolSize": 1,
      "MaxPoolSize": 10,
      "ConnectionTimeout": 30,
      "CommandTimeout": 30
    }
  }
}
```

### Repository Usage

```csharp
var mysql = context.GetLibrary<CL.MySQL2.MySQL2Library>();
var orderRepo = mysql.GetRepository<Order>();
var reportingRepo = mysql.GetRepository<OrderReport>("reporting");

var pendingOrders = await orderRepo.FindAsync(o => o.Status == "Pending");
```

### Query Builder

```csharp
var results = await mysql.Query<Order>()
    .Where(o => o.Status == "Completed")
    .OrderByDescending(o => o.Id)
    .Limit(50)
    .ToListAsync();
```

### Table Sync

```csharp
await mysql.SyncTableAsync<User>();
```

---

## CL.PostgreSQL

PostgreSQL library with support for multiple named database connections.

### Registration

```csharp
await Libraries.LoadAsync<CL.PostgreSQL.PostgreSQLLibrary>();
```

### Configuration (`config.postgresql.json`)

```json
{
  "Databases": {
    "Default": {
      "Enabled": true,
      "Host": "localhost",
      "Port": 5432,
      "Database": "myapp",
      "Username": "app",
      "Password": "secret"
    },
    "reporting": {
      "Enabled": true,
      "Host": "replica.internal",
      "Port": 5432,
      "Database": "myapp_ro",
      "Username": "ro_app",
      "Password": "secret"
    }
  }
}
```

### Multi-Database Usage

```csharp
var pg = context.GetLibrary<CL.PostgreSQL.PostgreSQLLibrary>();

var userRepo = pg.GetRepository<User>();
var reportRepo = pg.GetRepository<ReportRow>("reporting");

var monthlyStats = await pg.Query<ReportRow>("reporting")
    .Where(r => r.Month == DateTime.UtcNow.Month)
    .ToListAsync();
```

### Table Sync

```csharp
await pg.SyncTableAsync<User>(connectionId: "Default");
```

---

## Shared Patterns

The three libraries share the same broad approach:

```csharp
Repository<T> GetRepository<T>(string connectionId = "Default") where T : class, new();
Task<HealthStatus> HealthCheckAsync();
```

Query-builder entry points differ slightly:

```csharp
// SQLite
QueryBuilder<T> GetQueryBuilder<T>(string connectionId = "Default") where T : class, new();

// MySQL2 / PostgreSQL
QueryBuilder<T> Query<T>(string connectionId = "Default") where T : class, new();
```

That keeps most application code familiar across backends, while still allowing each library to expose backend-specific behavior where needed.
