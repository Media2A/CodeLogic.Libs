using CodeLogic;                          // Libraries, CodeLogicOptions
using CodeLogic.Framework.Libraries;      // HealthStatusLevel
using CL.SQLite;
using CL.SQLite.Models;
using Xunit;

namespace SQLite.Tests;

// ── Offline integration tests for CL.SQLite ────────────────────────────────────
// SQLite is an embedded DB, so the whole library is testable with no external
// service: we boot the real CodeLogic runtime against a throwaway temp-file DB.
//
// The CodeLogic runtime is a process-wide singleton, so EVERY test that needs the
// booted library lives in this one class behind a single shared fixture that boots
// once. The [Collection] attribute (with the matching CollectionDefinition below)
// serializes this class against any other CodeLogic-touching test assembly.

// ── Test entities ──────────────────────────────────────────────────────────────

[SQLiteTable("widget")]
public sealed class Widget
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "name", DataType = SQLiteDataType.TEXT, IsNotNull = true)]
    public string Name { get; set; } = "";

    [SQLiteColumn(ColumnName = "quantity", DataType = SQLiteDataType.INTEGER)]
    public int Quantity { get; set; }
}

// Entity whose column maps to a SQL reserved word ("Order") — exercises the
// identifier-quoting fix so a WHERE on it does not blow up.
[SQLiteTable("reserved_word_thing")]
public sealed class ReservedWordThing
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "Order", DataType = SQLiteDataType.INTEGER)]
    public int Order { get; set; }

    [SQLiteColumn(ColumnName = "label", DataType = SQLiteDataType.TEXT)]
    public string Label { get; set; } = "";
}

// ── Shared one-time-boot fixture ───────────────────────────────────────────────

