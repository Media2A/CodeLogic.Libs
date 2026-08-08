using System.Net.Sockets;
using CodeLogic;                    // Libraries, CodeLogicOptions
using CL.MSSQL;
using CL.MSSQL.Models;
using CL.MSSQL.Services;
using Xunit;
using LinqExpr = System.Linq.Expressions;
using Microsoft.Data.SqlClient;

namespace MSSQL.Tests;

// ── Integration tests for CL.MSSQL against a SQL Server 2019+ ──────────────────
// Ported from the old tests/MSSQL.IntegrationTests console runner. Boots the real
// CodeLogic runtime (process-wide singleton), so EVERY DB test lives in this one class
// behind a single shared fixture that boots ONCE. The [Collection] attribute serializes
// it against any other CodeLogic-touching test assembly.
//
// The live-DB tests are ENV-GATED: a quick TCP probe to host:port decides availability.
// When the DB is NOT reachable, the [DbFact] tests SKIP (not fail) so CI stays green.
// Connection is env-driven (CL_MSSQL_TEST_CONNECTION_STRING); defaults target a SQL Server on
// 127.0.0.1:3310 (db cl_test, user root, no password).

// ── Connection settings (env-driven, shared by probe + fixture) ───────────────────

internal static class DbEnv
{
    public static string Get(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

    public static string ConnectionString => Get("CL_MSSQL_TEST_CONNECTION_STRING", "Server=127.0.0.1,1433;Database=cl_test;User ID=sa;Password=Your_strong_password123!;Encrypt=False;TrustServerCertificate=True");
    public static string Root => Get("CL_MSSQL_TEST_ROOT",
        Path.Combine(Path.GetTempPath(), "cl_mssql_tests_" + Guid.NewGuid().ToString("N")));

    // Computed once: is the DB reachable via a short TCP connect?
    private static readonly Lazy<bool> _reachable = new(Probe);
    public static bool Reachable => _reachable.Value;

    private static bool Probe()
    {
        try
        {
            using var connection = new SqlConnection(ConnectionString);
            var connect = connection.OpenAsync();
            return connect.Wait(TimeSpan.FromSeconds(3)) && connection.State == System.Data.ConnectionState.Open;
        }
        catch { return false; }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips the test unless a SQL Server
/// is reachable on the configured host:port. xUnit 2.9.3 has no runtime
/// <c>Assert.Skip</c>, so the skip decision is made here (at discovery time) via a quick
/// TCP probe and reported as a proper "Skipped".
/// </summary>
internal sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (!DbEnv.Reachable)
            Skip = "SQL Server test connection is unavailable";
    }
}

// ── Shared one-time-boot fixture ───────────────────────────────────────────────────

public sealed class MSSQLRuntimeFixture : IAsyncLifetime
{
    public MSSQLLibrary Mysql { get; private set; } = null!;
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

