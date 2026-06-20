using CodeLogic;                    // namespace → Libraries, CodeLogicOptions
using static CodeLogic.CodeLogic;   // InitializeAsync / ConfigureAsync / StartAsync / StopAsync
using CL.MySQL2;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using MySqlConnector;

// ── Integration test suite for CL.MySQL2 against a local MariaDB ───────────────
// Boots the real CodeLogic runtime (the way a consumer does), then exercises the
// four features added this cycle: typed JOINs, EXISTS/IN subqueries, column rename,
// and the multi-node cache coordinator. Console runner: prints PASS/FAIL, exits non-zero
// on any failure.

// Environment-driven so there are no machine-specific paths. Defaults target a local
// MySQL/MariaDB on 127.0.0.1:3310 (override via CL_MYSQL2_TEST_* env vars).
static string Env(string key, string fallback) =>
    Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
var Fw     = Env("CL_MYSQL2_TEST_ROOT", Path.Combine(Path.GetTempPath(), "cl_mysql2_tests"));
var dbHost = Env("CL_MYSQL2_TEST_HOST", "127.0.0.1");
var dbPort = Env("CL_MYSQL2_TEST_PORT", "3310");
var dbName = Env("CL_MYSQL2_TEST_DB",   "cl_test");
var dbUser = Env("CL_MYSQL2_TEST_USER", "root");
var dbPass = Env("CL_MYSQL2_TEST_PASS", "");

int total = 0, fails = 0;
void Check(string name, bool ok, string? detail = null)
{
    total++; if (!ok) fails++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(!ok && detail is not null ? $" — {detail}" : "")}");
}

// ── 1. Write a valid config pointing at the local MariaDB, BEFORE ConfigureAsync ──
var init = await InitializeAsync(o =>
{
    o.FrameworkRootPath = Fw;
    o.AppVersion = "1.0.0";
    o.HandleShutdownSignals = false;
});
if (!init.Success) { Console.Error.WriteLine($"init failed: {init.Message}"); return 1; }

var cfgDir = Path.Combine(Fw, "Libraries", "CL.MySQL2");
Directory.CreateDirectory(cfgDir);
File.WriteAllText(Path.Combine(cfgDir, "config.mysql.json"), $$"""
{
  "databases": {
    "Default": {
      "enabled": true,
      "host": "{{dbHost}}",
      "port": {{dbPort}},
      "database": "{{dbName}}",
      "username": "{{dbUser}}",
      "password": "{{dbPass}}",
      "syncMode": "developer",
      "characterSet": "utf8mb4"
    }
  }
}
""");

await Libraries.LoadAsync<MySQL2Library>();
await ConfigureAsync();
await StartAsync();

var mysql = Libraries.Get<MySQL2Library>();
if (mysql is null) { Console.Error.WriteLine("MySQL2Library not available after start"); return 1; }
Console.WriteLine("CodeLogic booted; MySQL2 library running.\n");

