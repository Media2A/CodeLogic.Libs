using CL.MySQL2;
using CL.MySQL2.Models;
using Xunit;
using Xunit.Abstractions;

namespace MySQL2.Tests;

// Public entry points the existing suite never calls. Measuring name coverage across the
// public surface put CL.MySQL2 lowest of the three libraries, with most of
// ConnectionManager and several library-level helpers never exercised.

[Table(Name = "it_api")]
public sealed class ApiRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", Size = 40, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "score", NotNull = true)] public int Score { get; set; }
}

[Table(Name = "it_api_b")]
public sealed class ApiRowB
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "note", Size = 40)] public string? Note { get; set; }
}

[Table(Name = "it_api_soft")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class ApiSoftRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 40, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "deleted_utc")] public DateTime? DeletedUtc { get; set; }
}

[Collection("codelogic")]
public sealed class ApiSurfaceTests
{
    private readonly MySQL2RuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private MySQL2Library Mysql => _fx.Mysql;

    public ApiSurfaceTests(MySQL2RuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task FreshAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_api");
        Assert.True((await Mysql.SyncTableAsync<ApiRow>(createBackup: false)).IsSuccess);
    }

    // ── ConnectionManager ────────────────────────────────────────────────────────

    [DbFact]
    public async Task ConnectionManager_surface_works()
    {
        var cm = Mysql.ConnectionManager;

        Assert.True(cm.HasConfiguration());
        Assert.False(cm.HasConfiguration("no-such-connection"));
        Assert.Contains("Default", cm.GetConnectionIds());

        var cfg = cm.GetConfiguration();
        Assert.NotNull(cfg);
        Assert.False(string.IsNullOrWhiteSpace(cfg!.Host));

        // Built through MySqlConnectionStringBuilder, so it must parse back cleanly.
        var parsed = new MySqlConnector.MySqlConnectionStringBuilder(cm.GetConnectionString());
        Assert.Equal(cfg.Database, parsed.Database);

        Assert.True(await cm.TestConnectionAsync());

        var info = await cm.GetServerInfoAsync();
        _out.WriteLine($"server: {info.Version} / {info.Comment} db={info.Database}");
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(string.IsNullOrWhiteSpace(info.Database));
    }

    [DbFact]
    public async Task Open_and_close_connection_track_the_open_count()
    {
        var cm = Mysql.ConnectionManager;
        var before = cm.GetOpenConnectionCount();

        var conn = await cm.OpenConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
        Assert.Equal(before + 1, cm.GetOpenConnectionCount());

        await cm.CloseConnectionAsync(conn);
        await conn.DisposeAsync();
        Assert.Equal(before, cm.GetOpenConnectionCount());

        Assert.True(cm.GetAllConnectionCounts().ContainsKey("Default"));
    }

    [DbFact]
    public async Task Unknown_connection_id_is_rejected_rather_than_defaulted()
    {
        var cm = Mysql.ConnectionManager;
        await Assert.ThrowsAnyAsync<Exception>(async () => await cm.OpenConnectionAsync("nope"));
        Assert.Null(cm.GetConfiguration("nope"));
    }

    // ── Library-level helpers ────────────────────────────────────────────────────

    [DbFact]
    public async Task Library_TestConnection_and_accessors_work()
    {
        var ok = await Mysql.TestConnectionAsync();
        Assert.True(ok.IsSuccess, ok.Error?.ToString());
        Assert.True(ok.Value);

        Assert.NotNull(Mysql.TableSync);
        Assert.NotNull(Mysql.SchemaState);
        Assert.NotNull(Mysql.BackupManager);
        Assert.NotNull(Mysql.Migrations);
        Assert.Equal("CL.MySQL2", Mysql.Manifest.Id);
    }

    [DbFact]
    public async Task SyncSchemaAsync_reconciles_several_entities_in_one_pass()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_api");
        await _fx.Exec("DROP TABLE IF EXISTS it_api_b");

