# CL.MySQL2

> A typed data-access layer for MySQL, MariaDB, and Percona — repositories, a LINQ query builder, declarative schema sync, imperative migrations, and a self-invalidating result cache.

`CL.MySQL2` is the flagship data library for CodeLogic 4. Map a plain class with attributes and the library keeps the live table in shape, generates reflection-free row mappers, translates LINQ-shaped expressions to real SQL, and caches results with version-stamped invalidation. It builds on [MySqlConnector](https://www.nuget.org/packages/MySqlConnector) and works against MySQL, MariaDB, and Percona. Every fallible operation returns a framework `Result<T>` — no exceptions for the expected failure paths.

| | |
|---|---|
| **Package** | [`CodeLogic.MySQL2`](https://www.nuget.org/packages/CodeLogic.MySQL2) |
| **Library class** | `CL.MySQL2.MySQL2Library` |
| **Config files** | `config.mysql.json` · `config.mysql.cache.json` |
| **Dependencies** | MySqlConnector 2.x |
| **Engines** | MySQL 5.7+ · MariaDB 10.3+ · Percona |

This overview covers loading, the entry points, and configuration. The deep material lives on three sub-pages:

- **[Query Builder](queries.md)** — `Where` / subquery filters / ordering / paging / typed and raw joins / projections / `GroupBy` aggregates / terminals / bulk update & delete / raw SQL / transactions.
- **[Schema & Migrations](schema-migrations.md)** — entity attributes, `SyncMode` & `SchemaSyncLevel`, `SyncTableAsync` / `SyncSchemaAsync`, the CRC sentinel, soft delete, retention, imperative migrations, backups & restore.
- **[Performance & Caching](performance.md)** — the result cache, time quantization, table-version invalidation, `SmartCachePool`, multi-node coordination, transient retry, the N+1 detector, slow-query / `EXPLAIN`, compiled materializers, projection pushdown.

## Install & load

```bash
dotnet add package CodeLogic.MySQL2
```

```csharp
using CL.MySQL2;

await Libraries.LoadAsync<MySQL2Library>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var mysql = Libraries.Get<MySQL2Library>();
```

Set your connection in `config.mysql.json` (auto-generated on first run) before `ConfigureAsync()`.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.MySQL2.Attributes`. The full attribute set is documented on the [Schema & Migrations](schema-migrations.md) page.

```csharp
using CL.MySQL2.Attributes;

[Table(Name = "users")]
public class User
{
    [Column(Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Size = 120, Unique = true)]            public string Email { get; set; } = "";
    [Column(Size = 80, Index = true)]              public string DisplayName { get; set; } = "";
    [Column] public int LoginCount { get; set; }
    [Column] public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
```

Reconcile it to the database once, at startup:

```csharp
Result<SyncResult> sync = await mysql.SyncTableAsync<User>();
// or reconcile a whole set as one pass under a single cross-node lock:
await mysql.SyncSchemaAsync(typeof(User), typeof(Order), typeof(Customer));
```

## Repository basics

`GetRepository<T>()` returns a `Repository<T>` covering the common CRUD, paging, count, upsert, increment, and delete operations. All return `Result<…>`.

```csharp
var repo = mysql.GetRepository<User>();

// Create
Result<User> created = await repo.InsertAsync(new User { Email = "ada@example.com", DisplayName = "Ada" });
Result<int>  many    = await repo.InsertManyAsync(batch);   // chunked at MaxBatchInsertSize (default 500)

// Read
Result<User?>       byId   = await repo.GetByIdAsync(1L);
Result<List<User>>  byCol  = await repo.GetByColumnAsync(nameof(User.DisplayName), "Ada");
Result<List<User>>  all    = await repo.GetAllAsync();
Result<long>        count  = await repo.CountAsync();
Result<List<User>>  found  = await repo.FindAsync(u => u.LoginCount > 10);

// Paged
Result<PagedResult<User>> page =
    await repo.GetPagedAsync(page: 1, pageSize: 25, orderByColumn: nameof(User.CreatedUtc), descending: true);

// Update / upsert
Result<User> updated = await repo.UpdateAsync(created.Value!);
Result<User> upserted = await repo.UpsertAsync(new User { Email = "ada@example.com", DisplayName = "Ada L." });
Result<int>  upmany   = await repo.UpsertManyAsync(rows);

// Atomic counter adjustments (single UPDATE … SET col = col ± delta)
Result<int> inc = await repo.IncrementAsync(1L, u => u.LoginCount, 1);
Result<int> dec = await repo.DecrementAsync(1L, u => u.LoginCount, 1);
Result<int> adj = await repo.AdjustAsync(1L, u => u.LoginCount, -5);

// Delete — soft if the entity declares [SoftDelete], otherwise a physical row delete
Result<bool> deleted = await repo.DeleteAsync(1L);
Result<bool> purged  = await repo.HardDeleteAsync(1L);   // always physical
```

`UpsertWithIncrementsAsync` performs an insert-or-accumulate — useful for counters seeded on first sight:

```csharp
await repo.UpsertWithIncrementsAsync(
    insertSeed: new DailyHit { Day = today, Hits = 1 },
    incrementProperties: new[] { nameof(DailyHit.Hits) });
```

`PagedResult<T>` carries `Items`, `PageNumber`, `PageSize`, `TotalItems`, `TotalPages`, `HasPreviousPage`, and `HasNextPage`.

## Entry points

Everything flows through three methods on the library, plus a raw-SQL escape hatch.

| Member | Returns | Purpose |
|--------|---------|---------|
| `GetRepository<T>(connectionId = "Default")` | `Repository<T>` | CRUD / paging / upsert / delete. |
| `Query<T>(connectionId = "Default")` | `QueryBuilder<T>` | Fluent LINQ-to-SQL queries. See [Query Builder](queries.md). |
| `BeginTransactionAsync(connectionId, ct)` | `Task<TransactionScope>` | Explicit transaction (`IAsyncDisposable`, auto-rollback). |
| `SqlQueryAsync<T>(sql, parameters, …)` | `Result<List<T>>` | Raw query materialized into `T`. |
| `ExecuteSqlAsync(sql, parameters, …)` | `Result<int>` | Raw non-query, returns affected rows. |
| `SqlScalarAsync<T>(sql, parameters, …)` | `Result<T?>` | Raw single-value query. |

The `connectionId` selects one of the named databases in `config.mysql.json`; it defaults to `"Default"`. Library properties expose the underlying machinery for advanced use: `ConnectionManager`, `TableSync`, `MigrationTracker`, `BackupManager`, `SchemaState`, and `Migrations`.

## Configuration summary

Two files are written on first run.

`config.mysql.json` (section `mysql`) holds a `Databases` dictionary keyed by connection id — `Default` is created automatically.

| Setting | Default | Notes |
|---------|---------|-------|
| `Enabled` | `true` | Per-database master switch. |
| `Host` / `Port` | `localhost` / `3306` | Server endpoint. |
| `Database` / `Username` / `Password` | `""` | Credentials. |
| `EnablePooling` | `true` | Connection pooling. |
| `MinPoolSize` / `MaxPoolSize` | `1` / `100` | Pool bounds. |
| `ConnectionLifetime` | `300` | Seconds before a pooled connection is recycled. |
| `ConnectionTimeout` / `CommandTimeout` | `30` / `30` | Seconds. |
| `EnableSsl` | `false` | TLS to the server; `SslCertificatePath`, `AllowPublicKeyRetrieval` related. |
| `CharacterSet` / `Collation` | `utf8mb4` / `utf8mb4_unicode_ci` | Session defaults. |
| `SyncMode` | `Production` | `Developer` · `Production` · `Migration`. See [Schema & Migrations](schema-migrations.md). |
| `SchemaSyncLevel` | `Safe` | Low-level cap: `None` · `Safe` · `Additive` · `Full`. |
| `AllowDestructiveSync` | `false` | Legacy flag honoured under `SyncMode` mapping. |
| `BackupDirectory` | `null` | Where schema backups are written. |
| `SlowQueryThresholdMs` | `1000` | Threshold for `SlowQueryEvent`. |
| `CaptureExplainOnSlowQuery` | `true` | Attach `EXPLAIN` JSON to slow-query events. |
| `QueryTimeoutMs` | `30000` | Per-query timeout. |
| `MaxBatchInsertSize` | `500` | Insert/upsert chunk size. |
| `MaxInClauseValues` | `1000` | `IN (...)` value cap. |
| `PreparedStatementCacheSize` | `256` | Per-connection prepared-statement cache. |
| `TransientRetryCount` | `3` | Deadlock/lock-wait retries (0 disables). |
| `TransientRetryBaseDelayMs` | `50` | Base backoff; exponential + jitter. |
| `N1DetectorThreshold` | `0` | Repeats before an N+1 is flagged (0 = off). |
| `CacheEnabledOverride` | `null` | Per-database cache on/off override. |
| `DefaultStringSize` | `255` | `VarChar` size when `Size` is unset. |

`config.mysql.cache.json` (section `mysql.cache`) controls the result cache.

| Setting | Default | Notes |
|---------|---------|-------|
| `Enabled` | `true` | Global cache switch. |
| `MaxEntries` | `10000` | Entry ceiling. |
| `MaxMemoryMb` | `256` | Approximate memory budget. |
| `DefaultTtlSeconds` | `60` | Default TTL when none is given. |
| `TimeQuantizeSeconds` | `60` | Quantization bucket for DateTime params (see [Performance](performance.md)). |
| `PublishEvents` | `true` | Publish cache hit/miss events to the bus. |

Full caching behaviour is on the [Performance & Caching](performance.md) page.

## Health check

```csharp
HealthStatus status = await mysql.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy

Result<bool> ok = await mysql.TestConnectionAsync("Default");
```

`HealthCheckAsync` probes the configured databases; `TestConnectionAsync` opens a single named connection and reports success.

## Events

All events implement `IEvent` and publish to the CodeLogic event bus.

| Event | Published when |
|-------|----------------|
| `DatabaseConnectedEvent` | A database connection is established. |
| `DatabaseDisconnectedEvent` | A connection is closed or lost. |
| `TableSyncedEvent` | A table is reconciled by schema sync. |
| `QueryExecutedEvent` | Any query completes (carries a `CacheHit` flag). |
| `SlowQueryEvent` | A query exceeds `SlowQueryThresholdMs` (carries `ExplainJson`). |
| `CacheHitEvent` | A cached result satisfies a read. |
| `CacheMissEvent` | A cacheable read misses the cache. |
| `N1QueryDetectedEvent` | The N+1 detector trips (`N1DetectorThreshold` > 0). |
| `HealthChangedEvent` | The health status transitions. |

## See also

- [Getting Started](../../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.MySQL2)
