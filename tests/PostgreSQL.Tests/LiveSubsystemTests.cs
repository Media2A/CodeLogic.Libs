using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using Xunit;

namespace PostgreSQL.Tests;

// The subsystems the rebuild added that nothing had yet executed: migrations, the result
// cache, retention, joins and subquery filters, offset paging, and the repository helpers.

[Table(Name = "it_sub_main", Schema = "public")]
public sealed class SubMain
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", Size = 50, NotNull = true)] public string Name { get; set; } = "";
    [Column(Name = "hits", NotNull = true)] public int Hits { get; set; }
    [Column(Name = "group_id", NotNull = true)] public long GroupId { get; set; }
}

[Table(Name = "it_sub_group", Schema = "public")]
public sealed class SubGroup
{
    [Column(Name = "id", Primary = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 50, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "active", NotNull = true)] public bool Active { get; set; }
}

public sealed class MainWithGroup
{
    [Column(Name = "name")] public string Name { get; set; } = "";
    [Column(Name = "label")] public string Label { get; set; } = "";
}

[Table(Name = "it_sub_retain", Schema = "public")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class SubRetain
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", NotNull = true)] public DateTime CreatedUtc { get; set; }
}

// ── Migrations ───────────────────────────────────────────────────────────────────

public sealed class AddSubFlagColumn : Migration
{
    public AddSubFlagColumn() : base("1.0.0", 1, "add flag column") { }

    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("ALTER TABLE public.it_sub_main ADD COLUMN IF NOT EXISTS flag boolean", ct: ct);

    public override async Task DownAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("ALTER TABLE public.it_sub_main DROP COLUMN IF EXISTS flag", ct: ct);
}

public sealed class SeedSubFlag : Migration
{
    public SeedSubFlag() : base("1.0.0", 2, "seed flag") { }

    public override async Task UpAsync(IMigrationContext ctx, CancellationToken ct) =>
        await ctx.ExecuteAsync("UPDATE public.it_sub_main SET flag = true", ct: ct);

    public override Task DownAsync(IMigrationContext ctx, CancellationToken ct) => Task.CompletedTask;
}

