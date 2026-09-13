# CL.PostgreSQL

> A typed data-access layer for PostgreSQL — repositories, a LINQ query builder, declarative schema sync, imperative migrations, and a self-invalidating result cache.

`CL.PostgreSQL` maps a plain class with attributes, keeps the live table in shape, generates reflection-free row mappers, translates LINQ-shaped expressions to real SQL, and caches results with version-stamped invalidation. It builds on [Npgsql](https://www.nuget.org/packages/Npgsql). Every fallible operation returns a framework `Result<T>` — no exceptions for the expected failure paths.

It shares its architecture with [`CL.MySQL2`](../mysql2/index.md) and [`CL.MSSQL`](../mssql/index.md), so the API is nearly identical across the three. Where PostgreSQL genuinely differs — `ON CONFLICT` arbitration, schemas, case-sensitive identifiers — the difference is called out rather than papered over. See [Dialect notes](#dialect-notes) below.

| | |
|---|---|
| **Package** | [`CodeLogic.PostgreSQL`](https://www.nuget.org/packages/CodeLogic.PostgreSQL) |
| **Library class** | `CL.PostgreSQL.PostgreSQLLibrary` |
| **Config files** | `config.postgresql.json` · `config.postgresql.cache.json` |
| **Dependencies** | Npgsql 9.x |
| **Engines** | PostgreSQL 12+ |

This overview covers loading, the entry points, and configuration. The deep material lives on three sub-pages:

- **[Query Builder](queries.md)** — `Where` / subquery filters / ordering / paging / cursor paging / joins / projections / `GroupBy` aggregates / terminals / bulk update & delete / raw SQL / transactions.
- **[Schema & Migrations](schema-migrations.md)** — entity attributes, `SyncMode` & `SchemaSyncLevel`, `SyncTableAsync` / `SyncSchemaAsync`, the CRC sentinel, soft delete, retention, imperative migrations, backups & restore.
- **[Performance & Caching](performance.md)** — the result cache, time quantization, table-version invalidation, `SmartCachePool`, multi-node coordination, transient retry, the N+1 detector, slow-query / `EXPLAIN`, compiled materializers, projection pushdown.

## Install & load

```bash
dotnet add package CodeLogic.PostgreSQL
```

```csharp
using CL.PostgreSQL;

await Libraries.LoadAsync<PostgreSQLLibrary>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var pg = Libraries.Get<PostgreSQLLibrary>()!;
```

Set your connection in `config.postgresql.json` (auto-generated on first run) before `ConfigureAsync()`.

## Define an entity

A mapped class is a plain C# type decorated with attributes from `CL.PostgreSQL.Models`. The full attribute set is documented on the [Schema & Migrations](schema-migrations.md) page.

```csharp
using CL.PostgreSQL.Models;

[Table(Name = "users", Schema = "public")]
public sealed class User
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "email", Size = 160, Unique = true, NotNull = true)]
    public string Email { get; set; } = "";

    // No DataType: inferred from the CLR type. Guid -> uuid, DateTime -> timestamptz.
    [Column(Name = "external_id")]
    public Guid ExternalId { get; set; }

    [Column(Name = "created_utc", DefaultValue = "now()", Index = true)]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Column(Name = "settings", DataType = DataType.Jsonb)]
    public string? Settings { get; set; }
}
```

Leaving `DataType` off is the common case: the CLR property type decides, and it picks the
native PostgreSQL type rather than a lowest-common-denominator one. Set it explicitly when
you want something the CLR type does not imply, such as `Jsonb` for a `string`.

## Entry points

| Member | Purpose |
|---|---|
| `SyncTableAsync<T>()` | Reconcile one table to the entity definition. |
| `SyncSchemaAsync(params Type[])` | Reconcile several entities in one pass. |
| `GetRepository<T>()` | Typed CRUD: insert, batched bulk insert, upsert, update, delete, paging, increment. |
| `Query<T>()` | Fluent query builder: filters, ordering, joins, projections, aggregates, cursor paging. |
| `SqlQueryAsync<T>()` / `SqlScalarAsync<T>()` / `ExecuteSqlAsync()` | Parameterised raw SQL. |
| `BeginTransactionAsync()` | An `await using` scope that rolls back unless committed. |
| `Migrations` | The migration runner: `MigrateAsync`, `RollbackAsync`, `GetPendingAsync`. |
| `ConnectionManager.RegisterConfiguration(config, id)` | Add a connection at runtime. |
| `HealthCheckAsync()` | Per-connection health for the framework's health endpoint. |
| `GetCacheStats()` / `GetCachePoolStats()` | Cache counters. |

```csharp
await pg.SyncTableAsync<User>();

var repo = pg.GetRepository<User>();
var created = await repo.InsertAsync(new User { Email = "ada@example.com" });

var recent = await pg.Query<User>()
    .Where(u => u.CreatedUtc >= DateTime.UtcNow.AddDays(-7))
    .OrderByDescending(u => u.CreatedUtc)
    .Take(20)
    .ToListAsync();
```

## Multi-database

`Databases` is a named map. Every entry point takes an optional `connectionId` that defaults
to `"Default"`.

```csharp
var reporting = pg.GetRepository<User>("Reporting");
var rows = await pg.Query<User>("Reporting").Where(u => u.Email != null).ToListAsync();
```

Register one at runtime instead of in config:

```csharp
pg.ConnectionManager.RegisterConfiguration(new PostgreSqlDatabaseConfig
{
    Host = "tenant42.db.internal",
    Database = "tenant42",
    Username = "app",
    Password = secret,
    SslMode = PostgreSqlSslMode.VerifyFull,
}, "Tenant42");
```

## Configuration

`config.postgresql.json`, section `postgresql`:

```json
{
  "databases": {
    "Default": {
      "enabled": true,
      "host": "localhost",
      "port": 5432,
      "database": "app",
      "username": "postgres",
      "password": "",
      "sslMode": "Prefer",
      "defaultSchema": "public",
      "minPoolSize": 1,
      "maxPoolSize": 100,
      "syncMode": "production",
      "slowQueryThresholdMs": 1000
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Per-database switch; disabled databases are skipped at startup. |
| `host` / `port` | `localhost` / `5432` | Server endpoint. |
| `database` / `username` / `password` | `""` | Connection credentials. |
| `sslMode` | `Prefer` | `Disable`, `Allow`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. |
| `sslCertificatePath` / `sslKeyPath` / `sslRootCertificatePath` | `null` | Client certificate, its key, and the CA bundle for `VerifyCA` / `VerifyFull`. |
| `defaultSchema` | `public` | Applied as the connection's `search_path`. It does not move entities: a `[Table]` without a `Schema` is always mapped to the literal `public` schema. |
| `applicationName` | `null` | Reported to the server; visible in `pg_stat_activity`. |
| `minPoolSize` / `maxPoolSize` | `1` / `100` | Connection-pool bounds. |
| `connectionLifetime` | `300` | Seconds a pooled connection may sit idle before being closed. |
| `connectionTimeout` / `commandTimeout` | `30` / `30` | Seconds to wait opening a connection / running a command. |
| `syncMode` | `production` | See [Schema & Migrations](schema-migrations.md). |
| `maxBatchInsertSize` | `500` | Intended rows per batched insert. Not currently read — the repository's own 500-row default applies, capped further by the parameter limit. |
| `maxInClauseValues` | `1000` | Advisory only. Generated `IN` lists are not chunked; every value is emitted in one list. |
| `slowQueryThresholdMs` | `1000` | Queries at or above this duration raise a `SlowQueryEvent`. |
| `captureExplainOnSlowQuery` | `true` | Reserved. `EXPLAIN` capture is not implemented; `SlowQueryEvent.ExplainJson` is always null. |
| `n1DetectorThreshold` | `0` | Reserved. The N+1 detector is not implemented; the setting has no effect. |
| `transientRetryCount` / `transientRetryBaseDelayMs` | `3` / `50` | Retry policy for SQLSTATE `40001`, `40P01` and `55P03`. |
| `defaultStringSize` | `255` | Reserved. Type inference uses a hard-coded 255; changing this has no effect. |

### TLS

`Prefer`, the default, encrypts when the server offers it but **does not verify the
certificate**, so it does not protect against an active attacker. Production deployments
should use `VerifyFull`, which checks both the chain and the hostname:

```json
{ "sslMode": "VerifyFull", "sslRootCertificatePath": "/etc/ssl/certs/rds-ca.pem" }
```

## Dialect notes

Points where PostgreSQL behaves differently from the MySQL and SQL Server libraries:

- **Upserts need a conflict target.** `ON CONFLICT` arbitrates on one named unique key, not
  "whichever unique key collides". `UpsertAsync` infers it when the entity has exactly one
  candidate key and throws — naming the candidates — when it has several. Pass
  `conflictTarget` to choose:

  ```csharp
  await repo.UpsertAsync(user, conflictTarget: [nameof(User.Email)]);
  ```

- **Identifiers are case-sensitive.** Everything is emitted double-quoted, so
  `[Column(Name = "userId")]` is a different column from `userid`. Prefer `snake_case`.
- **Schemas are namespaces inside a database.** The connection picks the database;
  `[Table(Schema = "…")]` picks the schema. Every generated statement is schema-qualified.
- **`DateTime` maps to `timestamptz`.** Values with `DateTimeKind.Unspecified` are treated
  as UTC on the way in, since that is what the rest of the stack produces.
- **`OnUpdateCurrentTimestamp` becomes a trigger.** PostgreSQL has no such column clause,
  so schema sync creates a `BEFORE UPDATE` row trigger named `trg_{table}_{column}_touch`.
- **Retention batches by `ctid`.** `DELETE … LIMIT` is not valid PostgreSQL, so the worker
  selects a batch by row pointer with `FOR UPDATE SKIP LOCKED`.

## Health & events

```csharp
var health = await pg.HealthCheckAsync();
```

Returns `Healthy` when every configured connection responds, `Degraded` when some do, and
`Unhealthy` when none do; the payload carries connection counts.

Events published on the framework bus (`CL.PostgreSQL.Events`):

| Event | Raised when |
|---|---|
| `DatabaseConnectedEvent` / `DatabaseDisconnectedEvent` | A connection opens or closes. |
| `TableSyncedEvent` | A table is created or altered; carries the schema, table and statements. |
| `QueryExecutedEvent` | After every query — SQL, elapsed ms, row count, cache-hit flag. |
| `SlowQueryEvent` | A query crosses `slowQueryThresholdMs`. (`ExplainJson` is reserved and always null.) |
| `CacheHitEvent` / `CacheMissEvent` | A cached read is served or falls through. |
| `N1QueryDetectedEvent` | Declared for future use — the detector is not implemented, so this is never published. |
| `HealthChangedEvent` | Health state transitions. |