        var result = await Mysql.SyncSchemaAsync(typeof(ApiRow), typeof(ApiRowB));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value.Values, r => Assert.True(r.Success, string.Join("; ", r.Errors)));
    }

    [DbFact]
    public async Task RegisterMigrationsFrom_scans_an_assembly_without_throwing()
    {
        var returned = Mysql.RegisterMigrationsFrom(typeof(ApiSurfaceTests).Assembly);
        Assert.Same(Mysql, returned);
        var pending = await Mysql.GetPendingMigrationsAsync();
        Assert.True(pending.Count >= 0);
    }

    // ── Repository ───────────────────────────────────────────────────────────────

    [DbFact]
    public async Task FindAsync_filters_by_predicate()
    {
        await FreshAsync();
        var repo = Mysql.GetRepository<ApiRow>();
        await repo.InsertManyAsync([
            new ApiRow { Name = "low", Score = 1 },
            new ApiRow { Name = "high", Score = 99 },
        ]);

        var found = await repo.FindAsync(r => r.Score > 50);
        Assert.True(found.IsSuccess, found.Error?.ToString());
        Assert.Single(found.Value!);
        Assert.Equal("high", found.Value![0].Name);
    }

    [DbFact]
    public async Task HardDeleteAsync_removes_a_soft_deleted_row_for_real()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_api_soft");
        Assert.True((await Mysql.SyncTableAsync<ApiSoftRow>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<ApiSoftRow>();
        var row = await repo.InsertAsync(new ApiSoftRow { Label = "doomed" });

        await repo.DeleteAsync(row.Value!.Id);
        Assert.Equal(1, (await Mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM it_api_soft")).Value);

        var hard = await repo.HardDeleteAsync(row.Value.Id);
        Assert.True(hard.IsSuccess, hard.Error?.ToString());
        Assert.Equal(0, (await Mysql.SqlScalarAsync<long>("SELECT COUNT(*) FROM it_api_soft")).Value);
    }

    [DbFact]
    public async Task GetByColumn_and_GetPaged_reject_an_unmapped_column()
    {
        await FreshAsync();
        var repo = Mysql.GetRepository<ApiRow>();
        await repo.InsertAsync(new ApiRow { Name = "x", Score = 1 });

        var ok = await repo.GetByColumnAsync(nameof(ApiRow.Name), "x");
        Assert.True(ok.IsSuccess, ok.Error?.ToString());
        Assert.Single(ok.Value!);

        // The allow-list that keeps a caller string out of the SQL text.
        var bad = await repo.GetByColumnAsync("name`; DROP TABLE it_api; -- ", "x");
        Assert.True(bad.IsFailure);

        var badOrder = await repo.GetPagedAsync(1, 10, orderByColumn: "name`; DROP TABLE it_api; -- ");
        Assert.True(badOrder.IsFailure);

        // The table is intact.
        Assert.Equal(1, (await repo.CountAsync()).Value);
    }

    [DbFact]
    public async Task Increment_and_decrement_are_atomic_server_side()
    {
        await FreshAsync();
        var repo = Mysql.GetRepository<ApiRow>();
        var row = await repo.InsertAsync(new ApiRow { Name = "counter", Score = 10 });

        await repo.IncrementAsync(row.Value!.Id, r => r.Score, 5);
        await repo.IncrementAsync(row.Value.Id, r => r.Score, 5);
        await repo.DecrementAsync(row.Value.Id, r => r.Score, 3);

        var after = await repo.GetByIdAsync(row.Value.Id);
        Assert.Equal(17, after.Value!.Score);
    }

    // ── QueryBuilder ─────────────────────────────────────────────────────────────

    [DbFact]
    public async Task ToPagedListAsync_pages_with_totals()
    {
        await FreshAsync();
        await Mysql.GetRepository<ApiRow>().InsertManyAsync(Enumerable.Range(0, 9)
            .Select(i => new ApiRow { Name = $"n{i}", Score = i }).ToList());

        var page = await Mysql.Query<ApiRow>()
            .Where(r => r.Score >= 0)
            .OrderBy(r => r.Score)
            .ToPagedListAsync(page: 2, pageSize: 4);

        Assert.True(page.IsSuccess, page.Error?.ToString());
        Assert.Equal(9, page.Value!.TotalItems);
        Assert.Equal(4, page.Value.Items.Count);
        Assert.Equal(4, page.Value.Items[0].Score);
    }

    [DbFact]
    public async Task WithConnection_retargets_the_query()
    {
        await FreshAsync();
        await Mysql.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        var rows = await Mysql.Query<ApiRow>().WithConnection("Default").ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Single(rows.Value!);

        var bad = await Mysql.Query<ApiRow>().WithConnection("no-such-connection").ToListAsync();
        Assert.True(bad.IsFailure, "an unknown connection id must fail, not silently use Default");
    }

    [DbFact]
    public async Task Empty_in_clause_does_not_produce_invalid_sql()
    {
        await FreshAsync();
        await Mysql.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        // "IN ()" is a syntax error in MySQL too.
        var none = Array.Empty<string>();
        var miss = await Mysql.Query<ApiRow>().Where(r => none.Contains(r.Name)).ToListAsync();
        Assert.True(miss.IsSuccess, miss.Error?.ToString());
        Assert.Empty(miss.Value!);
    }

    [DbFact]
    public async Task Generated_columns_are_rejected_by_update()
    {
        await FreshAsync();
        await Mysql.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        // The auto-increment key is database-generated; updating it must be refused.
        var result = await Mysql.Query<ApiRow>()
            .UpdateAsync(new Dictionary<string, object?> { ["id"] = 99 });
        Assert.True(result.IsFailure);
    }
}
