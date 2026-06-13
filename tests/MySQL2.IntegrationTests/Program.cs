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

const string Fw = @"C:\Users\claus.HLAB-DC\AppData\Local\clmaria\clogic";

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
File.WriteAllText(Path.Combine(cfgDir, "config.mysql.json"), """
{
  "databases": {
    "Default": {
      "enabled": true,
      "host": "127.0.0.1",
      "port": 3310,
      "database": "cl_test",
      "username": "root",
      "password": "",
      "schemaSyncLevel": "full",
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