public sealed class SQLiteRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_test_" + Guid.NewGuid().ToString("N"));

    public SQLiteLibrary Library { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(TempDir);
        var dbPath = Path.Combine(TempDir, "test.db").Replace("\\", "/");

        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.SQLite");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.sqlite.json"), $$"""
        {
          "databases": {
            "Default": {
              "enabled": true,
              "databasePath": "{{dbPath}}",
              "useWAL": true
            }
          }
        }
        """);

        await Libraries.LoadAsync<SQLiteLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<SQLiteLibrary>()
            ?? throw new InvalidOperationException("SQLiteLibrary not available after start.");
    }

    public async Task DisposeAsync()
    {
        try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* the DB file may linger briefly on Windows; ignore */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<SQLiteRuntimeFixture> { }

// ── Tests ──────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class SQLiteTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;

    public SQLiteTests(SQLiteRuntimeFixture fx) => _fx = fx;

    [Fact]
    public async Task SyncTable_succeeds()
    {
        var r = await Lib.TableSync.SyncTableAsync<Widget>();
        Assert.True(r.IsSuccess, r.Error?.Message);
        Assert.True(r.Value!.Success, r.Value.Message);
    }

    [Fact]
    public async Task Insert_returns_rowid_and_GetById_round_trips()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var repo = Lib.GetRepository<Widget>();

        var ins = await repo.InsertAsync(new Widget { Name = "alpha", Quantity = 3 });
        Assert.True(ins.IsSuccess, ins.Error?.Message);
        Assert.True(ins.Value > 0);

        var fetched = await repo.GetByIdAsync(ins.Value);
        Assert.True(fetched.IsSuccess, fetched.Error?.Message);
        Assert.NotNull(fetched.Value);
        Assert.Equal("alpha", fetched.Value!.Name);
        Assert.Equal(3, fetched.Value.Quantity);
    }

    [Fact]
    public async Task Insert_two_then_GetAll_sees_both()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var repo = Lib.GetRepository<Widget>();

        var a = (await repo.InsertAsync(new Widget { Name = "getall-a", Quantity = 1 })).Value;
        var b = (await repo.InsertAsync(new Widget { Name = "getall-b", Quantity = 2 })).Value;

        var all = await repo.GetAllAsync();
        Assert.True(all.IsSuccess, all.Error?.Message);
        Assert.Contains(all.Value!, w => w.Id == a);
        Assert.Contains(all.Value!, w => w.Id == b);
    }

    [Fact]
    public async Task QueryBuilder_Where_filters_and_Count_matches()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var repo = Lib.GetRepository<Widget>();
        var tag = "qb-" + Guid.NewGuid().ToString("N")[..8];
        await repo.InsertAsync(new Widget { Name = tag, Quantity = 10 });
        await repo.InsertAsync(new Widget { Name = tag, Quantity = 20 });
        await repo.InsertAsync(new Widget { Name = tag, Quantity = 99 });

        var filtered = await Lib.GetQueryBuilder<Widget>()
            .Where(w => w.Name == tag && w.Quantity < 50)
            .ToListAsync();
        Assert.True(filtered.IsSuccess, filtered.Error?.Message);
        Assert.Equal(2, filtered.Value!.Count);
        Assert.All(filtered.Value!, w => Assert.True(w.Quantity < 50));

        var count = await Lib.GetQueryBuilder<Widget>()
            .Where(w => w.Name == tag)
            .CountAsync();
        Assert.True(count.IsSuccess, count.Error?.Message);
        Assert.Equal(3, count.Value);
    }

    [Fact]
    public async Task QueryBuilder_ToPagedList_returns_a_page()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var repo = Lib.GetRepository<Widget>();
        var tag = "paged-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < 5; i++)
            await repo.InsertAsync(new Widget { Name = tag, Quantity = i });

        var paged = await Lib.GetQueryBuilder<Widget>()
            .Where(w => w.Name == tag)
            .OrderBy(w => w.Quantity)
            .ToPagedListAsync(page: 1, pageSize: 2);

        Assert.True(paged.IsSuccess, paged.Error?.Message);
        Assert.Equal(2, paged.Value!.Items.Count);
        Assert.Equal(5, paged.Value.TotalItems);
        Assert.Equal(1, paged.Value.PageNumber);
    }

    [Fact]
    public async Task ToPagedList_with_page_zero_fails()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var paged = await Lib.GetQueryBuilder<Widget>()
            .ToPagedListAsync(page: 0, pageSize: 10);
        Assert.True(paged.IsFailure);
        Assert.False(paged.IsSuccess);
    }

    [Fact]
    public async Task Where_on_reserved_word_column_executes()
    {
        var sync = await Lib.TableSync.SyncTableAsync<ReservedWordThing>();
        Assert.True(sync.IsSuccess, sync.Error?.Message);

        var repo = Lib.GetRepository<ReservedWordThing>();
        await repo.InsertAsync(new ReservedWordThing { Order = 1, Label = "first" });
        await repo.InsertAsync(new ReservedWordThing { Order = 2, Label = "second" });

        // Without the identifier-quoting fix, WHERE "Order" = @p would be a syntax error.
        var res = await Lib.GetQueryBuilder<ReservedWordThing>()
            .Where(x => x.Order == 2)
            .ToListAsync();

        Assert.True(res.IsSuccess, res.Error?.Message);
        Assert.Single(res.Value!);
        Assert.Equal("second", res.Value![0].Label);
    }

    [Fact]
    public async Task Upsert_inserts_then_replaces()
    {
        await Lib.TableSync.SyncTableAsync<Widget>();
        var repo = Lib.GetRepository<Widget>();

        var id = (await repo.InsertAsync(new Widget { Name = "upsert-me", Quantity = 1 })).Value;

        // INSERT OR REPLACE on the same PK replaces the existing row.
        var up = await repo.UpsertAsync(new Widget { Id = id, Name = "upsert-me", Quantity = 777 });
        Assert.True(up.IsSuccess, up.Error?.Message);

        var fetched = await repo.GetByIdAsync(id);
        Assert.True(fetched.IsSuccess, fetched.Error?.Message);
        Assert.NotNull(fetched.Value);
        Assert.Equal(777, fetched.Value!.Quantity);
    }

    [Fact]
    public async Task HealthCheck_is_healthy()
    {
        var health = await Lib.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
    }
}