try
{
    // ── 2. Clean slate ────────────────────────────────────────────────────────
    await Exec(mysql, "DROP TABLE IF EXISTS it_shipment, it_order, it_customer, it_rename");

    // ── 3. Schema sync ────────────────────────────────────────────────────────
    Console.WriteLine("Schema sync:");
    Check("sync it_customer", (await mysql.SyncTableAsync<Customer>(createBackup: false)).IsSuccess);
    Check("sync it_order",    (await mysql.SyncTableAsync<Order>(createBackup: false)).IsSuccess);
    Check("sync it_shipment", (await mysql.SyncTableAsync<Shipment>(createBackup: false)).IsSuccess);

    // ── 4. Seed data ──────────────────────────────────────────────────────────
    var cust = mysql.GetRepository<Customer>();
    var alice = (await cust.InsertAsync(new Customer { Name = "Alice", Country = "DK", IsVip = true })).Value!;
    var bob   = (await cust.InsertAsync(new Customer { Name = "Bob",   Country = "SE", IsVip = false })).Value!;
    var ord = mysql.GetRepository<Order>();
    var o1 = (await ord.InsertAsync(new Order { CustomerId = alice.Id, Total = 150m })).Value!;
    var o2 = (await ord.InsertAsync(new Order { CustomerId = alice.Id, Total = 40m  })).Value!;
    var o3 = (await ord.InsertAsync(new Order { CustomerId = bob.Id,   Total = 999m })).Value!;
    var ship = mysql.GetRepository<Shipment>();
    await ship.InsertAsync(new Shipment { OrderId = o1.Id, Status = "sent" });
    await ship.InsertAsync(new Shipment { OrderId = o3.Id, Status = "pending" });
    Console.WriteLine($"\nSeeded: customers Alice#{alice.Id}, Bob#{bob.Id}; orders {o1.Id},{o2.Id},{o3.Id}.\n");

    // ── 5. Typed JOIN ─────────────────────────────────────────────────────────
    Console.WriteLine("Typed JOIN:");
    var joinRes = await mysql.Query<Order>()
        .Where(o => o.Total > 100m)
        .Join<Customer, long, OrderView>(
            o => o.CustomerId,
            c => c.Id,
            (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total })
        .OrderByDescending((o, c) => o.Total)
        .ToListAsync();
    Check("join succeeds", joinRes.IsSuccess, joinRes.Error?.Message);
    var jv = joinRes.Value ?? [];
    Check("join row count (Total>100 → o1,o3)", jv.Count == 2, $"got {jv.Count}");
    Check("join ordered desc by total (Bob 999 first)", jv.Count == 2 && jv[0].Customer == "Bob" && jv[0].Total == 999m);
    Check("join projects customer name (o1 → Alice)", jv.Any(v => v.OrderId == o1.Id && v.Customer == "Alice"));

    // LEFT join: every order, customer may be null-projected (all have customers here)
    var leftCount = (await mysql.Query<Order>()
        .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
            (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total },
            JoinType.Left)
        .CountAsync()).Value;
    Check("left join counts all 3 orders", leftCount == 3, $"got {leftCount}");

    // ── 6. Subqueries ─────────────────────────────────────────────────────────
    Console.WriteLine("\nSubqueries:");
    var existsCount = (await mysql.Query<Order>()
        .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent")
        .CountAsync()).Value;
    Check("WhereExists sent shipment (only o1)", existsCount == 1, $"got {existsCount}");

    var notExistsCount = (await mysql.Query<Order>()
        .WhereNotExists<Shipment>((o, s) => s.OrderId == o.Id)
        .CountAsync()).Value;
    Check("WhereNotExists any shipment (o2 has none)", notExistsCount == 1, $"got {notExistsCount}");

    var inRes = await mysql.Query<Order>()
        .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip)
        .ToListAsync();
    Check("WhereIn VIP customers (Alice's o1,o2)", inRes.IsSuccess && inRes.Value!.Count == 2,
        $"got {inRes.Value?.Count}");

    var notInCount = (await mysql.Query<Order>()
        .WhereNotIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip)
        .CountAsync()).Value;
    Check("WhereNotIn VIP (Bob's o3)", notInCount == 1, $"got {notInCount}");

    // ── 7. Column rename (data preserved) ─────────────────────────────────────
    Console.WriteLine("\nColumn rename:");
    Check("sync it_rename v1 (email)", (await mysql.SyncTableAsync<RenameV1>(createBackup: false)).IsSuccess);
    var rrepo = mysql.GetRepository<RenameV1>();
    await rrepo.InsertAsync(new RenameV1 { Email = "alice@example.com" });
    // Re-sync the SAME table with a renamed column → CHANGE COLUMN email email_address
    var renameSync = await mysql.SyncTableAsync<RenameV2>(createBackup: false);
    Check("sync it_rename v2 (rename)", renameSync.IsSuccess, renameSync.Error?.Message);
    var renamedOps = renameSync.Value?.Operations ?? [];
    Check("emitted CHANGE COLUMN", renamedOps.Any(op => op.Contains("CHANGE COLUMN", StringComparison.OrdinalIgnoreCase)),
        string.Join(" | ", renamedOps));
    var renamedRow = (await mysql.Query<RenameV2>().FirstOrDefaultAsync()).Value;
    Check("data preserved through rename", renamedRow is not null && renamedRow.EmailAddress == "alice@example.com",
        renamedRow?.EmailAddress);
    // The old column must be gone, the new one present
    var cols = await ColumnNames(mysql, "it_rename");
    Check("old column 'email' dropped", !cols.Contains("email"));
    Check("new column 'email_address' present", cols.Contains("email_address"));

    // ── 8. Result cache ───────────────────────────────────────────────────────
    Console.WriteLine("\nResult cache:");
    var c1 = await mysql.Query<Customer>().WithCache(TimeSpan.FromMinutes(1)).ToListAsync();
    var c2 = await mysql.Query<Customer>().WithCache(TimeSpan.FromMinutes(1)).ToListAsync();
    Check("cached query returns same count twice", c1.IsSuccess && c2.IsSuccess && c1.Value!.Count == c2.Value!.Count);
    Check("cache has entries for it_customer", mysql.GetCacheStats().EntriesByTable.ContainsKey("it_customer"));

    // ── 8b. Raw SQL escape hatch (A1) ─────────────────────────────────────────
    Console.WriteLine("\nRaw SQL (A1):");
    var raw = await mysql.SqlQueryAsync<Customer>(
        "SELECT * FROM it_customer WHERE country = @c ORDER BY id",
        new Dictionary<string, object?> { ["@c"] = "DK" });
    Check("SqlQueryAsync materializes rows", raw.IsSuccess && raw.Value!.Count == 1 && raw.Value[0].Name == "Alice",
        raw.Error?.Message ?? $"count {raw.Value?.Count}");
    var scalar = await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM it_order");
    Check("SqlScalarAsync returns scalar", scalar.IsSuccess && scalar.Value == 3, $"got {scalar.Value}");
    var aff = await mysql.ExecuteSqlAsync("UPDATE it_order SET total = total WHERE total < 0");
    Check("ExecuteSqlAsync returns affected count", aff.IsSuccess && aff.Value == 0, aff.Error?.Message);

    // ── 8c. Soft deletes (F1) ─────────────────────────────────────────────────
    Console.WriteLine("\nSoft deletes (F1):");
    await Exec(mysql, "DROP TABLE IF EXISTS it_soft");
    Check("sync it_soft", (await mysql.SyncTableAsync<SoftThing>(createBackup: false)).IsSuccess);
    var sr = mysql.GetRepository<SoftThing>();
    var s1 = (await sr.InsertAsync(new SoftThing { Label = "keep" })).Value!;
    var s2 = (await sr.InsertAsync(new SoftThing { Label = "remove" })).Value!;
    var del = await sr.DeleteAsync(s2.Id);
    Check("repo.DeleteAsync soft-deletes (affected)", del.IsSuccess && del.Value);
    var visible = (await mysql.Query<SoftThing>().ToListAsync()).Value ?? [];
    Check("query hides soft-deleted (1 of 2)", visible.Count == 1 && visible[0].Id == s1.Id, $"got {visible.Count}");
    var allRows = (await mysql.Query<SoftThing>().IncludeDeleted().ToListAsync()).Value ?? [];
    Check("IncludeDeleted shows all (2)", allRows.Count == 2, $"got {allRows.Count}");
    Check("repo.GetById hides soft-deleted", (await sr.GetByIdAsync(s2.Id)).Value is null);
    var deletedRow = (await mysql.Query<SoftThing>().IncludeDeleted().Where(x => x.Id == s2.Id).FirstOrDefaultAsync()).Value;
    Check("deleted_utc timestamp populated", deletedRow?.DeletedUtc is not null);
    Check("HardDeleteAsync removes physically", (await sr.HardDeleteAsync(s2.Id)).Value);
    Check("row gone after hard delete (1 left)",
        (await mysql.Query<SoftThing>().IncludeDeleted().CountAsync()).Value == 1);

    // ── 8d. Stampede protection (C2) — concurrent cold reads collapse, all correct ──
    Console.WriteLine("\nStampede (C2):");
    var herd = Enumerable.Range(0, 20)
        .Select(_ => mysql.Query<Customer>().Where(c => c.Id > 0).WithCache(TimeSpan.FromMinutes(1)).ToListAsync());
    var herdResults = await Task.WhenAll(herd);
    Check("20 concurrent cached reads all succeed + consistent",
        herdResults.All(r => r.IsSuccess && r.Value!.Count == herdResults[0].Value!.Count));

    // ── 9. Multi-node cache coordinator (fan-out on mutation) ─────────────────
    Console.WriteLine("\nCache coordinator:");
    var fake = new FakeCoordinator();
    QueryCache.UseCoordinator(fake);
    Check("OnInvalidation handler wired", fake.HandlerWired);
    // A bulk update on it_order must invalidate + fan out to peers.
    await mysql.Query<Order>().Where(o => o.Total < 0m).UpdateAsync(o => new Order { Total = 0m });
    Check("local mutation fans out to peers", fake.Published.Contains("it_order"),
        $"published: [{string.Join(",", fake.Published)}]");
    // Simulate a peer broadcast → local version bumps without re-publishing.
    fake.Published.Clear();
    var before = mysql.GetCacheStats().TableVersions.TryGetValue("it_customer", out var bv) ? bv : 0;
    fake.FireInvalidation("it_customer");
    var after = mysql.GetCacheStats().TableVersions.TryGetValue("it_customer", out var av) ? av : 0;
    Check("peer broadcast bumps local version", after == before + 1, $"{before}→{after}");
    Check("peer broadcast does not re-publish", !fake.Published.Contains("it_customer"));

    // ── 10. Sync modes + CRC sentinel ─────────────────────────────────────────
    Console.WriteLine("\nSync modes + CRC sentinel:");
    await Exec(mysql, "DROP TABLE IF EXISTS it_modes");
    await mysql.SchemaState.RemoveStateAsync("it_modes");
    mysql.SetSyncMode(SyncMode.Production);

    // 10a CRC fast-path: first sync creates, second sync skips via matching CRC.
    var m1 = (await mysql.SyncTableAsync<ModesV1>(createBackup: false)).Value!;
    Check("modes v1 created (not skipped)", m1.Success && !m1.Skipped && m1.Operations.Count > 0,
        $"skipped={m1.Skipped} ops={m1.Operations.Count}");
    var crc1 = m1.SchemaCrc;
    var m1b = (await mysql.SyncTableAsync<ModesV1>(createBackup: false)).Value!;
    Check("modes v1 re-sync skipped via CRC", m1b.Skipped && m1b.SchemaCrc == crc1, $"skipped={m1b.Skipped}");
    Check("__schema_state row is Synced",
        (await mysql.SchemaState.GetStateAsync("it_modes"))?.Status == SchemaSyncStatus.Synced);

    // 10b Production never drops + flags DriftPending (model drops 'temp', DB keeps it).
    await Exec(mysql, "INSERT INTO it_modes (name, temp) VALUES ('x', 'y')");
    var prod = (await mysql.SyncTableAsync<ModesV2>(createBackup: false)).Value!;
    var colsModes = await ColumnNames(mysql, "it_modes");
    Check("Production keeps removed column 'temp'", colsModes.Contains("temp"));
    Check("Production reports DriftPending", prod.DriftPending);
    Check("state row is DriftPending",
        (await mysql.SchemaState.GetStateAsync("it_modes"))?.Status == SchemaSyncStatus.DriftPending);

    // 10c Migration drops the column and self-disables on a second pass.
    mysql.SetSyncMode(SyncMode.Migration);
    var mig = (await mysql.SyncSchemaAsync(typeof(ModesV2))).Value!["it_modes"];
    var colsAfter = await ColumnNames(mysql, "it_modes");
    Check("Migration drops removed column 'temp'", !colsAfter.Contains("temp"),
        $"cols=[{string.Join(",", colsAfter)}]");
    Check("state row back to Synced",
        (await mysql.SchemaState.GetStateAsync("it_modes"))?.Status == SchemaSyncStatus.Synced);
    var mig2 = (await mysql.SyncSchemaAsync(typeof(ModesV2))).Value!["it_modes"];
    Check("Migration re-run is a no-op (skipped)", mig2.Skipped);

    // 10d Developer drops a removed column immediately on a model change.
    await Exec(mysql, "DROP TABLE IF EXISTS it_dev");
    await mysql.SchemaState.RemoveStateAsync("it_dev");
    mysql.SetSyncMode(SyncMode.Developer);
    await mysql.SyncTableAsync<DevV1>(createBackup: false);
    var devSync = (await mysql.SyncTableAsync<DevV2>(createBackup: false)).Value!;
    var devCols = await ColumnNames(mysql, "it_dev");
    Check("Developer drops removed column immediately",
        !devCols.Contains("extra") && devSync.Operations.Any(o => o.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase)),
        $"cols=[{string.Join(",", devCols)}] ops=[{string.Join(" | ", devSync.Operations)}]");

    // ── 11. Concurrent schema sync (cross-node lock) ──────────────────────────
    Console.WriteLine("\nConcurrent schema sync (lock):");
    await Exec(mysql, "DROP TABLE IF EXISTS it_lock");
    await mysql.SchemaState.RemoveStateAsync("it_lock");
    mysql.SetSyncMode(SyncMode.Developer);
    var t1 = mysql.SyncSchemaAsync(typeof(LockEntity));
    var t2 = mysql.SyncSchemaAsync(typeof(LockEntity));
    var rr = await Task.WhenAll(t1, t2);
    Check("concurrent sync both succeed", rr.All(r => r.IsSuccess));
    var lockExists = (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_lock'")).Value;
    Check("it_lock created exactly once", lockExists == 1, $"got {lockExists}");

    // ── 12. Imperative migrations ─────────────────────────────────────────────
    Console.WriteLine("\nMigrations:");
    await Exec(mysql, "DROP TABLE IF EXISTS it_mig");
    await Exec(mysql, "DELETE FROM __migrations WHERE MigrationId LIKE '1.0.0/%'");
    mysql.RegisterMigration(new CreateMigTable());
    mysql.RegisterMigration(new SeedMig());

    var pending = await mysql.GetPendingMigrationsAsync();
    Check("2 migrations pending", pending.Count == 2, $"got {pending.Count}");
    Check("pending ordered by version", pending.Count == 2 && pending[0].Version.Order == 1 && pending[1].Version.Order == 2);

    var run = (await mysql.MigrateAsync()).Value!;
    Check("both migrations applied", run.Count == 2, $"got {run.Count}");
    var migCount = (await mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM it_mig")).Value;
    Check("migration applied schema + data (2 rows)", migCount == 2, $"got {migCount}");
    var run2 = (await mysql.MigrateAsync()).Value!;
    Check("re-run migrations is a no-op", run2.Count == 0, $"got {run2.Count}");
    Check("no pending after apply", (await mysql.GetPendingMigrationsAsync()).Count == 0);

    // ── 13. Rollback ──────────────────────────────────────────────────────────
    Console.WriteLine("\nRollback:");
    var rb = await mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
    Check("rollback succeeds", rb.IsSuccess, rb.Error?.Message);
    Check("rolled back 2 (newest-first)", rb.Value!.Count == 2);
    var migDropped = (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_mig'")).Value;
    Check("it_mig dropped by rollback", migDropped == 0, $"got {migDropped}");
    Check("both pending again after rollback", (await mysql.GetPendingMigrationsAsync()).Count == 2);

    // 13b A migration without DownAsync aborts the rollback cleanly (no partial changes).
    await mysql.MigrateAsync();                       // re-apply the two reversible ones
    mysql.RegisterMigration(new NoDownMig());         // 1.0.0/003 — no DownAsync override
    await mysql.MigrateAsync();                        // apply the no-down migration
    var rb2 = await mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
    Check("rollback aborts when a migration has no DownAsync", rb2.IsFailure, "expected failure");
    var migStillThere = (await mysql.SqlScalarAsync<long>(
        "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_mig'")).Value;
    Check("no partial rollback — it_mig still present", migStillThere == 1, $"got {migStillThere}");
}
finally
{
    await StopAsync();
}

Console.WriteLine($"\n{'='}=== {total - fails}/{total} passed ===");
if (fails > 0) Console.WriteLine($"{fails} FAILED");
return fails == 0 ? 0 : 1;

// ── helpers ────────────────────────────────────────────────────────────────────
static async Task Exec(MySQL2Library mysql, string sql) =>
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
        return true;
    }, "Default");

