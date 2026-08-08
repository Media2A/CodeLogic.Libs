# CL.MSSQL — Schema & Migrations

CL.MSSQL combines attribute-driven schema reconciliation with ordered imperative migrations. Both workflows use a dedicated SQL Server session holding `sys.sp_getapplock`.

## Mapping

Attributes live in `CL.MSSQL.Models`.

```csharp
[Table(Name = "users", Schema = "app", Comment = "Application users")]
public sealed class User
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(DataType = DataType.NVarChar, Size = 200, Unique = true, NotNull = true,
            Collation = "Latin1_General_100_CI_AS_SC_UTF8")]
    [Index(Name = "IX_users_email", Include = [nameof(CreatedUtc)])]
    public string Email { get; set; } = "";

    [Column(DataType = DataType.Json)]
    public string? ProfileJson { get; set; }

    [Column(DataType = DataType.DateTime2, DefaultValue = "SYSUTCDATETIME()")]
    public DateTime CreatedUtc { get; set; }

    [Column(DataType = DataType.RowVersion)]
    public byte[] Version { get; set; } = [];
}
```

`TableAttribute` exposes `Name`, `Schema` (default `dbo`), and `Comment`. `ColumnAttribute` exposes `Name`, `PreviousName`, native `DataType`, size/precision/scale, primary key, identity (`AutoIncrement`), nullability, uniqueness, indexing, default, collation, comment, and `StorageType`.

Native types cover SQL Server integer, decimal, money, real/float, bit, ANSI/Unicode string and MAX, binary and MAX, date/time variants, `uniqueidentifier`, XML, JSON, geometry/geography, and rowversion. JSON maps to `nvarchar(max)` with an `ISJSON` check. Rowversion columns are excluded from insert and update lists.

Default inference uses `nvarchar(255)` for strings, `datetime2` for `DateTime`, `uniqueidentifier` for `Guid`, `bit` for booleans, `varbinary(max)` for bytes, and `int` for enums.

Indexes support unique and composite definitions. `[Index(Include = [...])]` emits a true SQL Server `INCLUDE` list. Foreign keys support cascade, set-null, set-default, and no-action behavior. Comments use `MS_Description` extended properties.

## Synchronization modes

| Mode | Behavior |
|---|---|
| `Production` | Applies additive/non-data-losing changes and records destructive drift as pending. |
| `Developer` | Performs full reconciliation, including removal of obsolete managed objects. |
| `Migration` | Takes a catalog DDL snapshot and performs a one-shot full reconciliation. |

```csharp
await mssql.SyncTableAsync<User>();
await mssql.SyncSchemaAsync(typeof(User), typeof(Order));
mssql.SetSyncMode(SyncMode.Production);
```

Schema inspection uses `sys.schemas`, `sys.tables`, `sys.columns`, `sys.types`, indexes, defaults, checks, foreign keys, and extended properties. Model CRC and reconciliation status are stored in `[dbo].[__schema_state]` using UTC timestamps. `PreviousName` invokes `sys.sp_rename` for in-place column renames.

## Imperative migrations

Implement `IMigration` or derive from `Migration`, register instances, then inspect or execute the ordered plan.

```csharp
public sealed class SeedRoles : Migration
{
    public override MigrationVersion Version => new("1.0.0", 1);
    public override string Description => "Seed roles";

    public override Task UpAsync(IMigrationContext db, CancellationToken ct) =>
        db.ExecuteAsync("INSERT INTO [app].[roles] ([name]) VALUES (N'admin')", ct: ct);

    public override Task DownAsync(IMigrationContext db, CancellationToken ct) =>
        db.ExecuteAsync("DELETE FROM [app].[roles] WHERE [name]=N'admin'", ct: ct);
}

mssql.RegisterMigration(new SeedRoles());
var pending = await mssql.GetPendingMigrationsAsync();
await mssql.MigrateAsync();
await mssql.RollbackAsync(new MigrationVersion("1.0.0", 0));
```

Migration IDs, descriptions, checksums, and application times are stored in `[dbo].[__migrations]` with `SYSUTCDATETIME()`.

## Backups and restore

Schema snapshots are generated from SQL Server catalogs for library-managed columns, identity/default/check constraints, keys, indexes/includes, foreign keys, collations, and comments. Restore validates the presence of a matching `CREATE TABLE`, drops the target, and recreates objects in dependency order. It is schema-only and destructive to table data.

Triggers, temporal history, partitioning, replication, and other externally managed advanced objects are outside snapshot fidelity.
