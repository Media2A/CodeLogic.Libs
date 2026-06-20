# CL.MySQL2 — Schema & Migrations

Your record classes are the source of truth for your database schema. Decorate
them with attributes, call `SyncTableAsync<T>()`, and CL.MySQL2 figures out the
`CREATE TABLE` or `ALTER TABLE` statements needed to make MySQL match.

## The attribute cheat-sheet

| Attribute | Target | Purpose |
|---|---|---|
| `[Table]` | class | table name, engine, charset, collation, comment |
| `[Column]` | property | type, size, nullability, default, PK, AI, Unique, Index |
| `[Index]` | property | named index, unique, covering `Include` list |
| `[CompositeIndex]` | class | multi-column index named on the class |
| `[ForeignKey]` | property | FK with ON DELETE / ON UPDATE actions |
| `[RetainDays]` | class | declarative background purge by timestamp |
| `[Ignore]` | property | exclude from schema, read, and write |

Full example:

```csharp
using CL.MySQL2.Models;

[Table(Name = "servers_snapshot",
       Engine = TableEngine.InnoDB,
       Charset = Charset.Utf8mb4,
       Collation = "utf8mb4_unicode_ci",
       Comment = "Per-server health/player samples")]
[RetainDays(90, nameof(SnapshotUtc))]
[CompositeIndex("ix_server_snapshot", "server_id", "snapshot_utc")]
public sealed class SnapshotRecord
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "server_id", DataType = DataType.BigInt, NotNull = true)]
    [ForeignKey("servers", "id", OnDelete = ForeignKeyAction.Cascade)]
    public long ServerId { get; set; }

    [Column(Name = "snapshot_utc", DataType = DataType.DateTime, NotNull = true)]
    [Index(Name = "ix_snapshot_utc_cover",
           Include = new[] { nameof(ServerId), nameof(IsOnline), nameof(PlayerCount) })]
    public DateTime SnapshotUtc { get; set; }

    [Column(Name = "is_online", DataType = DataType.TinyInt, NotNull = true)]
    public bool IsOnline { get; set; }

    [Column(Name = "player_count", DataType = DataType.Int, NotNull = true)]
    public int PlayerCount { get; set; }

    [Ignore]
    public TimeSpan Age => DateTime.UtcNow - SnapshotUtc;
}
```

---

## `[Table]`

```csharp
[Table(Name = "users")]
public class UserRecord { ... }
```

| Field | Default | Notes |
|---|---|---|
| `Name` | class name | MySQL table name |
| `Engine` | `InnoDB` | `TableEngine.InnoDB`, `MyISAM`, `Memory`, `Archive`, `CSV`, `NDB` |
| `Charset` | `Utf8mb4` | Table default charset |
| `Collation` | null | e.g. `"utf8mb4_unicode_ci"` — overrides charset default |
| `Comment` | null | Table comment — useful for DB tooling |

**Recommendation:** keep the defaults (`InnoDB` + `utf8mb4`) unless you have a
specific reason. Row-level locking, foreign keys, crash-safe — InnoDB is the
right choice for ~everything.

---

## `[Column]`

Per-property attribute that describes a column. Every property you want
persisted needs one (or falls back to inferred defaults, which might not do
what you want).

```csharp
[Column(Name = "email",
        DataType = DataType.VarChar,
        Size = 256,
        NotNull = true,
        Unique = true)]
public string Email { get; set; } = "";
```