static async Task<HashSet<string>> ColumnNames(MySQL2Library mysql, string table) =>
    await mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t";
        cmd.Parameters.AddWithValue("@t", table);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }, "Default");

// ── entities ─────────────────────────────────────────────────────────────────
[Table(Name = "it_customer")]
public class Customer
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 100, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "country", DataType = DataType.VarChar, Size = 10, NotNull = true)] public string Country { get; set; } = "";
    [Column(Name = "is_vip", DataType = DataType.TinyInt, NotNull = true)] public bool IsVip { get; set; }
}

[Table(Name = "it_order")]
public class Order
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "customer_id", DataType = DataType.BigInt, NotNull = true)] public long CustomerId { get; set; }
    [Column(Name = "total", DataType = DataType.Decimal, Precision = 10, Scale = 2, NotNull = true)] public decimal Total { get; set; }
}

[Table(Name = "it_shipment")]
public class Shipment
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "order_id", DataType = DataType.BigInt, NotNull = true)] public long OrderId { get; set; }
    [Column(Name = "status", DataType = DataType.VarChar, Size = 20, NotNull = true)] public string Status { get; set; } = "";
}

public class OrderView
{
    public long OrderId { get; set; }
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
}

[Table(Name = "it_soft")]
[SoftDelete(nameof(DeletedUtc))]
public class SoftThing
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "deleted_utc", DataType = DataType.DateTime)] public DateTime? DeletedUtc { get; set; }
}

