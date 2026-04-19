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

## Syncing — `SyncTableAsync<T>()`

```csharp
await mysql.SyncTableAsync<UserRecord>();
```

What it does, depending on `SchemaSyncLevel`:

| Level | Behavior |
|---|---|
| `None` | No-op. Treats the DB as externally managed. |
| `Safe` (default) | Creates the table if missing; adds missing columns/indexes/FKs; modifies columns to match the model (grow VARCHAR, change default, toggle NULL). **Never drops anything.** |
| `Additive` | `Safe` + drops indexes and FKs no longer in the model. No column data lost. |
| `Full` | `Additive` + drops columns no longer in the model. **Dev only.** |

Config per-DB in `config.mysql.json`:

```json
{
  "SchemaSyncLevel": "Safe"
}
```

**Typical setup:**
- Local dev: `Full` (let the library rebuild as you iterate)
- Staging: `Additive`
- Production: `Safe` (or `None` and run migrations yourself)

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

```csharp
await mysql.TableSync.SyncTablesAsync(new[]
{
    typeof(UserRecord),
    typeof(OrderRecord),
    typeof(SnapshotRecord),
});
```

Uses a single non-generic core — no per-type reflection thrash.

---

## Migration tracking

`MigrationTracker` records explicit migrations in a `__migrations` table so
you know what's been applied:

```csharp
var applied = await mysql.MigrationTracker.HasMigrationBeenAppliedAsync("2026-03-20-seed-roles");

if (!applied)
{
    // run your migration SQL / code ...
    await mysql.MigrationTracker.RecordMigrationAsync(
        migrationId: "2026-03-20-seed-roles",
        description: "Seed default role set",
        checksum: ComputeChecksum(migrationScript));
}
```

This is orthogonal to `SyncTableAsync` — use it for data migrations, seed
data, or SQL that doesn't flow from your record classes.

To inspect history:

```csharp
var history = await mysql.MigrationTracker.GetAppliedMigrationsAsync();
foreach (var m in history)
    Console.WriteLine($"{m.AppliedAt:u}  {m.MigrationId}  {m.Description}");
```

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