| Field | Default | Purpose |
|---|---|---|
| `Name` | property name | MySQL column name |
| `DataType` | inferred | See the full `DataType` enum below |
| `Size` | 0 (type default) | VARCHAR length, CHAR length, BINARY length |
| `Precision` | 10 | DECIMAL precision |
| `Scale` | 2 | DECIMAL scale |
| `Primary` | false | Part of PK |
| `AutoIncrement` | false | AUTO_INCREMENT |
| `NotNull` | false | NOT NULL constraint |
| `Unique` | false | UNIQUE constraint (becomes a unique index) |
| `Index` | false | Plain single-column index |
| `DefaultValue` | null | Raw SQL expression (`"0"`, `"'active'"`, `"CURRENT_TIMESTAMP"`) |
| `Charset` | null | Column-level charset override |
| `Comment` | null | Column comment |
| `Unsigned` | false | For numeric types |
| `OnUpdateCurrentTimestamp` | false | Adds `ON UPDATE CURRENT_TIMESTAMP` |
| `StorageType` | `Default` | Physical storage override — see [StorageType](#storagetype--binary-columns) below |

### Supported `DataType`s

Numeric: `TinyInt`, `SmallInt`, `MediumInt`, `Int`, `BigInt`, `Decimal`,
`Float`, `Double`, `Bit`.

String: `Char`, `VarChar`, `TinyText`, `Text`, `MediumText`, `LongText`,
`Enum`, `Set`.

Binary: `Binary`, `VarBinary`, `TinyBlob`, `Blob`, `MediumBlob`, `LongBlob`.

Date/time: `Date`, `Time`, `DateTime`, `Timestamp`, `Year`.

Other: `Json`, `Geometry`.

### Type inference — when you omit `DataType`

If you leave `DataType` unspecified, CL.MySQL2 infers one from the CLR type:

| CLR | Inferred column |
|---|---|
| `bool` | `TinyInt` |
| `byte` | `TinyInt UNSIGNED` |
| `short` / `ushort` | `SmallInt` (`UNSIGNED` if unsigned) |
| `int` / `uint` | `Int` (`UNSIGNED` if unsigned) |
| `long` / `ulong` | `BigInt` (`UNSIGNED` if unsigned) |
| `float` | `Float` |
| `double` | `Double` |
| `decimal` | `Decimal(10, 2)` |
| `string` | `VarChar(DefaultStringSize)` — 255 unless config says otherwise |
| `char` | `Char(1)` |
| `DateTime` / `DateTimeOffset` | `DateTime` |
| `TimeSpan` | `Time` |
| `Guid` | `Char(36)` |
| `byte[]` | `Blob` |
| enum | `Int` |

The inference respects `DefaultStringSize` from `config.mysql.json` — change
it there if you want VARCHAR(512) site-wide.

### Common patterns

**Auto-increment primary key:**
```csharp
[Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
public long Id { get; set; }
```

**Created/updated timestamps:**
```csharp
[Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true,
        DefaultValue = "CURRENT_TIMESTAMP")]
public DateTime CreatedUtc { get; set; }

[Column(Name = "updated_utc", DataType = DataType.DateTime, NotNull = true,
        DefaultValue = "CURRENT_TIMESTAMP",
        OnUpdateCurrentTimestamp = true)]
public DateTime UpdatedUtc { get; set; }
```

**Unique email:**
```csharp
[Column(Name = "email", DataType = DataType.VarChar, Size = 320, NotNull = true, Unique = true)]
public string Email { get; set; } = "";
```

**Stored enum as string:**
```csharp
[Column(Name = "status", DataType = DataType.VarChar, Size = 16, NotNull = true,
        DefaultValue = "'pending'")]
public string Status { get; set; } = "pending";
```

---

## StorageType — binary columns

The `StorageType` property on `[Column]` overrides the physical storage format.
Values are automatically converted to/from binary on every read and write path
(insert, update, materializer, WHERE clauses, IN queries).

Available values: `Binary`, `VarBinary`, `TinyBlob`, `Blob`, `MediumBlob`, `LongBlob`.

### Guid as BINARY(16)

The most common use case. Stores a `Guid` as 16 bytes using RFC 4122 big-endian
layout — half the storage of `CHAR(36)` and sorts correctly:

```csharp
[Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
public Guid Id { get; set; }
```

Generates `BINARY(16)`. The size is auto-detected from the CLR type — no need
to set `Size = 16` explicitly.

### String as binary

Store a string column in binary form (UTF-8 encoded):

```csharp
[Column(StorageType = StorageType.Binary, Size = 64)]
public string Token { get; set; } = "";

[Column(StorageType = StorageType.Blob)]
public string Payload { get; set; } = "";
```

### Auto-sized BINARY

When `StorageType = StorageType.Binary` and `Size` is not set, CL.MySQL2
infers the correct fixed size from the CLR type:

| CLR type | Auto size | Byte order |
|---|---|---|
| `Guid` | 16 | RFC 4122 big-endian |
| `long` / `ulong` / `double` / `DateTime` / `DateTimeOffset` | 8 | big-endian |
| `int` / `uint` / `float` | 4 | big-endian |
| `short` / `ushort` | 2 | big-endian |
| `decimal` | 16 | big-endian |
| `bool` / `byte` / `sbyte` | 1 | — |

All numeric types use big-endian byte order so `ORDER BY` on the binary column
matches the expected numeric sort.

### Full example

```csharp
[Table(Name = "sessions")]
public sealed class SessionRecord
{
    [Column(StorageType = StorageType.Binary, Primary = true, NotNull = true)]
    public Guid Id { get; set; }

    [Column(Name = "user_id", DataType = DataType.BigInt, NotNull = true)]
    public long UserId { get; set; }

    [Column(Name = "token", StorageType = StorageType.Binary, Size = 32, NotNull = true)]
    public string Token { get; set; } = "";

    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime CreatedUtc { get; set; }
}
```

Generates:
```sql
CREATE TABLE `sessions` (
    `Id` BINARY(16) NOT NULL,
    `user_id` BIGINT NOT NULL,
    `token` BINARY(32) NOT NULL,
    `created_utc` DATETIME NOT NULL,
    PRIMARY KEY (`Id`)
);
```

LINQ queries work as expected — Guid values are automatically converted to
binary parameters:

```csharp
var session = await repo.Where(x => x.Id == sessionGuid).FirstOrDefaultAsync();
```

---

## Indexes — `[Index]` and `[CompositeIndex]`

Two flavors. Prefer the new `[Index]` going forward; `Column.Index = true` still
works but the attribute gives you naming, uniqueness, and covering columns.

### Single-column `[Index]`

```csharp
[Column(Name = "snapshot_utc", DataType = DataType.DateTime, NotNull = true)]
[Index]  // name auto-generated: idx_{table}_snapshot_utc
public DateTime SnapshotUtc { get; set; }

// Or named:
[Index(Name = "ix_hot_snapshots")]
public DateTime SnapshotUtc { get; set; }

// Or unique:
[Column(Name = "email", DataType = DataType.VarChar, Size = 320)]
[Index(Unique = true)]
public string Email { get; set; } = "";
```

### Covering index — `Include`

The single most useful index pattern. The main column is the **seek column**;
`Include` columns are the **extra payload** stored at the index leaf:

```csharp
[Column(Name = "snapshot_utc", DataType = DataType.DateTime, NotNull = true)]
[Index(Name = "ix_snapshot_covering",
       Include = new[] { nameof(ServerId), nameof(IsOnline), nameof(PlayerCount) })]
public DateTime SnapshotUtc { get; set; }
```

Emits `INDEX ix_snapshot_covering (snapshot_utc, server_id, is_online, player_count)`.

A query like `WHERE snapshot_utc >= ? SELECT server_id, is_online, player_count`
can now read everything from the index pages — no primary-key lookup per row.
See [Performance docs](mysql2-performance.md#index-strategy--covering-indexes) for
the full benchmark story.

`Include` uses **property names** (refactor-safe with `nameof`) that CL.MySQL2
resolves to column names via `[Column(Name = ...)]` at sync time. Typos throw
at startup, not at runtime.

### Multiple indexes on one column

`[Index]` has `AllowMultiple = true`:

```csharp
[Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)]
[Index(Name = "ix_created")]
[Index(Name = "ix_created_with_user", Include = new[] { nameof(UserId) })]
public DateTime CreatedUtc { get; set; }
```

### Composite index — `[CompositeIndex]`

Class-level; name must be unique within the table:

```csharp
[Table(Name = "orders")]
[CompositeIndex("ix_user_status", "user_id", "status")]
[CompositeIndex("ix_user_placed", "user_id", "placed_utc", Unique = false)]
public sealed class OrderRecord
{
    // ...
}
```

Column arguments are **raw column names** (use `nameof` with care — these are
strings, not property references — though if your property names match your
column names, `nameof(...)` works).

### Which index type should I use?

| Need | Use |
|---|---|
| Simple B-tree on one column | `[Column(Index = true)]` or `[Index]` |
| Unique constraint + lookup | `[Column(Unique = true)]` |
| Covering (include extra columns at leaf) | `[Index(Include = ...)]` |
| Multi-column index led by column A | `[CompositeIndex("...", "a", "b", "c")]` |
| Same column, multiple shapes | multiple `[Index]` + `[CompositeIndex]` as needed |

---

## `[ForeignKey]`

```csharp
[Column(Name = "user_id", DataType = DataType.BigInt, NotNull = true)]
[ForeignKey("users", "id",
            OnDelete = ForeignKeyAction.Cascade,
            OnUpdate = ForeignKeyAction.Restrict)]
public long UserId { get; set; }
```

Generates:
```sql
CONSTRAINT fk_orders_user_id_users
FOREIGN KEY (user_id) REFERENCES users (id)
ON DELETE CASCADE ON UPDATE RESTRICT
```

Actions: `Restrict` (default), `Cascade`, `SetNull`, `NoAction`, `SetDefault`.

Custom constraint name via `ConstraintName` if auto-generated collides (rare).

---

## Retention — `[RetainDays]`

For time-series tables, `[RetainDays]` replaces hand-rolled purge jobs:

```csharp
[Table(Name = "servers_snapshot")]
[RetainDays(90, nameof(SnapshotUtc))]
public sealed class SnapshotRecord
{
    [Column(Name = "snapshot_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime SnapshotUtc { get; set; }
    // ...
}
```

When you call `await mysql.SyncTableAsync<SnapshotRecord>()`, the library
registers the entity. A background `RetentionWorker` picks it up, and once per
24h runs:

```sql
DELETE FROM `servers_snapshot`
 WHERE `snapshot_utc` < NOW() - INTERVAL 90 DAY
 LIMIT 5000
```

...in a loop until the pass deletes zero rows. Batched to keep InnoDB's undo
log small.

| Parameter | Default | Notes |
|---|---|---|
| `Days` (positional) | — | Keep rows for this many days |
| `TimestampColumn` (positional) | — | Property name (use `nameof`) |
| `BatchSize` | 5000 | Rows per DELETE statement |

The worker starts automatically in `OnStartAsync` if any registered entity
carries `[RetainDays]`. It runs with a 5-minute initial delay so startup isn't
blocked.

> ⚠️ **`TimestampColumn` must point to a `DateTime` column.** Validation
> happens at run-time when the worker first tries to purge. If you pass the
> wrong name you'll get a clear log error.

---

## `[Ignore]`

Exclude a property from all DB operations — read, write, and schema sync:

```csharp
[Ignore]
public TimeSpan Age => DateTime.UtcNow - CreatedUtc;

[Ignore]
public List<string> RuntimeTags { get; set; } = new();
```

Use for computed values and in-memory scratch state.

---

## Syncing — `SyncTableAsync<T>()` and `SyncSchemaAsync(...)`

```csharp
// One table.
await mysql.SyncTableAsync<UserRecord>();

// A whole set as one pass under a single cross-node lock — the recommended
// startup entry point.
await mysql.SyncSchemaAsync(
    typeof(UserRecord),
    typeof(OrderRecord),
    typeof(SnapshotRecord));
```

`SyncSchemaAsync` returns `Result<Dictionary<string, SyncResult>>` keyed by table
name; the single-table `SyncTableAsync<T>` returns `Result<SyncResult>`.

### Sync modes — `SyncMode` _(new in 4.5.3)_

`SyncMode` is the primary, operator-facing knob (per database). It decides how
aggressively sync reconciles the live schema with your models:

| Mode | Behavior |
|---|---|
| `Developer` | Rolling-update dev mode. Reconciles aggressively on every boot — drops removed columns/indexes/FKs without asking. (Maps to `SchemaSyncLevel.Full`.) |
| `Production` *(default)* | Additive only — adds/modifies columns, indexes and FKs but **never drops**. When a model change would require a drop, the change is deferred and the table is flagged `DriftPending` for a later `Migration` pass. (Maps to `SchemaSyncLevel.Safe`.) |
| `Migration` | Deliberate one-shot destructive reconcile — takes a schema backup first, then applies the drops `Production` deferred. **Idempotent:** once every model matches its stored CRC and nothing is pending, the pass does nothing and logs a warning to switch back to `Production`. (Maps to `SchemaSyncLevel.Full`.) |

Config per-DB in `config.mysql.json`:

```json
{
  "Databases": { "Default": { "SyncMode": "Production" } }
}
```

**Typical lifecycle:**
- Local dev: `Developer` — let the library rebuild as you iterate.
- Production: `Production` — additive, never loses data.
- When you ship a release that drops columns/indexes: switch one node to
  `Migration` for a single boot to apply the deferred drops, then switch back to
  `Production` (the library nags you to in the logs once the pass is clean).

Flip the mode at runtime — no config edit or restart — with:

```csharp
mysql.SetSyncMode(SyncMode.Production);     // e.g. right after a Migration pass completes
```

#### Legacy: `SchemaSyncLevel` / `AllowDestructiveSync`

`SyncMode` **supersedes** the older `SchemaSyncLevel` and `AllowDestructiveSync`
flags, which are retained for back-compat. `SyncMode` takes precedence and maps
onto an internal `SchemaSyncLevel` via `EffectiveSyncLevel`:

| `SchemaSyncLevel` | Behavior |
|---|---|
| `None` | No-op. Treats the DB as externally managed. |
| `Safe` | Add missing columns/indexes/FKs; modify columns to match the model (grow VARCHAR, change default, toggle NULL); rename via `PreviousName`. **Never drops.** |
| `Additive` | `Safe` + drops indexes and FKs no longer in the model. No column data lost. |
| `Full` | `Additive` + drops columns no longer in the model. |

An explicit `SyncMode.Developer` or `SyncMode.Migration` always resolves to
`Full`. A `Production` config honours the legacy `SchemaSyncLevel` /
`AllowDestructiveSync` for back-compat — so an old config with
`SchemaSyncLevel = Full` still behaves destructively, while a fresh `Production`
default (`Safe`) never drops.

### The CRC fast-path — `__schema_state` _(new in 4.5.3)_

Sync keeps a sentinel table, `__schema_state`, with one row per model holding a
CRC of that model's desired schema plus its reconciliation status. **Before any
`information_schema` diffing**, sync hashes the current model and compares it to
the stored CRC: if the CRC matches, the row is `Synced`, and the table really
exists, the table is skipped entirely — no diffing, no DDL, no lock contention.

`SyncResult` surfaces what happened:

| Field | Meaning |
|---|---|
| `Skipped` | True when the CRC fast-path skipped the table (model unchanged). |
| `SchemaCrc` | The model's computed schema CRC. |
| `DriftPending` | True when an additive (`Production`) sync left a destructive change deferred — a later `Migration` pass will complete it. |

The per-table status is one of `SchemaSyncStatus.Synced` / `DriftPending`.
Inspect the sentinel directly through `mysql.SchemaState` (a `SchemaStateStore`):

```csharp
foreach (var row in await mysql.SchemaState.GetAllAsync())
    Console.WriteLine($"{row.TableName,-30} {row.Status}  crc={row.SchemaCrc}");
```

### Cross-node lock _(new in 4.5.3)_

A schema-sync (or migration) pass serializes across application nodes with MySQL's
connection-scoped `GET_LOCK`. When a cluster boots, one node wins the lock and
runs the DDL; the others wait, then find the schema already reconciled (matching
CRCs) and do nothing. `SyncSchemaAsync` holds a single lock for the whole pass; a
node that can't get the lock logs a warning and skips — unchanged tables are still
served via the CRC fast-path.

### Backups before ALTER

With `createBackup: true` (the default), the library writes the current
`SHOW CREATE TABLE` output to `data/backups/{table}_{timestamp}.sql` **before**
any `ALTER` pass. That's not a data backup — it's a schema snapshot you can
diff. Cheap insurance.

```csharp
// Skip the backup (faster test cycles)
await mysql.SyncTableAsync<UserRecord>(createBackup: false);
```

### Syncing many tables at once

Prefer `mysql.SyncSchemaAsync(...)` — it brackets the whole pass in one cross-node
lock and honours the configured `SyncMode`:

```csharp
await mysql.SyncSchemaAsync(
    typeof(UserRecord),
    typeof(OrderRecord),
    typeof(SnapshotRecord));
```

The lower-level `mysql.TableSync.SyncTablesAsync(types)` does the same and is what
`SyncSchemaAsync` delegates to. Both use a single non-generic core — no per-type
reflection thrash — and the CRC fast-path per table.

---

## Imperative migrations _(new in 4.5.3)_

Declarative sync handles structural shape (columns, indexes, FKs). For everything
it can't express — data transforms, seed data, semantic changes — write an
**imperative migration**: an `IMigration` run in order over the `__migrations`
history table.

### Writing a migration

Subclass the `Migration` base (it supplies `Version` and `Description` from the
constructor and a `DownAsync` that throws until you override it):

```csharp
using CL.MySQL2.Models;
using CL.MySQL2.Services;

public sealed class SeedRoles() : Migration(appVersion: "1.4.0", order: 1, "Seed default roles")
{
    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct)
    {
        await ctx.ExecuteAsync(
            "INSERT INTO roles (name) VALUES ('admin'), ('user')", ct: ct);
    }

    // Override DownAsync to make the migration reversible. The default base
    // implementation throws NotSupportedException.
    public override async Task DownAsync(IMigrationContext ctx, CancellationToken ct)
    {
        await ctx.ExecuteAsync(
            "DELETE FROM roles WHERE name IN ('admin','user')", ct: ct);
    }
}
```

`MigrationVersion` is `(string AppVersion, int Order)`; migrations sort by semantic
version, then order. A migration is only eligible to run once the application's
version (`CodeLogicEnvironment.AppVersion`) is at or above its `AppVersion`.

### The migration context

`IMigrationContext` runs on the migration's own connection and transaction:

| Member | Purpose |
|---|---|
| `ExecuteAsync(sql, parameters?, ct)` | Non-query (INSERT/UPDATE/DELETE/DDL); returns affected rows. |
| `QueryAsync<T>(sql, parameters?, ct)` | Materializes rows into `T` via the compiled materializer. |
| `ScalarAsync<T>(sql, parameters?, ct)` | Single value (first column of the first row). |
| `SyncTableAsync<T>(ct)` | Bridge into declarative sync — brings `T`'s table to its current model shape (CREATE if missing, else additive ALTERs) so the migration can add only the data transform around it. |
| `Connection` / `Transaction` | The raw `MySqlConnection` / `MySqlTransaction`. |

> **DDL auto-commit caveat.** MySQL implicitly commits on DDL, so a migration that
> mixes `ALTER` (or `SyncTableAsync`) with data changes is **not atomic**. Keep
> `UpAsync` steps idempotent, and split heavy DDL and heavy backfill into separate
> migrations.

### Registering and running

```csharp
mysql.RegisterMigration(new SeedRoles());
// ...or scan an assembly for every concrete IMigration with a parameterless ctor:
mysql.RegisterMigrationsFrom(typeof(SeedRoles).Assembly);

// Inspect the plan without applying anything.
foreach (var p in await mysql.GetPendingMigrationsAsync())
    Console.WriteLine($"{p.Version}  {p.MigrationId}  {p.Description}");

// Apply all pending migrations in order, each in its own transaction, under the
// shared schema-sync lock. Caller-driven — not auto-run on start.
var result = await mysql.MigrateAsync();
if (result.IsSuccess)
    Console.WriteLine($"Applied {result.Value!.Count} migration(s).");
```

Migrations are applied in `MigrationVersion` order, gated by the app version, and
serialized across nodes by the same `GET_LOCK` lock declarative sync uses. If a
previously-applied migration's body has been edited since it ran, the runner
detects the checksum drift and logs a warning.

### Rollback

`RollbackAsync` reverts every applied migration whose version is **strictly
greater** than the target, newest-first, each in its own transaction:

```csharp
await mysql.RollbackAsync(new MigrationVersion("1.3.0", 0));
```

It **pre-flights the range and aborts before making any change** if a migration in
range does not override `DownAsync` — so a half-rolled-back state isn't possible
because of a missing down step.

### Declarative restore — `RestoreSchemaAsync`

To roll back a *declaratively-synced* table instead of an imperative migration,
replay a `BackupManager` schema snapshot:

```csharp
await mysql.RestoreSchemaAsync("orders");                 // latest backup
await mysql.RestoreSchemaAsync("orders", backupFile);     // a specific snapshot
```

This drops and recreates the table from the captured DDL and clears its
`__schema_state` row so the next sync reconciles it from scratch. It is operator-
driven and **destructive — only DDL was backed up, so the table's rows are lost.**

### Low-level migration tracking

`MigrationTracker` is the `__migrations` history table the runner sits on. You can
use it directly for ad-hoc bookkeeping:

```csharp
var applied = await mysql.MigrationTracker.HasMigrationBeenAppliedAsync("2026-03-20-seed-roles");
var history = await mysql.MigrationTracker.GetAppliedMigrationsAsync();
foreach (var m in history)
    Console.WriteLine($"{m.AppliedAt:u}  {m.MigrationId}  {m.Description}");
```

Most code should prefer the `IMigration` / `MigrateAsync` flow above; the tracker
is the plumbing underneath it.

---

## Backup & restore

### Ad-hoc schema backup

```csharp
await mysql.BackupManager.BackupTableSchemaAsync("orders");
// → data/backups/orders_{timestamp}.sql  (CREATE TABLE DDL)

await mysql.BackupManager.BackupDatabaseSchemaAsync();
// → data/backups/database_{timestamp}.sql
```

### Old backup cleanup

```csharp
await mysql.BackupManager.CleanupOldBackupsAsync(olderThanDays: 30);
```

> Schema backups are DDL only — they don't include data. For data backups,
> use `mysqldump` or your provider's snapshot feature.

---

## Common schema recipes

### "I want a cache of expensive computations"

```csharp
[Table(Name = "computed_cache")]
[RetainDays(7, nameof(ComputedAt))]
public sealed class CachedResult
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "key", DataType = DataType.VarChar, Size = 255, NotNull = true, Unique = true)]
    public string Key { get; set; } = "";

    [Column(Name = "payload", DataType = DataType.Json, NotNull = true)]
    public string Payload { get; set; } = "{}";

    [Column(Name = "computed_at", DataType = DataType.DateTime, NotNull = true)]
    public DateTime ComputedAt { get; set; }
}
```

Unique key for lookup, JSON payload, auto-purge after 7 days.

### "I want audit trails that never grow unbounded"

```csharp
[Table(Name = "audit_events")]
[RetainDays(365, nameof(AtUtc))]
[CompositeIndex("ix_actor_at", "actor_id", "at_utc")]
public sealed class AuditEvent
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "actor_id", DataType = DataType.BigInt, NotNull = true)]
    public long ActorId { get; set; }

    [Column(Name = "event_type", DataType = DataType.VarChar, Size = 64, NotNull = true)]
    public string EventType { get; set; } = "";

    [Column(Name = "at_utc", DataType = DataType.DateTime, NotNull = true)]
    public DateTime AtUtc { get; set; }

    [Column(Name = "details", DataType = DataType.Json)]
    public string? Details { get; set; }
}
```

### "I want per-tenant data isolation"

```csharp
[Table(Name = "tenant_orders")]
[CompositeIndex("ix_tenant_placed", "tenant_id", "placed_utc")]
public sealed class TenantOrder
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "tenant_id", DataType = DataType.BigInt, NotNull = true)]
    [Index]
    public long TenantId { get; set; }

    // ... other fields ...
}
```

Every query becomes `WHERE tenant_id = ? AND ...` — make sure `tenant_id` is
the first index column so the index is selective per tenant.

---

## What's next

- **How to query what you just modeled** → [Query Builder](mysql2-queries.md)
- **Making it fast** → [Performance & Caching](mysql2-performance.md)
- **Getting started** → [Overview](mysql2.md)
