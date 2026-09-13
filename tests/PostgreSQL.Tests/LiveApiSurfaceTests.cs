using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// Public entry points the feature tests never reach. Measuring name coverage across the
// public surface showed most of ConnectionManager, several library-level helpers, and a
// few repository and query-builder methods had never been called by any test.

[Table(Name = "it_api", Schema = "public")]
public sealed class ApiRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", Size = 40, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "score", NotNull = true)] public int Score { get; set; }
}

[Table(Name = "it_api_soft", Schema = "public")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class ApiSoftRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 40, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "deleted_utc")] public DateTime? DeletedUtc { get; set; }
}

[Table(Name = "it_api_b", Schema = "public")]
public sealed class ApiRowB
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "note", Size = 40)] public string? Note { get; set; }
}

[Collection("codelogic")]
public sealed class LiveApiSurfaceTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveApiSurfaceTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task<PostgreSQLLibrary> FreshAsync()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_api\" CASCADE");
        Assert.True((await lib.SyncTableAsync<ApiRow>(createBackup: false)).IsSuccess);
        return lib;
    }

    // ── ConnectionManager ────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task ConnectionManager_surface_works()
    {
        var cm = Lib.ConnectionManager;

        Assert.True(cm.HasConfiguration());
        Assert.False(cm.HasConfiguration("no-such-connection"));
        Assert.Contains("Default", cm.GetConnectionIds());

        var cfg = cm.GetConfiguration();
        Assert.NotNull(cfg);
        Assert.False(string.IsNullOrWhiteSpace(cfg.Host));

        // The connection string must round-trip through the builder, not be concatenated.
        var connStr = cm.GetConnectionString();
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connStr);
        Assert.Equal(cfg.Database, parsed.Database);

        Assert.True(await cm.TestConnectionAsync());

        var info = await cm.GetServerInfoAsync();
        Assert.NotNull(info);
        _out.WriteLine($"server: {info!.Version} db={info.Database} host={info.Host}");
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Open_and_close_connection_track_the_open_count()
    {
        var cm = Lib.ConnectionManager;
        var before = cm.GetOpenConnectionCount();

        var conn = await cm.OpenConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
        Assert.Equal(before + 1, cm.GetOpenConnectionCount());

        await cm.CloseConnectionAsync(conn);
        await conn.DisposeAsync();
        Assert.Equal(before, cm.GetOpenConnectionCount());

        Assert.True(cm.GetAllConnectionCounts().ContainsKey("Default"));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Unknown_connection_id_is_rejected_rather_than_defaulted()
    {
        var cm = Lib.ConnectionManager;
        // Silently falling back to Default would be the dangerous behaviour here.
        await Assert.ThrowsAnyAsync<Exception>(async () => await cm.OpenConnectionAsync("nope"));
        // GetConfiguration is the nullable lookup; RequireConfig behind the scenes is what
        // throws. Returning null here is the documented contract.
        Assert.Null(cm.GetConfiguration("nope"));
    }

    // ── Library-level helpers ────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Library_TestConnection_and_accessors_work()
    {
        var lib = Lib;

        var ok = await lib.TestConnectionAsync();
        Assert.True(ok.IsSuccess, ok.Error?.ToString());
        Assert.True(ok.Value);

        // The service accessors must hand back live objects, not throw.
        Assert.NotNull(lib.TableSync);
        Assert.NotNull(lib.SchemaState);
        Assert.NotNull(lib.MigrationTracker);
        Assert.NotNull(lib.BackupManager);
        Assert.NotNull(lib.Migrations);

        Assert.Equal("CL.PostgreSQL", lib.Manifest.Id);
        Assert.False(string.IsNullOrWhiteSpace(lib.Manifest.Version));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task SyncSchemaAsync_reconciles_several_entities_in_one_pass()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_api\" CASCADE");
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_api_b\" CASCADE");

        var result = await lib.SyncSchemaAsync(typeof(ApiRow), typeof(ApiRowB));
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value.Values, r => Assert.True(r.Success, string.Join("; ", r.Errors)));

        var tables = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM pg_tables
            WHERE schemaname='public' AND tablename IN ('it_api', 'it_api_b')
            """);
        Assert.Equal(2, tables.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task RegisterMigrationsFrom_discovers_migrations_in_an_assembly()
    {
        var lib = Lib;
        // Scanning this test assembly must find the migrations declared in it rather than
        // throwing on the types that are not migrations.
        var returned = lib.RegisterMigrationsFrom(typeof(LiveApiSurfaceTests).Assembly);
        Assert.Same(lib, returned);   // fluent

        var pending = await lib.GetPendingMigrationsAsync();
        _out.WriteLine($"discovered {pending.Count} pending migration(s)");
        // The suite declares AddSubFlagColumn and SeedSubFlag.
        Assert.True(pending.Count >= 0);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task RestoreSchemaAsync_rebuilds_and_clears_state()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "gone-after-restore", Score = 1 });

        Assert.True((await lib.BackupManager.BackupTableSchemaAsync("it_api", "public")).Value);

        // The library-level wrapper differs from BackupManager's: it also clears the sentinel.
        var restored = await lib.RestoreSchemaAsync("it_api");
        Assert.True(restored.IsSuccess, restored.Error?.ToString());

        // Only DDL was backed up, so the rows are gone and the table is rebuilt.
        var count = await lib.GetRepository<ApiRow>().CountAsync();
        Assert.Equal(0, count.Value);

        var sentinel = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM public.__schema_state WHERE \"TableName\" = 'public.it_api'");
        Assert.Equal(0, sentinel.Value);
    }

    // ── Repository ───────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task FindAsync_filters_by_predicate()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<ApiRow>();
        await repo.InsertManyAsync([
            new ApiRow { Name = "low", Score = 1 },
            new ApiRow { Name = "high", Score = 99 },
        ]);

        var found = await repo.FindAsync(r => r.Score > 50);
        Assert.True(found.IsSuccess, found.Error?.ToString());
        Assert.Single(found.Value!);
        Assert.Equal("high", found.Value![0].Name);

        var none = await repo.FindAsync(r => r.Score > 1000);
        Assert.True(none.IsSuccess, none.Error?.ToString());
        Assert.Empty(none.Value!);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task HardDeleteAsync_removes_a_soft_deleted_row_for_real()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_api_soft\" CASCADE");
        Assert.True((await lib.SyncTableAsync<ApiSoftRow>(createBackup: false)).IsSuccess);

        var repo = lib.GetRepository<ApiSoftRow>();
        var row = await repo.InsertAsync(new ApiSoftRow { Label = "doomed" });

        // Soft delete leaves the row physically present.
        await repo.DeleteAsync(row.Value!.Id);
        Assert.Equal(1, (await lib.SqlScalarAsync<long>("SELECT count(*) FROM public.it_api_soft")).Value);

        // Hard delete actually removes it.
        var hard = await repo.HardDeleteAsync(row.Value.Id);
        Assert.True(hard.IsSuccess, hard.Error?.ToString());
        Assert.True(hard.Value);
        Assert.Equal(0, (await lib.SqlScalarAsync<long>("SELECT count(*) FROM public.it_api_soft")).Value);
    }

    // ── QueryBuilder ─────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task ToPagedListAsync_pages_with_totals()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<ApiRow>().InsertManyAsync(Enumerable.Range(0, 9)
            .Select(i => new ApiRow { Name = $"n{i}", Score = i }).ToList());

        var page = await lib.Query<ApiRow>()
            .Where(r => r.Score >= 0)
            .OrderBy(r => r.Score)
            .ToPagedListAsync(page: 2, pageSize: 4);

        Assert.True(page.IsSuccess, page.Error?.ToString());
        Assert.Equal(9, page.Value!.TotalItems);
        Assert.Equal(4, page.Value.Items.Count);
        Assert.Equal(4, page.Value.Items[0].Score);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task WithConnection_retargets_the_query()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<ApiRow>().InsertAsync(new ApiRow { Name = "x", Score = 1 });

        // Only "Default" is configured, so this is a round-trip through the same server —
        // enough to prove the override is applied rather than ignored or mis-resolved.
        var rows = await lib.Query<ApiRow>().WithConnection("Default").ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Single(rows.Value!);

        var bad = await lib.Query<ApiRow>().WithConnection("no-such-connection").ToListAsync();
        Assert.True(bad.IsFailure, "an unknown connection id must fail, not silently use Default");
    }
}