[Table(Name = "it_rename")]
public class RenameV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "email", DataType = DataType.VarChar, Size = 200, NotNull = true)] public string Email { get; set; } = "";
}

[Table(Name = "it_rename")]
public class RenameV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "email_address", DataType = DataType.VarChar, Size = 200, PreviousName = "email", NotNull = true)] public string EmailAddress { get; set; } = "";
}

// ── entities for sync-mode / CRC tests ─────────────────────────────────────────
[Table(Name = "it_modes")]
public class ModesV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "temp", DataType = DataType.VarChar, Size = 50)] public string? Temp { get; set; }
}

[Table(Name = "it_modes")]
public class ModesV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "it_dev")]
public class DevV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "extra", DataType = DataType.VarChar, Size = 50)] public string? Extra { get; set; }
}

[Table(Name = "it_dev")]
public class DevV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "it_lock")]
public class LockEntity
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "val", DataType = DataType.VarChar, Size = 50)] public string? Val { get; set; }
}

[Table(Name = "it_mig")]
public class MigEntity
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", DataType = DataType.VarChar, Size = 50, NotNull = true)] public string Label { get; set; } = "";
}

// ── migrations for the imperative-runner tests ─────────────────────────────────
public sealed class CreateMigTable : Migration
{
    public CreateMigTable() : base("1.0.0", 1, "create it_mig and seed one row") { }
    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct)
    {
        await ctx.SyncTableAsync<MigEntity>(ct);
        await ctx.ExecuteAsync("INSERT INTO it_mig (label) VALUES ('from-up')", ct: ct);
    }
    public override Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("DROP TABLE IF EXISTS it_mig", ct: ct);
}

public sealed class SeedMig : Migration
{
    public SeedMig() : base("1.0.0", 2, "seed an extra row") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("INSERT INTO it_mig (label) VALUES ('seed')", ct: ct);
    public override Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("DELETE FROM it_mig WHERE label = 'seed'", ct: ct);
}

public sealed class NoDownMig : Migration
{
    public NoDownMig() : base("1.0.0", 3, "irreversible insert") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        ctx.ExecuteAsync("INSERT INTO it_mig (label) VALUES ('nodown')", ct: ct);
    // No DownAsync override → rollback unsupported.
}

// ── fake coordinator for the multi-node test ───────────────────────────────────
public sealed class FakeCoordinator : ICacheCoordinator
{
    public readonly List<string> Published = [];
    private Action<string>? _handler;
    public bool HandlerWired => _handler is not null;
    public void FireInvalidation(string table) => _handler?.Invoke(table);

    public Task PublishInvalidationAsync(string tableName, CancellationToken ct = default)
    {
        Published.Add(tableName);
        return Task.CompletedTask;
    }
    public void OnInvalidation(Action<string> handler) => _handler = handler;
    public Task<bool> TryAcquireRefreshLeaseAsync(string poolName, TimeSpan lease, CancellationToken ct = default)
        => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
