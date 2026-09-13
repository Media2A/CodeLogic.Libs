using CL.MSSQL;
using CL.MSSQL.Models;
using Xunit;
using Xunit.Abstractions;

namespace MSSQL.Tests;

// Public entry points the existing suite never calls — most of ConnectionManager, several
// library-level helpers, and a few repository and query-builder methods.

[Table(Name = "it_api", Schema = "dbo")]
public sealed class ApiRow
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    public string Name { get; set; } = "";

    [Column(DataType = DataType.Int, NotNull = true)]
    public int Score { get; set; }
}

[Table(Name = "it_api_b", Schema = "dbo")]
public sealed class ApiRowB
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(DataType = DataType.NVarChar, Size = 40)]
    public string? Note { get; set; }
}

[Table(Name = "it_api_soft", Schema = "dbo")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class ApiSoftRow
{
    [Column(DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    public string Label { get; set; } = "";

    [Column(DataType = DataType.DateTime2)]
    public DateTime? DeletedUtc { get; set; }
}

[Collection("codelogic")]
public sealed class ApiSurfaceTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private MSSQLLibrary Mssql => _fx.Mysql;

    public ApiSurfaceTests(MSSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task FreshAsync()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_api]");
        Assert.True((await Mssql.SyncTableAsync<ApiRow>(createBackup: false)).IsSuccess);
    }

    // ── ConnectionManager ────────────────────────────────────────────────────────

    [DbFact]
    public async Task ConnectionManager_surface_works()
    {
        var cm = Mssql.ConnectionManager;

        Assert.True(cm.HasConfiguration());
        Assert.False(cm.HasConfiguration("no-such-connection"));
        Assert.Contains("Default", cm.GetConnectionIds());
        Assert.NotNull(cm.GetConfiguration());

        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cm.GetConnectionString());
        Assert.False(string.IsNullOrWhiteSpace(parsed.DataSource));

        Assert.True(await cm.TestConnectionAsync());

        var info = await cm.GetServerInfoAsync();
        _out.WriteLine($"server: {info.Version} / {info.Comment} db={info.Database}");
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.False(string.IsNullOrWhiteSpace(info.Database));
    }

    [DbFact]
    public async Task Open_and_close_connection_track_the_open_count()
    {
        var cm = Mssql.ConnectionManager;
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
        var cm = Mssql.ConnectionManager;
        await Assert.ThrowsAnyAsync<Exception>(async () => await cm.OpenConnectionAsync("nope"));
        Assert.Null(cm.GetConfiguration("nope"));
    }

    // ── Library-level helpers ────────────────────────────────────────────────────

    [DbFact]
    public async Task Library_TestConnection_and_accessors_work()
    {
        var ok = await Mssql.TestConnectionAsync();
        Assert.True(ok.IsSuccess, ok.Error?.ToString());
        Assert.True(ok.Value);

        Assert.NotNull(Mssql.TableSync);
        Assert.NotNull(Mssql.SchemaState);
        Assert.NotNull(Mssql.BackupManager);
        Assert.NotNull(Mssql.Migrations);
        Assert.Equal("CL.MSSQL", Mssql.Manifest.Id);
    }

    [DbFact]
    public async Task SyncSchemaAsync_reconciles_several_entities_in_one_pass()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_api]");
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_api_b]");

        var result = await Mssql.SyncSchemaAsync(typeof(ApiRow), typeof(ApiRowB));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value.Values, r => Assert.True(r.Success, string.Join("; ", r.Errors)));
    }

    [DbFact]
    public async Task RegisterMigrationsFrom_scans_an_assembly_without_throwing()
    {
        var returned = Mssql.RegisterMigrationsFrom(typeof(ApiSurfaceTests).Assembly);
        Assert.Same(Mssql, returned);
        var pending = await Mssql.GetPendingMigrationsAsync();
        Assert.True(pending.Count >= 0);
    }

    // ── Repository ───────────────────────────────────────────────────────────────

    [DbFact]
    public async Task FindAsync_filters_by_predicate()
    {
        await FreshAsync();
        var repo = Mssql.GetRepository<ApiRow>();
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
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_api_soft]");
        Assert.True((await Mssql.SyncTableAsync<ApiSoftRow>(createBackup: false)).IsSuccess);

        var repo = Mssql.GetRepository<ApiSoftRow>();
        var row = await repo.InsertAsync(new ApiSoftRow { Label = "doomed" });

        await repo.DeleteAsync(row.Value!.Id);
        Assert.Equal(1, (await Mssql.SqlScalarAsync<int>("SELECT COUNT(*) FROM [dbo].[it_api_soft]")).Value);

        var hard = await repo.HardDeleteAsync(row.Value.Id);
        Assert.True(hard.IsSuccess, hard.Error?.ToString());
        Assert.Equal(0, (await Mssql.SqlScalarAsync<int>("SELECT COUNT(*) FROM [dbo].[it_api_soft]")).Value);
    }

    [DbFact]
    public async Task GetByColumn_and_GetPaged_reject_an_unmapped_column()
    {
        await FreshAsync();
        var repo = Mssql.GetRepository<ApiRow>();
        await repo.InsertAsync(new ApiRow { Name = "x", Score = 1 });

        var ok = await repo.GetByColumnAsync(nameof(ApiRow.Name), "x");
        Assert.True(ok.IsSuccess, ok.Error?.ToString());
        Assert.Single(ok.Value!);

        var bad = await repo.GetByColumnAsync("Name]; DROP TABLE [dbo].[it_api]; -- ", "x");
        Assert.True(bad.IsFailure);

        var badOrder = await repo.GetPagedAsync(1, 10, orderByColumn: "Name]; DROP TABLE [dbo].[it_api]; -- ");
        Assert.True(badOrder.IsFailure);

        Assert.Equal(1, (await repo.CountAsync()).Value);
    }

    [DbFact]
    public async Task Increment_and_decrement_are_atomic_server_side()
    {
        await FreshAsync();
        var repo = Mssql.GetRepository<ApiRow>();
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
        await Mssql.GetRepository<ApiRow>().InsertManyAsync(Enumerable.Range(0, 9)
            .Select(i => new ApiRow { Name = $"n{i}", Score = i }).ToList());

        var page = await Mssql.Query<ApiRow>()
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
        await Mssql.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        var rows = await Mssql.Query<ApiRow>().WithConnection("Default").ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Single(rows.Value!);

        var bad = await Mssql.Query<ApiRow>().WithConnection("no-such-connection").ToListAsync();
        Assert.True(bad.IsFailure, "an unknown connection id must fail, not silently use Default");
    }

    [DbFact]
    public async Task Empty_in_clause_does_not_produce_invalid_sql()
    {
        await FreshAsync();
        await Mssql.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        var none = Array.Empty<string>();
        var miss = await Mssql.Query<ApiRow>().Where(r => none.Contains(r.Name)).ToListAsync();
        Assert.True(miss.IsSuccess, miss.Error?.ToString());
        Assert.Empty(miss.Value!);
    }

    [DbFact]
    public async Task Generated_columns_are_rejected_by_update()
    {
        await FreshAsync();
        await Mssql.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        var result = await Mssql.Query<ApiRow>()
            .UpdateAsync(new Dictionary<string, object?> { ["Id"] = 99L });
        Assert.True(result.IsFailure);
    }
}
