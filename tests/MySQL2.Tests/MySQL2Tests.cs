using System.Net.Sockets;
using CodeLogic;                    // Libraries, CodeLogicOptions
using CL.MySQL2;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using Xunit;
using LinqExpr = System.Linq.Expressions;

namespace MySQL2.Tests;

// ── Integration tests for CL.MySQL2 against a local MariaDB/MySQL ──────────────────
// Ported from the old tests/MySQL2.IntegrationTests console runner. Boots the real
// CodeLogic runtime (process-wide singleton), so EVERY DB test lives in this one class
// behind a single shared fixture that boots ONCE. The [Collection] attribute serializes
// it against any other CodeLogic-touching test assembly.
//
// The live-DB tests are ENV-GATED: a quick TCP probe to host:port decides availability.
// When the DB is NOT reachable, the [DbFact] tests SKIP (not fail) so CI stays green.
// Connection is env-driven (CL_MYSQL2_TEST_*); defaults target a local MariaDB on
// 127.0.0.1:3310 (db cl_test, user root, no password).

// ── Connection settings (env-driven, shared by probe + fixture) ───────────────────

internal static class DbEnv
{
    public static string Get(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    public static string Host => Get("CL_MYSQL2_TEST_HOST", "127.0.0.1");
    public static string Port => Get("CL_MYSQL2_TEST_PORT", "3310");
    public static string Db   => Get("CL_MYSQL2_TEST_DB",   "cl_test");
    public static string User => Get("CL_MYSQL2_TEST_USER", "root");
    public static string Pass => Get("CL_MYSQL2_TEST_PASS", "");
    public static string Root => Get("CL_MYSQL2_TEST_ROOT",
        Path.Combine(Path.GetTempPath(), "cl_mysql2_tests_" + Guid.NewGuid().ToString("N")));

    // Computed once: is the DB reachable via a short TCP connect?
    private static readonly Lazy<bool> _reachable = new(Probe);
    public static bool Reachable => _reachable.Value;

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(Host, int.Parse(Port));
            return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch { return false; }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips the test unless a MySQL/MariaDB
/// is reachable on the configured host:port. xUnit 2.9.3 has no runtime
/// <c>Assert.Skip</c>, so the skip decision is made here (at discovery time) via a quick
/// TCP probe and reported as a proper "Skipped".
/// </summary>
internal sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (!DbEnv.Reachable)
            Skip = $"MySQL not reachable on {DbEnv.Host}:{DbEnv.Port}";
    }
}

// ── Shared one-time-boot fixture ───────────────────────────────────────────────────

public sealed class MySQL2RuntimeFixture : IAsyncLifetime
{
    public MySQL2Library Mysql { get; private set; } = null!;
    public bool Booted { get; private set; }

    // Seeded ids, exposed so read-only facts can assert against the shared state.
    public long AliceId { get; private set; }
    public long BobId { get; private set; }
    public long Order1Id { get; private set; }
    public long Order2Id { get; private set; }
    public long Order3Id { get; private set; }

    private readonly string _root = DbEnv.Root;