[Collection("codelogic")]
public sealed class LiveSubsystemTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveSubsystemTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private async Task<PostgreSQLLibrary> FreshAsync()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_sub_main\" CASCADE");
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_sub_group\" CASCADE");
        Assert.True((await lib.SyncTableAsync<SubGroup>(createBackup: false)).IsSuccess);
        Assert.True((await lib.SyncTableAsync<SubMain>(createBackup: false)).IsSuccess);
        return lib;
    }

    private static async Task SeedAsync(PostgreSQLLibrary lib)
    {
        var groups = lib.GetRepository<SubGroup>();
        await groups.InsertManyAsync([
            new SubGroup { Id = 1, Label = "alpha", Active = true },
            new SubGroup { Id = 2, Label = "beta", Active = false },
        ]);
        var mains = lib.GetRepository<SubMain>();
        await mains.InsertManyAsync([
            new SubMain { Name = "a1", Hits = 1, GroupId = 1 },
            new SubMain { Name = "a2", Hits = 2, GroupId = 1 },
            new SubMain { Name = "b1", Hits = 3, GroupId = 2 },
        ]);
    }

    // ── Migrations ───────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Migrations_apply_record_and_roll_back()
    {
        var lib = await FreshAsync();
        await SeedAsync(lib);

        // Start from a clean history so the run is deterministic.
        await lib.ExecuteSqlAsync(
            "DELETE FROM public.__migrations WHERE \"MigrationId\" LIKE '%SubFlag%' OR \"MigrationId\" LIKE '%AddSub%'");

        lib.RegisterMigration(new AddSubFlagColumn());
        lib.RegisterMigration(new SeedSubFlag());

        var pending = await lib.GetPendingMigrationsAsync();
        Assert.True(pending.Count >= 2, $"expected 2 pending, saw {pending.Count}");

        var run = await lib.Migrations.MigrateAsync();
        Assert.True(run.IsSuccess, run.Error?.ToString());
        Assert.True(run.Value!.Count >= 2);

        // The column exists and the seed ran.
        var flagged = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM public.it_sub_main WHERE flag = true");
        Assert.Equal(3, flagged.Value);

        // History is in the database, not a file.
        var recorded = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM public.__migrations WHERE \"MigrationId\" LIKE '%SubFlag%' OR \"MigrationId\" LIKE '%AddSub%'");
        Assert.True(recorded.Value >= 2);

        // Re-running is a no-op: applied migrations are not repeated.
        var again = await lib.Migrations.MigrateAsync();
        Assert.True(again.IsSuccess, again.Error?.ToString());
        Assert.Equal(0, again.Value!.Count);

        // Rolling back to the version before these two removes the column again.
        var back = await lib.Migrations.RollbackAsync(new MigrationVersion("0.0.0", 0));
        Assert.True(back.IsSuccess, back.Error?.ToString());

        var stillThere = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='public' AND table_name='it_sub_main' AND column_name='flag'
            """);
        Assert.Equal(0, stillThere.Value);
    }

    // ── Cache ────────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Cached_query_is_served_from_cache_then_invalidated_by_a_write()
    {
        var lib = await FreshAsync();
        await SeedAsync(lib);

        var ttl = TimeSpan.FromMinutes(5);
        var first = await lib.Query<SubMain>().WithCache(ttl).ToListAsync();
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.Equal(3, first.Value!.Count);

        // Mutate behind the cache's back so a cache hit is observable.
        await lib.ExecuteSqlAsync("INSERT INTO public.it_sub_main (name, hits, group_id) VALUES ('ghost', 9, 1)");

        var cached = await lib.Query<SubMain>().WithCache(ttl).ToListAsync();
        Assert.Equal(3, cached.Value!.Count);   // still the cached answer

        // A write through the library bumps the table version, making the entry unreachable.
        await lib.GetRepository<SubMain>().InsertAsync(new SubMain { Name = "real", Hits = 1, GroupId = 1 });

        var afterWrite = await lib.Query<SubMain>().WithCache(ttl).ToListAsync();
        Assert.Equal(5, afterWrite.Value!.Count);   // ghost + real are both visible now
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Cache_stats_track_entries_per_table()
    {
        var lib = await FreshAsync();
        await SeedAsync(lib);

        var ttl = TimeSpan.FromMinutes(5);
        await lib.Query<SubMain>().Where(m => m.Hits > 0).WithCache(ttl).ToListAsync();

        var stats = lib.GetCacheStats();
        Assert.True(stats.TotalEntries > 0);
        Assert.True(stats.EntriesByTable.ContainsKey("it_sub_main"),
            "expected an entry for it_sub_main, saw: " + string.Join(", ", stats.EntriesByTable.Keys));

        // A write bumps the table's version, which is what makes prior entries unreachable.
        var versionBefore = stats.TableVersions.TryGetValue("it_sub_main", out var v) ? v : 0;
        await lib.GetRepository<SubMain>().InsertAsync(new SubMain { Name = "bump", Hits = 1, GroupId = 1 });
        var versionAfter = lib.GetCacheStats().TableVersions["it_sub_main"];
        Assert.True(versionAfter > versionBefore, $"table version did not advance ({versionBefore} -> {versionAfter})");
    }

    // ── Joins and subquery filters ───────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Typed_join_projects_across_both_sides()
    {
        var lib = await FreshAsync();
        await SeedAsync(lib);

        var rows = await lib.Query<SubMain>()
            .Join<SubGroup, long, MainWithGroup>(
                m => m.GroupId,
                g => g.Id,
                (m, g) => new MainWithGroup { Name = m.Name, Label = g.Label })
            .OrderBy((m, g) => m.Name)
            .ToListAsync();

        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal(3, rows.Value!.Count);
        Assert.Equal("alpha", rows.Value![0].Label);
        Assert.Equal("a1", rows.Value![0].Name);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task WhereExists_and_WhereNotExists_filter()
    {
        var lib = await FreshAsync();
        await SeedAsync(lib);

        var inActiveGroup = await lib.Query<SubMain>()
            .WhereExists<SubGroup>((m, g) => g.Id == m.GroupId && g.Active)
            .ToListAsync();
        Assert.True(inActiveGroup.IsSuccess, inActiveGroup.Error?.ToString());
        Assert.Equal(2, inActiveGroup.Value!.Count);

        var notInActiveGroup = await lib.Query<SubMain>()
            .WhereNotExists<SubGroup>((m, g) => g.Id == m.GroupId && g.Active)
            .ToListAsync();
        Assert.True(notInActiveGroup.IsSuccess, notInActiveGroup.Error?.ToString());
        Assert.Single(notInActiveGroup.Value!);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task WhereIn_and_WhereNotIn_filter()
    {
        var lib = await FreshAsync();
        await SeedAsync(lib);

        var inActive = await lib.Query<SubMain>()
            .WhereIn<SubGroup, long>(m => m.GroupId, g => g.Id, g => g.Active)
            .ToListAsync();
        Assert.True(inActive.IsSuccess, inActive.Error?.ToString());
        Assert.Equal(2, inActive.Value!.Count);

        var notInActive = await lib.Query<SubMain>()
            .WhereNotIn<SubGroup, long>(m => m.GroupId, g => g.Id, g => g.Active)
            .ToListAsync();
        Assert.True(notInActive.IsSuccess, notInActive.Error?.ToString());
        Assert.Single(notInActive.Value!);
    }

    // ── Repository helpers ───────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Offset_paging_reports_totals()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<SubMain>();
        await repo.InsertManyAsync(Enumerable.Range(0, 10)
            .Select(i => new SubMain { Name = $"p{i:D2}", Hits = i, GroupId = 1 }).ToList());

        var page = await repo.GetPagedAsync(page: 2, pageSize: 4, orderByColumn: nameof(SubMain.Name));
        Assert.True(page.IsSuccess, page.Error?.ToString());
        Assert.Equal(10, page.Value!.TotalItems);
        Assert.Equal(4, page.Value.Items.Count);
        Assert.Equal("p04", page.Value.Items[0].Name);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task GetPaged_rejects_an_unmapped_order_column()
    {
        var lib = await FreshAsync();
        // The allow-list behind the string-typed ordering parameter.
        var page = await lib.GetRepository<SubMain>()
            .GetPagedAsync(1, 10, orderByColumn: "name\"; DROP TABLE public.it_sub_main --");
        Assert.True(page.IsFailure);

        var alive = await lib.SqlScalarAsync<long>("SELECT count(*) FROM public.it_sub_main");
        Assert.True(alive.IsSuccess);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Increment_and_decrement_are_atomic_server_side()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<SubMain>();
        var row = await repo.InsertAsync(new SubMain { Name = "counter", Hits = 10, GroupId = 1 });

        await repo.IncrementAsync(row.Value!.Id, m => m.Hits, 5);
        await repo.IncrementAsync(row.Value.Id, m => m.Hits, 5);
        await repo.DecrementAsync(row.Value.Id, m => m.Hits, 3);

        var after = await repo.GetByIdAsync(row.Value.Id);
        Assert.Equal(17, after.Value!.Hits);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task GetByColumn_rejects_an_unmapped_column()
    {
        var lib = await FreshAsync();
        var ok = await lib.GetRepository<SubMain>().GetByColumnAsync(nameof(SubMain.Name), "nope");
        Assert.True(ok.IsSuccess, ok.Error?.ToString());

        var bad = await lib.GetRepository<SubMain>()
            .GetByColumnAsync("name\"; DROP TABLE public.it_sub_main --", "x");
        Assert.True(bad.IsFailure);
    }

    // ── Retention ────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Retention_prunes_rows_past_the_window()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_sub_retain\" CASCADE");
        Assert.True((await lib.SyncTableAsync<SubRetain>(createBackup: false)).IsSuccess);

        var repo = lib.GetRepository<SubRetain>();
        await repo.InsertManyAsync([
            new SubRetain { CreatedUtc = DateTime.UtcNow.AddDays(-100) },
            new SubRetain { CreatedUtc = DateTime.UtcNow.AddDays(-60) },
            new SubRetain { CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        ]);

        // PostgreSQL has no DELETE ... LIMIT, so the worker batches by ctid.
        await using var worker = new RetentionWorker(lib.ConnectionManager, null, [typeof(SubRetain)]);
        Assert.True(worker.HasWork);
        var removed = await worker.RunOnceAsync();
        Assert.Equal(2, removed);

        var remaining = await repo.GetAllAsync();
        Assert.Single(remaining.Value!);
        Assert.True(remaining.Value![0].CreatedUtc > DateTime.UtcNow.AddDays(-30));
    }
}