        var cfgDir = Path.Combine(_root, "Libraries", "CL.MSSQL");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.mssql.json"), $$"""
        {
          "databases": {
            "Default": {
              "enabled": true,
              "connectionString": "{{DbEnv.ConnectionString.Replace("\\", "\\\\").Replace("\"", "\\\"")}}",
              "syncMode": "developer"
            }
          }
        }
        """);

        await Libraries.LoadAsync<MSSQLLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Mysql = Libraries.Get<MSSQLLibrary>()
            ?? throw new InvalidOperationException("MSSQLLibrary not available after start.");
        Booted = true;

        await SeedAsync();
    }

    private async Task SeedAsync()
    {
        // Clean slate for the shared (read-only) seed tables.
        await Exec("DROP TABLE IF EXISTS [dbo].[it_shipment]; DROP TABLE IF EXISTS [dbo].[it_order]; DROP TABLE IF EXISTS [dbo].[it_customer]; DROP TABLE IF EXISTS [dbo].[it_cursor]; DROP TABLE IF EXISTS [dbo].[it_rename];");

        await Mysql.SyncTableAsync<Customer>(createBackup: false);
        await Mysql.SyncTableAsync<Order>(createBackup: false);
        await Mysql.SyncTableAsync<Shipment>(createBackup: false);
        await Mysql.SyncTableAsync<CursorRow>(createBackup: false);

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

        var cursor = Mysql.GetRepository<CursorRow>();
        await cursor.InsertAsync(new CursorRow { Rank = null, Bucket = "b" });
        await cursor.InsertAsync(new CursorRow { Rank = null, Bucket = "a" });
        await cursor.InsertAsync(new CursorRow { Rank = 1, Bucket = "b" });
        await cursor.InsertAsync(new CursorRow { Rank = 1, Bucket = "a" });
        await cursor.InsertAsync(new CursorRow { Rank = 1, Bucket = "a" });
        await cursor.InsertAsync(new CursorRow { Rank = 2, Bucket = "a" });
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
            cmd.CommandText = "SELECT c.name FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='dbo' AND t.name=@t";
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
        if (Environment.GetEnvironmentVariable("CL_MSSQL_TEST_KEEP_ROOT") == "1") return;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* lingering files on Windows; ignore */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<MSSQLRuntimeFixture> { }

// ── Tests ──────────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class MSSQLTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mysql => _fx.Mysql;

    public MSSQLTests(MSSQLRuntimeFixture fx) => _fx = fx;

    // ── Schema sync (read-only, against the seeded tables) ────────────────────────
    [DbFact]
    public async Task SchemaSync_works()
    {
        Assert.True((await Mysql.SyncTableAsync<Customer>(createBackup: false)).IsSuccess);
        Assert.True((await Mysql.SyncTableAsync<Order>(createBackup: false)).IsSuccess);
        Assert.True((await Mysql.SyncTableAsync<Shipment>(createBackup: false)).IsSuccess);
    }

    [DbFact]
    public async Task Generated_sql_escapes_schema_table_and_column_identifiers()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [odd/schema]]x].[odd table]]x]");
        await Mysql.SchemaState.RemoveStateAsync("odd table]x");

        var sync = await Mysql.SyncTableAsync<OddIdentifierRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);

        var repo = Mysql.GetRepository<OddIdentifierRow>();
        var first = (await repo.InsertAsync(new OddIdentifierRow { NaturalKey = "one", DisplayValue = "initial" })).Value!;
        Assert.True(first.Id > 0);

        first.DisplayValue = "repository update";
        Assert.True((await repo.UpdateAsync(first)).IsSuccess);
        Assert.Equal("repository update", (await repo.GetByIdAsync(first.Id)).Value!.DisplayValue);

        Assert.True((await repo.UpsertAsync(new OddIdentifierRow { NaturalKey = "one", DisplayValue = "upserted" })).IsSuccess);
        Assert.Equal(2, (await repo.InsertManyAsync([
            new OddIdentifierRow { NaturalKey = "two", DisplayValue = "second" },
            new OddIdentifierRow { NaturalKey = "three", DisplayValue = "third" }
        ])).Value);

        Assert.Equal(1, (await Mysql.Query<OddIdentifierRow>()
            .Where(row => row.NaturalKey == "one")
            .UpdateAsync(new Dictionary<string, object?> { [nameof(OddIdentifierRow.DisplayValue)] = "set update" })).Value);

        var rows = (await Mysql.Query<OddIdentifierRow>()
            .Where(row => row.DisplayValue.Contains("update"))
            .OrderBy(row => row.DisplayValue)
            .ToListAsync()).Value!;
        Assert.Single(rows);
        Assert.Equal("set update", rows[0].DisplayValue);

        Assert.True((await Mysql.BackupManager.BackupTableSchemaAsync("odd/schema]x.odd table]x")).Value);
        Assert.True(File.Exists(Mysql.BackupManager.GetLatestBackupFile("odd/schema]x.odd table]x")));
    }

    [DbFact]
    public async Task Persistence_batches_upserts_counters_and_transactions_work()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_persist]");
        await Mysql.SchemaState.RemoveStateAsync("it_persist");
        Assert.True((await Mysql.SyncTableAsync<PersistRow>(createBackup: false)).IsSuccess);
        var repo = Mysql.GetRepository<PersistRow>();

        var first = (await repo.InsertAsync(new PersistRow { Key = "one", Amount = 10m, Counter = 1, Enabled = true })).Value!;
        Assert.True(first.Id > 0);
        Assert.Equal(3, (await repo.InsertManyAsync([
            new PersistRow { Key = "two", Amount = 20m },
            new PersistRow { Key = "three", Amount = 30m },
            new PersistRow { Key = "four", Amount = 40m }
        ])).Value);

        var upserted = (await repo.UpsertAsync(new PersistRow { Key = "one", Amount = 15m, Counter = 2, Enabled = true })).Value!;
        Assert.Equal(first.Id, upserted.Id);
        Assert.Equal(2, (await repo.UpsertManyAsync([
            new PersistRow { Key = "two", Amount = 22m },
            new PersistRow { Key = "five", Amount = 50m }
        ])).Value);
        Assert.Equal(1, (await repo.UpsertWithIncrementsAsync(
            new PersistRow { Key = "one", Counter = 3 }, [nameof(PersistRow.Counter)])).Value);

        var one = (await repo.GetByColumnAsync(nameof(PersistRow.Key), "one")).Value!.Single();
        Assert.Equal(5, one.Counter);
        one.Amount = 19m;
        Assert.True((await repo.UpdateAsync(one)).IsSuccess);
        Assert.Equal(19m, (await repo.GetByIdAsync(one.Id)).Value!.Amount);
        Assert.Equal(2, (await repo.GetPagedAsync(1, 2)).Value!.Items.Count);

        await using (var transaction = await Mysql.BeginTransactionAsync())
        {
            var transactional = new Repository<PersistRow>(Mysql.ConnectionManager, null, transaction);
            Assert.True((await transactional.InsertAsync(new PersistRow { Key = "rolled-back" })).IsSuccess);
            await transaction.RollbackAsync();
        }
        Assert.Empty((await repo.GetByColumnAsync(nameof(PersistRow.Key), "rolled-back")).Value!);

        Assert.True((await repo.DeleteAsync(first.Id)).Value);
        Assert.Null((await repo.GetByIdAsync(first.Id)).Value);
    }

    [DbFact]
    public async Task Projections_grouping_offset_paging_health_and_named_connections_work()
    {
        var projected = (await Mysql.Query<Order>()
            .Where(order => order.Total > 0)
            .Select(order => new OrderView { OrderId = order.Id, Total = order.Total })
            .ToListAsync()).Value!;
        Assert.Equal(3, projected.Count);

        var grouped = (await Mysql.Query<Order>()
            .GroupBy(order => order.CustomerId)
            .Select(group => new CustomerTotal { CustomerId = group.Key, Count = group.Count(), Total = group.Sum(order => order.Total) })
            .ToListAsync()).Value!;
        Assert.Equal(2, grouped.Count);
        Assert.Equal(3, grouped.Sum(group => group.Count));

        var offset = (await Mysql.Query<Order>().Skip(1).Take(1).ToListAsync()).Value!;
        Assert.Single(offset);
        Assert.True(await Mysql.ConnectionManager.TestConnectionAsync());

        var config = Mysql.ConnectionManager.GetConfiguration()!;
        Mysql.ConnectionManager.RegisterConfiguration(config, "Secondary");
        Assert.Equal(3, (await Mysql.GetRepository<Order>("Secondary").CountAsync()).Value);
    }

    [DbFact]
    public async Task Parameterized_estimated_plan_capture_works()
    {
        const string sql = "SELECT * FROM [dbo].[it_order] WHERE [total] >= @minimum";
        var parameters = new Dictionary<string, object?> { ["@minimum"] = 10m };
        Assert.True((await Mysql.SqlQueryAsync<Order>(sql, parameters)).IsSuccess);
        Assert.True((await Mysql.ExecuteSqlAsync(
            "EXEC sys.sp_executesql N'SELECT * FROM [dbo].[it_order] WHERE [total] >= @minimum', N'@minimum decimal(18,2)', @minimum=10")).IsSuccess);
        var plan = await QueryObservability.CaptureEstimatedPlanAsync(
            "Default",
            sql,
            connections: Mysql.ConnectionManager);
        Assert.NotNull(plan);
        Assert.Contains("ShowPlanXML", plan, StringComparison.OrdinalIgnoreCase);
    }

    [DbFact]
    public async Task Catalog_backup_and_restore_work()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_backup]");
        await Mysql.SchemaState.RemoveStateAsync("it_backup");
        Assert.True((await Mysql.SyncTableAsync<BackupRow>(createBackup: false)).IsSuccess);
        Assert.True((await Mysql.GetRepository<BackupRow>().InsertAsync(new BackupRow { Name = "before", Payload = "{\"ok\":true}" })).IsSuccess);
        Assert.True((await Mysql.BackupManager.BackupTableSchemaAsync("dbo.it_backup")).Value);
        var file = Mysql.BackupManager.GetLatestBackupFile("dbo.it_backup");
        Assert.NotNull(file);
        Assert.True((await Mysql.BackupManager.RestoreTableSchemaAsync("dbo.it_backup", file)).Value);
        Assert.Equal(0, (await Mysql.GetRepository<BackupRow>().CountAsync()).Value);
        await Assert.ThrowsAsync<SqlException>(() => _fx.Exec("INSERT INTO [dbo].[it_backup] ([name], [payload]) VALUES (N'bad', N'not json')"));
        Assert.Null(await Mysql.SchemaState.GetStateAsync("it_backup"));
        Assert.True((await Mysql.GetRepository<BackupRow>().InsertAsync(new BackupRow { Name = "survives", Payload = "{}" })).IsSuccess);
        var invalidBackup = Path.Combine(Path.GetDirectoryName(file!)!, "invalid_restore.sql");
        await File.WriteAllTextAsync(invalidBackup, "CREATE TABLE [dbo].[it_backup] ([id] int); THIS IS NOT VALID SQL;");
        Assert.True((await Mysql.BackupManager.RestoreTableSchemaAsync("dbo.it_backup", invalidBackup)).IsFailure);
        Assert.Equal(1, (await Mysql.GetRepository<BackupRow>().CountAsync()).Value);
        Assert.True((await Mysql.BackupManager.BackupDatabaseSchemaAsync()).Value);
        File.SetLastWriteTimeUtc(file!, DateTime.UtcNow.AddDays(-60));
        Assert.True((await Mysql.BackupManager.CleanupOldBackupsAsync(30)).Value >= 1);
    }

    [DbFact]
    public async Task Rowversion_retention_and_parameter_limit_batches_work()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_rowversion]; DROP TABLE IF EXISTS [dbo].[it_retention]; DROP TABLE IF EXISTS [dbo].[it_parameter_batch]");
        await Mysql.SchemaState.RemoveStateAsync("it_rowversion");
        await Mysql.SchemaState.RemoveStateAsync("it_retention");
        await Mysql.SchemaState.RemoveStateAsync("it_parameter_batch");

        Assert.True((await Mysql.SyncTableAsync<RowVersionRow>(createBackup: false)).IsSuccess);
        var versionRepo = Mysql.GetRepository<RowVersionRow>();
        var inserted = (await versionRepo.InsertAsync(new RowVersionRow { Name = "v1" })).Value!;
        var fetched = (await versionRepo.GetByIdAsync(inserted.Id)).Value!;
        Assert.NotNull(fetched.Version);
        Assert.Equal(8, fetched.Version.Length);
        var firstVersion = fetched.Version.ToArray();
        fetched.Name = "v2";
        Assert.True((await versionRepo.UpdateAsync(fetched)).IsSuccess);
        var updated = (await versionRepo.GetByIdAsync(fetched.Id)).Value!;
        Assert.False(firstVersion.SequenceEqual(updated.Version));

        Assert.True((await Mysql.SyncTableAsync<RetentionRow>(createBackup: false)).IsSuccess);
        var retentionRepo = Mysql.GetRepository<RetentionRow>();
        Assert.Equal(4, (await retentionRepo.InsertManyAsync([
            new RetentionRow { CreatedUtc = DateTime.UtcNow.AddDays(-10), Value = "old-1" },
            new RetentionRow { CreatedUtc = DateTime.UtcNow.AddDays(-9), Value = "old-2" },
            new RetentionRow { CreatedUtc = DateTime.UtcNow.AddDays(-8), Value = "old-3" },
            new RetentionRow { CreatedUtc = DateTime.UtcNow, Value = "new" }
        ])).Value);
        await using (var worker = new RetentionWorker(Mysql.ConnectionManager, null, [typeof(RetentionRow)]))
            Assert.Equal(3, await worker.RunOnceAsync());
        Assert.Equal("new", (await retentionRepo.GetAllAsync()).Value!.Single().Value);

        Assert.True((await Mysql.SyncTableAsync<ParameterBatchRow>(createBackup: false)).IsSuccess);
        var batchRepo = Mysql.GetRepository<ParameterBatchRow>();
        var rows = Enumerable.Range(0, 750).Select(i => new ParameterBatchRow
        {
            BatchKey = $"key-{i}", Amount = i, Enabled = i % 2 == 0
        }).ToArray();
        Assert.Equal(750, (await batchRepo.InsertManyAsync(rows)).Value);
        Assert.Equal(750, (await batchRepo.CountAsync()).Value);
    }

    [DbFact]
    public async Task Lock_timeouts_retry_only_complete_noncaller_transaction_operations()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_retry]; CREATE TABLE [dbo].[it_retry] ([id] int NOT NULL PRIMARY KEY, [value] int NOT NULL); INSERT INTO [dbo].[it_retry] VALUES (1, 0)");
        await using var blocker = await Mysql.ConnectionManager.OpenConnectionAsync();
        await using var blockerTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var block = blocker.CreateCommand())
        {
            block.Transaction = blockerTransaction;
            block.CommandText = "UPDATE [dbo].[it_retry] SET [value]=1 WHERE [id]=1";
            await block.ExecuteNonQueryAsync();
        }

        try
        {
            var attempts = 0;
            await Assert.ThrowsAsync<SqlException>(() => Mysql.ConnectionManager.ExecuteWithConnectionAsync(async connection =>
            {
                attempts++;
                await using var command = connection.CreateCommand();
                command.CommandText = "SET LOCK_TIMEOUT 25; UPDATE [dbo].[it_retry] SET [value]=2 WHERE [id]=1";
                return await command.ExecuteNonQueryAsync();
            }));
            Assert.Equal(4, attempts); // first attempt plus the configured three retries

            var transactionAttempts = 0;
            await Assert.ThrowsAsync<SqlException>(() => Mysql.ConnectionManager.ExecuteWithTransactionAsync(async (connection, transaction) =>
            {
                transactionAttempts++;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SET LOCK_TIMEOUT 25; UPDATE [dbo].[it_retry] SET [value]=3 WHERE [id]=1";
                return await command.ExecuteNonQueryAsync();
            }));
            Assert.Equal(1, transactionAttempts);
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
            await Mysql.ConnectionManager.CloseConnectionAsync(blocker);
        }
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

    [DbFact]
    public async Task Cursor_paging_is_stable_and_validates_tokens()
    {
        var expected = (await Mysql.Query<CursorRow>()
            .OrderBy(x => x.Rank)
            .OrderByDescending(x => x.Bucket)
            .OrderByDescending(x => x.Id)
            .ToListAsync()).Value!;

        var actual = new List<CursorRow>();
        string? cursor = null;
        CursorPagedResult<CursorRow>? lastPage = null;
        do
        {
            var result = await Mysql.Query<CursorRow>()
                .OrderBy(x => x.Rank)
                .OrderByDescending(x => x.Bucket)
                .After(cursor)
                .ToCursorPagedListAsync(2);

            Assert.True(result.IsSuccess, result.Error?.Message);
            lastPage = result.Value!;
            Assert.Equal(2, lastPage.PageSize);
            actual.AddRange(lastPage.Items);
            cursor = lastPage.NextCursor;
        } while (lastPage.HasNextPage);

        Assert.Equal(expected.Select(x => x.Id), actual.Select(x => x.Id));
        Assert.Equal(expected.Count, actual.Select(x => x.Id).Distinct().Count());
        Assert.Null(lastPage.NextCursor);

        var first = (await Mysql.Query<CursorRow>()
            .OrderBy(x => x.Rank)
            .ToCursorPagedListAsync(2)).Value!;
        Assert.True(first.HasNextPage);

        var malformed = await Mysql.Query<CursorRow>()
            .OrderBy(x => x.Rank)
            .After("not-a-token")
            .ToCursorPagedListAsync(2);
        Assert.True(malformed.IsFailure);
        Assert.Equal("mssql.invalid_cursor", malformed.Error!.Code);

        var mismatched = await Mysql.Query<CursorRow>()
            .OrderByDescending(x => x.Rank)
            .After(first.NextCursor)
            .ToCursorPagedListAsync(2);
        Assert.True(mismatched.IsFailure);
        Assert.Equal("mssql.invalid_cursor", mismatched.Error!.Code);

        var unordered = await Mysql.Query<CursorRow>().ToCursorPagedListAsync(2);
        Assert.True(unordered.IsFailure);
        Assert.Equal("mssql.cursor_paging_invalid", unordered.Error!.Code);

        var limited = await Mysql.Query<CursorRow>()
            .OrderBy(x => x.Rank)
            .Take(2)
            .ToCursorPagedListAsync(2);
        Assert.True(limited.IsFailure);
        Assert.Equal("mssql.cursor_paging_invalid", limited.Error!.Code);

        var cursorMisuse = await Mysql.Query<CursorRow>()
            .OrderBy(x => x.Rank)
            .After(first.NextCursor)
            .ToListAsync();
        Assert.True(cursorMisuse.IsFailure);
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
        Assert.Contains(renamedOps, op => op.Contains("sp_rename", StringComparison.OrdinalIgnoreCase));

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

    [DbFact]
    public async Task SchemaSync_reconciles_safe_and_incompatible_column_drift()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_column_drift]");
        await Mysql.SchemaState.RemoveStateAsync("it_column_drift");
        Mysql.SetSyncMode(SyncMode.Production);
        try
        {
            Assert.True((await Mysql.SyncTableAsync<ColumnDriftV1>(createBackup: false)).IsSuccess);
            Assert.True((await Mysql.GetRepository<ColumnDriftV1>().InsertAsync(
                new ColumnDriftV1 { Name = "kept", Score = 7 })).IsSuccess);

            var production = (await Mysql.SyncTableAsync<ColumnDriftV2>(createBackup: false)).Value!;
            Assert.True(production.DriftPending);
            Assert.Contains(production.Operations, sql => sql.Contains("nvarchar(80)", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("column-v2", (await Mysql.SqlScalarAsync<string>("""
                SELECT CONVERT(nvarchar(max), ep.value)
                FROM sys.extended_properties ep
                JOIN sys.columns c ON c.object_id=ep.major_id AND c.column_id=ep.minor_id
                WHERE ep.name=N'MS_Description' AND ep.major_id=OBJECT_ID(N'[dbo].[it_column_drift]') AND c.name=N'name'
                """)).Value);

            var beforeMigration = await Mysql.SqlQueryAsync<ColumnCatalogRow>("""
                SELECT ty.name AS type_name, c.max_length
                FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id
                WHERE c.object_id=OBJECT_ID(N'[dbo].[it_column_drift]') AND c.name=N'score'
                """);
            Assert.Equal("int", beforeMigration.Value!.Single().TypeName);

            Mysql.SetSyncMode(SyncMode.Migration);
            var migrated = (await Mysql.SyncTableAsync<ColumnDriftV2>(createBackup: false)).Value!;
            Assert.Contains(migrated.Operations, sql => sql.Contains("ALTER COLUMN [score] bigint", StringComparison.OrdinalIgnoreCase));
            var row = (await Mysql.Query<ColumnDriftV2>().FirstOrDefaultAsync()).Value;
            Assert.NotNull(row);
            Assert.Equal(7L, row!.Score);
            Assert.Equal("kept", row.Name);

            Mysql.SetSyncMode(SyncMode.Developer);
            var reindexed = (await Mysql.SyncTableAsync<ColumnDriftV3>(createBackup: false)).Value!;
            Assert.Contains(reindexed.Operations, sql => sql.Contains("DROP INDEX [IX_it_column_drift_name]", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(reindexed.Operations, sql => sql.Contains("CREATE UNIQUE INDEX [IX_it_column_drift_name]", StringComparison.OrdinalIgnoreCase));
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
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='dbo' AND t.name='it_lock'")).Value;
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
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='dbo' AND t.name='it_mig'")).Value);
        Assert.Equal(2, (await Mysql.GetPendingMigrationsAsync()).Count);

        // A migration without DownAsync aborts the rollback cleanly (no partial changes).
        await Mysql.MigrateAsync();                       // re-apply the two reversible ones
        Mysql.RegisterMigration(new NoDownMig());         // 1.0.0/003 — no DownAsync override
        await Mysql.MigrateAsync();                        // apply the no-down migration
        var rb2 = await Mysql.RollbackAsync(new MigrationVersion("1.0.0", 0));
        Assert.True(rb2.IsFailure);
        Assert.Equal(1, (await Mysql.SqlScalarAsync<long>(
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='dbo' AND t.name='it_mig'")).Value);
    }
}

// ── Entities ──────────────────────────────────────────────────────────────────────

[Table(Name = "it_customer")]
public class Customer
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.VarChar, Size = 100, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "country", DataType = DataType.VarChar, Size = 10, NotNull = true)] public string Country { get; set; } = "";
    [Column(Name = "is_vip", DataType = DataType.Bit, NotNull = true)] public bool IsVip { get; set; }
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

[Table(Name = "it_cursor")]
public class CursorRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "rank", DataType = DataType.Int)] public int? Rank { get; set; }
    [Column(Name = "bucket", DataType = DataType.VarChar, Size = 10, NotNull = true)] public string Bucket { get; set; } = "";
}

public class OrderView
{
    public long OrderId { get; set; }
    public string Customer { get; set; } = "";
    public decimal Total { get; set; }
}

[Table(Name = "odd table]x", Schema = "odd/schema]x")]
public class OddIdentifierRow
{
    [Column(Name = "key ] id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "natural ] key", DataType = DataType.NVarChar, Size = 80, NotNull = true, Unique = true)]
    public string NaturalKey { get; set; } = "";

    [Column(Name = "display ] value", DataType = DataType.NVarChar, Size = 120, NotNull = true)]
    public string DisplayValue { get; set; } = "";
}

public class CustomerTotal
{
    public long CustomerId { get; set; }
    public int Count { get; set; }
    public decimal Total { get; set; }
}

[Table(Name = "it_persist")]
public class PersistRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "row_key", DataType = DataType.NVarChar, Size = 80, NotNull = true, Unique = true)] public string Key { get; set; } = "";
    [Column(Name = "amount", DataType = DataType.Decimal, Precision = 18, Scale = 2, NotNull = true)] public decimal Amount { get; set; }
    [Column(Name = "counter", DataType = DataType.Int, NotNull = true)] public int Counter { get; set; }
    [Column(Name = "enabled", DataType = DataType.Bit, NotNull = true)] public bool Enabled { get; set; }
}

[Table(Name = "it_backup", Comment = "Backup integration table")]
public class BackupRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.NVarChar, Size = 80, NotNull = true, Comment = "Display name")]
    [Index(Name = "IX_it_backup_name", Include = [nameof(Id)])]
    public string Name { get; set; } = "";
    [Column(Name = "payload", DataType = DataType.Json)] public string? Payload { get; set; }
}

[Table(Name = "it_column_drift", Comment = "table-v1")]
public class ColumnDriftV1
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.NVarChar, Size = 20, NotNull = true, Comment = "column-v1")]
    [Index(Name = "IX_it_column_drift_name", Include = [nameof(Score)])]
    public string Name { get; set; } = "";
    [Column(Name = "score", DataType = DataType.Int, NotNull = true, DefaultValue = "1")] public int Score { get; set; }
}

[Table(Name = "it_column_drift", Comment = "table-v2")]
public class ColumnDriftV2
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.NVarChar, Size = 80, NotNull = true, Comment = "column-v2")]
    [Index(Name = "IX_it_column_drift_name", Include = [nameof(Score)])]
    public string Name { get; set; } = "";
    [Column(Name = "score", DataType = DataType.BigInt, NotNull = true, DefaultValue = "2")] public long Score { get; set; }
}

[Table(Name = "it_column_drift", Comment = "table-v2")]
public class ColumnDriftV3
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.NVarChar, Size = 80, NotNull = true, Comment = "column-v2")]
    [Index(Name = "IX_it_column_drift_name", Unique = true)]
    public string Name { get; set; } = "";
    [Column(Name = "score", DataType = DataType.BigInt, NotNull = true, DefaultValue = "2")] public long Score { get; set; }
}

public class ColumnCatalogRow
{
    [Column(Name = "type_name")] public string TypeName { get; set; } = "";
    [Column(Name = "max_length")] public short MaxLength { get; set; }
}

[Table(Name = "it_rowversion")]
public class RowVersionRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", DataType = DataType.NVarChar, Size = 80, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "version", DataType = DataType.RowVersion)] public byte[] Version { get; set; } = [];
}

[Table(Name = "it_retention")]
[RetainDays(1, nameof(CreatedUtc), BatchSize = 2)]
public class RetentionRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", DataType = DataType.DateTime2, Precision = 7, NotNull = true)] public DateTime CreatedUtc { get; set; }
    [Column(Name = "value", DataType = DataType.NVarChar, Size = 80, NotNull = true)] public string Value { get; set; } = "";
}

[Table(Name = "it_parameter_batch")]
public class ParameterBatchRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "batch_key", DataType = DataType.NVarChar, Size = 80, NotNull = true)] public string BatchKey { get; set; } = "";
    [Column(Name = "amount", DataType = DataType.Int, NotNull = true)] public int Amount { get; set; }
    [Column(Name = "enabled", DataType = DataType.Bit, NotNull = true)] public bool Enabled { get; set; }
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