    public async Task InitializeAsync()
    {
        // Only boot the runtime when the DB is actually reachable; otherwise leave the
        // fixture un-booted and every [DbFact] skips.
        if (!DbEnv.Reachable)
            return;

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = _root;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var cfgDir = Path.Combine(_root, "Libraries", "CL.MySQL2");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.mysql.json"), $$"""
        {
          "databases": {
            "Default": {
              "enabled": true,
              "host": "{{DbEnv.Host}}",
              "port": {{DbEnv.Port}},
              "database": "{{DbEnv.Db}}",
              "username": "{{DbEnv.User}}",
              "password": "{{DbEnv.Pass}}",
              "syncMode": "developer",
              "characterSet": "utf8mb4"
            }
          }
        }
        """);

        await Libraries.LoadAsync<MySQL2Library>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Mysql = Libraries.Get<MySQL2Library>()
            ?? throw new InvalidOperationException("MySQL2Library not available after start.");
        Booted = true;

        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        // Clean slate for the shared (read-only) seed tables.
        await Exec("DROP TABLE IF EXISTS it_shipment, it_order, it_customer, it_rename");

        await Mysql.SyncTableAsync<Customer>(createBackup: false);
        await Mysql.SyncTableAsync<Order>(createBackup: false);
        await Mysql.SyncTableAsync<Shipment>(createBackup: false);

        var cust = Mysql.GetRepository<Customer>();
        var alice = (await cust.InsertAsync(new Customer { Name = "Alice", Country = "DK", IsVip = true })).Value!;
        var bob   = (await cust.InsertAsync(new Customer { Name = "Bob",   Country = "SE", IsVip = false })).Value!;
        AliceId = alice.Id; BobId = bob.Id;

        var ord = Mysql.GetRepository<Order>();
        Order1Id = (await ord.InsertAsync(new Order { CustomerId = alice.Id, Total = 150m })).Value!.Id;
        Order2Id = (await ord.InsertAsync(new Order { CustomerId = alice.Id, Total = 40m  })).Value!.Id;
        Order3Id = (await ord.InsertAsync(new Order { CustomerId = bob.Id,   Total = 999m })).Value!.Id;

        var ship = Mysql.GetRepository<Shipment>();
        await ship.InsertAsync(new Shipment { OrderId = Order1Id, Status = "sent" });
        await ship.InsertAsync(new Shipment { OrderId = Order3Id, Status = "pending" });
    }

    public Task Exec(string sql) =>
        Mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
            return true;
        }, "Default");

    public Task<HashSet<string>> ColumnNames(string table) =>
        Mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t";
            cmd.Parameters.AddWithValue("@t", table);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
            return set;
        }, "Default");

    public async Task DisposeAsync()
    {
        if (Booted)
        {
            try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* lingering files on Windows; ignore */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<MySQL2RuntimeFixture> { }

// ── Tests ──────────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class MySQL2Tests
{
    private readonly MySQL2RuntimeFixture _fx;
    private MySQL2Library Mysql => _fx.Mysql;

    public MySQL2Tests(MySQL2RuntimeFixture fx) => _fx = fx;

    // ── Schema sync (read-only, against the seeded tables) ────────────────────────
    [DbFact]
    public async Task SchemaSync_works()
    {
        Assert.True((await Mysql.SyncTableAsync<Customer>(createBackup: false)).IsSuccess);
        Assert.True((await Mysql.SyncTableAsync<Order>(createBackup: false)).IsSuccess);
        Assert.True((await Mysql.SyncTableAsync<Shipment>(createBackup: false)).IsSuccess);
    }

    // ── Typed JOINs ───────────────────────────────────────────────────────────────
    [DbFact]
    public async Task TypedJoins_work()
    {
        var joinRes = await Mysql.Query<Order>()
            .Where(o => o.Total > 100m)
            .Join<Customer, long, OrderView>(
                o => o.CustomerId,
                c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total })
            .OrderByDescending((o, c) => o.Total)
            .ToListAsync();
        Assert.True(joinRes.IsSuccess, joinRes.Error?.Message);

        var jv = joinRes.Value ?? [];
        Assert.Equal(2, jv.Count);                                  // Total>100 → o1, o3
        Assert.Equal("Bob", jv[0].Customer);                        // ordered desc by total
        Assert.Equal(999m, jv[0].Total);
        Assert.Contains(jv, v => v.OrderId == _fx.Order1Id && v.Customer == "Alice");

        // LEFT join counts all 3 orders.
        var leftCount = (await Mysql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total },
                JoinType.Left)
            .CountAsync()).Value;
        Assert.Equal(3, leftCount);
    }

    // ── EXISTS / IN subqueries ────────────────────────────────────────────────────
    [DbFact]
    public async Task Subqueries_work()
    {
        var existsCount = (await Mysql.Query<Order>()
            .WhereExists<Shipment>((o, s) => s.OrderId == o.Id && s.Status == "sent")
            .CountAsync()).Value;
        Assert.Equal(1, existsCount);                               // only o1

        var notExistsCount = (await Mysql.Query<Order>()
            .WhereNotExists<Shipment>((o, s) => s.OrderId == o.Id)
            .CountAsync()).Value;
        Assert.Equal(1, notExistsCount);                           // o2 has no shipment

        var inRes = await Mysql.Query<Order>()
            .WhereIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip)
            .ToListAsync();
        Assert.True(inRes.IsSuccess, inRes.Error?.Message);
        Assert.Equal(2, inRes.Value!.Count);                       // Alice's o1, o2

        var notInCount = (await Mysql.Query<Order>()
            .WhereNotIn<Customer, long>(o => o.CustomerId, c => c.Id, c => c.IsVip)
            .CountAsync()).Value;
        Assert.Equal(1, notInCount);                               // Bob's o3
    }

    // ── Regression: 11+ params in one predicate (param rekey @p1 vs @p10) ─────────
    // A single predicate emitting 11+ parameters used to corrupt the SQL because the
    // rekey loop replaced "@p1" inside "@p10"/"@p11" (substring collision). Build a
    // 12-term OR chain (params @p0…@p11) and confirm it executes and matches exactly.
    [DbFact]
    public async Task ParamRekey_12param_regression()
    {
        var ord = Mysql.GetRepository<Order>();
        var manyIds = new List<long>();
        for (var i = 0; i < 12; i++)
            manyIds.Add((await ord.InsertAsync(new Order { CustomerId = _fx.AliceId, Total = 10m + i })).Value!.Id);

        try
        {
            var p = LinqExpr.Expression.Parameter(typeof(Order), "o");
            var idProp = LinqExpr.Expression.Property(p, nameof(Order.Id));
            LinqExpr.Expression body = LinqExpr.Expression.Equal(
                idProp, LinqExpr.Expression.Constant(manyIds[0]));
            for (var i = 1; i < manyIds.Count; i++)
                body = LinqExpr.Expression.OrElse(body,
                    LinqExpr.Expression.Equal(idProp, LinqExpr.Expression.Constant(manyIds[i])));
            var manyPredicate = LinqExpr.Expression.Lambda<Func<Order, bool>>(body, p);

            var manyParamRes = await Mysql.Query<Order>().Where(manyPredicate).ToListAsync();
            Assert.True(manyParamRes.IsSuccess, manyParamRes.Error?.Message);
            Assert.Equal(12, manyParamRes.Value!.Count);
        }
        finally
        {
            // Clean up the 12 extra orders so other facts see only the seeded rows.
            foreach (var id in manyIds)
                await ord.DeleteAsync(id);
        }
    }

    // ── Column rename (data preserved) — uses its own it_rename table ─────────────
    [DbFact]
    public async Task ColumnRename_preservesData()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_rename");
        await Mysql.SchemaState.RemoveStateAsync("it_rename");

        Assert.True((await Mysql.SyncTableAsync<RenameV1>(createBackup: false)).IsSuccess);
        var rrepo = Mysql.GetRepository<RenameV1>();
        await rrepo.InsertAsync(new RenameV1 { Email = "alice@example.com" });

        // Re-sync the SAME table with a renamed column → CHANGE COLUMN email email_address.
        var renameSync = await Mysql.SyncTableAsync<RenameV2>(createBackup: false);
        Assert.True(renameSync.IsSuccess, renameSync.Error?.Message);

        var renamedOps = renameSync.Value?.Operations ?? [];
        Assert.Contains(renamedOps, op => op.Contains("CHANGE COLUMN", StringComparison.OrdinalIgnoreCase));

        var renamedRow = (await Mysql.Query<RenameV2>().FirstOrDefaultAsync()).Value;
        Assert.NotNull(renamedRow);
        Assert.Equal("alice@example.com", renamedRow!.EmailAddress);

        var cols = await _fx.ColumnNames("it_rename");
        Assert.DoesNotContain("email", cols);
        Assert.Contains("email_address", cols);
    }

    // ── Result cache + stampede protection ───────────────────────────────────────
    [DbFact]
    public async Task ResultCache_works()
    {
        var c1 = await Mysql.Query<Customer>().WithCache(TimeSpan.FromMinutes(1)).ToListAsync();
        var c2 = await Mysql.Query<Customer>().WithCache(TimeSpan.FromMinutes(1)).ToListAsync();
        Assert.True(c1.IsSuccess && c2.IsSuccess);
        Assert.Equal(c1.Value!.Count, c2.Value!.Count);
        Assert.Contains("it_customer", Mysql.GetCacheStats().EntriesByTable.Keys);

        // Stampede protection: 20 concurrent cold reads collapse and stay consistent.
        var herd = Enumerable.Range(0, 20)
            .Select(_ => Mysql.Query<Customer>().Where(c => c.Id > 0)
                .WithCache(TimeSpan.FromMinutes(1)).ToListAsync());
        var herdResults = await Task.WhenAll(herd);
        Assert.All(herdResults, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.Equal(herdResults[0].Value!.Count, r.Value!.Count);
        });
    }

    // ── Raw SQL escape hatch ──────────────────────────────────────────────────────
    [DbFact]
    public async Task RawSql_works()
    {
        var raw = await Mysql.SqlQueryAsync<Customer>(
            "SELECT * FROM it_customer WHERE country = @c ORDER BY id",
            new Dictionary<string, object?> { ["@c"] = "DK" });
        Assert.True(raw.IsSuccess, raw.Error?.Message);
        Assert.Single(raw.Value!);
        Assert.Equal("Alice", raw.Value![0].Name);

        var scalar = await Mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM it_order");
        Assert.True(scalar.IsSuccess);
        Assert.Equal(3, scalar.Value);

        var aff = await Mysql.ExecuteSqlAsync("UPDATE it_order SET total = total WHERE total < 0");
        Assert.True(aff.IsSuccess, aff.Error?.Message);
        Assert.Equal(0, aff.Value);
    }

    // ── Soft deletes — uses its own it_soft table ────────────────────────────────
    [DbFact]
    public async Task SoftDelete_works()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_soft");
        Assert.True((await Mysql.SyncTableAsync<SoftThing>(createBackup: false)).IsSuccess);

        var sr = Mysql.GetRepository<SoftThing>();
        var s1 = (await sr.InsertAsync(new SoftThing { Label = "keep" })).Value!;
        var s2 = (await sr.InsertAsync(new SoftThing { Label = "remove" })).Value!;

        var del = await sr.DeleteAsync(s2.Id);
        Assert.True(del.IsSuccess && del.Value);

        var visible = (await Mysql.Query<SoftThing>().ToListAsync()).Value ?? [];
        Assert.Single(visible);
        Assert.Equal(s1.Id, visible[0].Id);

        var allRows = (await Mysql.Query<SoftThing>().IncludeDeleted().ToListAsync()).Value ?? [];
        Assert.Equal(2, allRows.Count);

        Assert.Null((await sr.GetByIdAsync(s2.Id)).Value);

        var deletedRow = (await Mysql.Query<SoftThing>().IncludeDeleted()
            .Where(x => x.Id == s2.Id).FirstOrDefaultAsync()).Value;
        Assert.NotNull(deletedRow!.DeletedUtc);

        Assert.True((await sr.HardDeleteAsync(s2.Id)).Value);
        Assert.Equal(1, (await Mysql.Query<SoftThing>().IncludeDeleted().CountAsync()).Value);
    }

    // ── Multi-node cache coordinator (fan-out on mutation) ───────────────────────
    [DbFact]
    public async Task CacheCoordinator_multiNode()
    {
        var fake = new FakeCoordinator();
        QueryCache.UseCoordinator(fake);
        Assert.True(fake.HandlerWired);

        // A bulk update on it_order must invalidate + fan out to peers.
        await Mysql.Query<Order>().Where(o => o.Total < 0m).UpdateAsync(o => new Order { Total = 0m });
        Assert.Contains("it_order", fake.Published);

        // A peer broadcast bumps the local version without re-publishing.
        fake.Published.Clear();
        var stats = Mysql.GetCacheStats();
        var before = stats.TableVersions.TryGetValue("it_customer", out var bv) ? bv : 0;
        fake.FireInvalidation("it_customer");
        var after = Mysql.GetCacheStats().TableVersions.TryGetValue("it_customer", out var av) ? av : 0;
        Assert.Equal(before + 1, after);
        Assert.DoesNotContain("it_customer", fake.Published);
    }

    // ── Sync modes + CRC sentinel — own it_modes / it_dev tables ─────────────────
    [DbFact]
    public async Task SyncModes_work()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_modes");
        await Mysql.SchemaState.RemoveStateAsync("it_modes");
        Mysql.SetSyncMode(SyncMode.Production);

        try
        {
            // CRC fast-path: first sync creates, second sync skips via matching CRC.
            var m1 = (await Mysql.SyncTableAsync<ModesV1>(createBackup: false)).Value!;
            Assert.True(m1.Success && !m1.Skipped && m1.Operations.Count > 0);
            var crc1 = m1.SchemaCrc;
            var m1b = (await Mysql.SyncTableAsync<ModesV1>(createBackup: false)).Value!;
            Assert.True(m1b.Skipped);
            Assert.Equal(crc1, m1b.SchemaCrc);
            Assert.Equal(SchemaSyncStatus.Synced, (await Mysql.SchemaState.GetStateAsync("it_modes"))?.Status);

            // Production never drops + flags DriftPending (model drops 'temp', DB keeps it).
            await _fx.Exec("INSERT INTO it_modes (name, temp) VALUES ('x', 'y')");
            var prod = (await Mysql.SyncTableAsync<ModesV2>(createBackup: false)).Value!;
            var colsModes = await _fx.ColumnNames("it_modes");
            Assert.Contains("temp", colsModes);
            Assert.True(prod.DriftPending);
            Assert.Equal(SchemaSyncStatus.DriftPending, (await Mysql.SchemaState.GetStateAsync("it_modes"))?.Status);

            // Migration drops the column and self-disables on a second pass.
            Mysql.SetSyncMode(SyncMode.Migration);
            _ = (await Mysql.SyncSchemaAsync(typeof(ModesV2))).Value!["it_modes"];
            var colsAfter = await _fx.ColumnNames("it_modes");
            Assert.DoesNotContain("temp", colsAfter);
            Assert.Equal(SchemaSyncStatus.Synced, (await Mysql.SchemaState.GetStateAsync("it_modes"))?.Status);
            var mig2 = (await Mysql.SyncSchemaAsync(typeof(ModesV2))).Value!["it_modes"];
            Assert.True(mig2.Skipped);

            // Developer drops a removed column immediately on a model change.
            await _fx.Exec("DROP TABLE IF EXISTS it_dev");
            await Mysql.SchemaState.RemoveStateAsync("it_dev");
            Mysql.SetSyncMode(SyncMode.Developer);
            await Mysql.SyncTableAsync<DevV1>(createBackup: false);
            var devSync = (await Mysql.SyncTableAsync<DevV2>(createBackup: false)).Value!;
            var devCols = await _fx.ColumnNames("it_dev");
            Assert.DoesNotContain("extra", devCols);
            Assert.Contains(devSync.Operations, o => o.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Mysql.SetSyncMode(SyncMode.Developer);
        }
    }

    // ── Concurrent schema sync (cross-node lock) — own it_lock table ─────────────
    [DbFact]
    public async Task ConcurrentSchemaSync_locksToOnce()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_lock");
        await Mysql.SchemaState.RemoveStateAsync("it_lock");
        Mysql.SetSyncMode(SyncMode.Developer);

        var t1 = Mysql.SyncSchemaAsync(typeof(LockEntity));
        var t2 = Mysql.SyncSchemaAsync(typeof(LockEntity));
        var rr = await Task.WhenAll(t1, t2);
        Assert.All(rr, r => Assert.True(r.IsSuccess));

        var lockExists = (await Mysql.SqlScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_lock'")).Value;
        Assert.Equal(1, lockExists);
    }

    // ── Imperative migrations + rollback — own it_mig table ──────────────────────
    [DbFact]
    public async Task Migrations_work()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_mig");
        await _fx.Exec("DELETE FROM __migrations WHERE MigrationId LIKE '1.0.0/%'");
        Mysql.RegisterMigration(new CreateMigTable());
        Mysql.RegisterMigration(new SeedMig());

        var pending = await Mysql.GetPendingMigrationsAsync();
        Assert.Equal(2, pending.Count);
        Assert.Equal(1, pending[0].Version.Order);
        Assert.Equal(2, pending[1].Version.Order);

        var run = (await Mysql.MigrateAsync()).Value!;
        Assert.Equal(2, run.Count);
        Assert.Equal(2, (await Mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM it_mig")).Value);

        var run2 = (await Mysql.MigrateAsync()).Value!;
        Assert.Equal(0, run2.Count);
        Assert.Empty(await Mysql.GetPendingMigrationsAsync());

        // Rollback (newest-first).
        var rb = await Mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
        Assert.True(rb.IsSuccess, rb.Error?.Message);
        Assert.Equal(2, rb.Value!.Count);
        Assert.Equal(0, (await Mysql.SqlScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_mig'")).Value);
        Assert.Equal(2, (await Mysql.GetPendingMigrationsAsync()).Count);

        // A migration without DownAsync aborts the rollback cleanly (no partial changes).
        await Mysql.MigrateAsync();                       // re-apply the two reversible ones
        Mysql.RegisterMigration(new NoDownMig());         // 1.0.0/003 — no DownAsync override
        await Mysql.MigrateAsync();                        // apply the no-down migration
        var rb2 = await Mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
        Assert.True(rb2.IsFailure);
        Assert.Equal(1, (await Mysql.SqlScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='it_mig'")).Value);
    }
}

// ── Entities ──────────────────────────────────────────────────────────────────────

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

// ── Entities for sync-mode / CRC tests ──────────────────────────────────────────────
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

// ── Migrations for the imperative-runner tests ──────────────────────────────────────
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

// ── Fake coordinator for the multi-node test ────────────────────────────────────────
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
