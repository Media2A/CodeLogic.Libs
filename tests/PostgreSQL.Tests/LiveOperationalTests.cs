using CL.PostgreSQL;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using CodeLogic.Core.Events;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// The operational surface: sync modes and drift deferral, cross-connection locking, smart
// cache pools, and the observability events. These are the behaviours an operator relies on
// and the ones least likely to be noticed when they silently stop working.

[Table(Name = "it_mode", Schema = "public")]
public sealed class ModeRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "keep", Size = 30, NotNull = true)] public string Keep { get; set; } = "";
}

[Collection("codelogic")]
public sealed class LiveOperationalTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveOperationalTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task ClearSentinelAsync(string table) =>
        await Lib.ExecuteSqlAsync(
            "DELETE FROM public.__schema_state WHERE \"TableName\" = @t",
            // Sentinel rows are keyed schema.table, not by the bare name.
            new Dictionary<string, object?> { ["@t"] = $"public.{table}" });

    private void SetMode(SyncMode mode) =>
        Lib.SetSyncMode(mode);

    // ── Sync modes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Production is additive: a column the model no longer declares must survive, and the
    /// table must be reported as drifting rather than quietly rewritten.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Production_mode_defers_a_drop_and_flags_drift()
    {
        var lib = Lib;
        SetMode(SyncMode.Production);
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_mode\" CASCADE");
        Assert.True((await lib.SyncTableAsync<ModeRow>(createBackup: false)).IsSuccess);

        // A column the entity does not know about.
        await lib.ExecuteSqlAsync("ALTER TABLE public.it_mode ADD COLUMN stray text");
        await lib.ExecuteSqlAsync("INSERT INTO public.it_mode (keep, stray) VALUES ('a', 'precious')");
        await ClearSentinelAsync("it_mode");

        var sync = await lib.SyncTableAsync<ModeRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        var survived = await lib.SqlScalarAsync<string>("SELECT stray FROM public.it_mode LIMIT 1");
        Assert.Equal("precious", survived.Value);
        Assert.True(sync.Value!.DriftPending, "production mode should flag the deferred drop as drift");
    }

    /// <summary>Developer mode reconciles destructively: the same stray column is dropped.</summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Developer_mode_drops_a_removed_column()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_mode\" CASCADE");
        SetMode(SyncMode.Production);
        Assert.True((await lib.SyncTableAsync<ModeRow>(createBackup: false)).IsSuccess);

        await lib.ExecuteSqlAsync("ALTER TABLE public.it_mode ADD COLUMN stray text");
        await ClearSentinelAsync("it_mode");

        SetMode(SyncMode.Developer);
        try
        {
            var sync = await lib.SyncTableAsync<ModeRow>(createBackup: false);
            Assert.True(sync.IsSuccess, sync.Error?.ToString());
            Assert.Contains(sync.Value!.Operations, o => o.Contains("DROP COLUMN", StringComparison.OrdinalIgnoreCase));

            var gone = await lib.SqlScalarAsync<long>("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema='public' AND table_name='it_mode' AND column_name='stray'
                """);
            Assert.Equal(0, gone.Value);
        }
        finally
        {
            SetMode(SyncMode.Production);   // never leave the fixture in a destructive mode
        }
    }

    // ── Cross-connection advisory lock ───────────────────────────────────────────

    /// <summary>
    /// The lock must exclude a genuinely separate connection manager, not just a second
    /// scope on the same one — that is the multi-instance case it exists for.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Advisory_lock_excludes_an_independent_connection_manager()
    {
        var config = new PostgreSqlDatabaseConfig
        {
            Host = Environment.GetEnvironmentVariable("CL_PG_TEST_HOST") ?? "127.0.0.1",
            Port = int.Parse(Environment.GetEnvironmentVariable("CL_PG_TEST_PORT") ?? "5432"),
            Database = Environment.GetEnvironmentVariable("CL_PG_TEST_DB") ?? "postgres",
            Username = Environment.GetEnvironmentVariable("CL_PG_TEST_USER") ?? "postgres",
            Password = Environment.GetEnvironmentVariable("CL_PG_TEST_PASS") ?? "",
        };

        // A second manager stands in for a second application instance.
        var otherNode = new ConnectionManager(null, null);
        otherNode.RegisterConfiguration(config, "Default");

        await using (var held = await SchemaSyncLock.AcquireAsync(Lib.ConnectionManager, timeoutSeconds: 5))
        {
            Assert.True(held.Acquired);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await using var blocked = await SchemaSyncLock.AcquireAsync(otherNode, timeoutSeconds: 1);
            sw.Stop();

            Assert.False(blocked.Acquired, "a second instance must not get the lock while it is held");
            // It waited for the timeout rather than returning instantly or hanging.
            Assert.InRange(sw.ElapsedMilliseconds, 500, 10_000);
        }

        await using var afterRelease = await SchemaSyncLock.AcquireAsync(otherNode, timeoutSeconds: 5);
        Assert.True(afterRelease.Acquired, "the lock must be released when the holder's scope ends");
    }

    /// <summary>The lock key must be stable across processes, or two nodes take different locks.</summary>
    [Fact]
    public void Advisory_lock_key_is_stable()
    {
        Assert.Equal(SchemaSyncLock.KeyFor(SchemaSyncLock.LockName), SchemaSyncLock.KeyFor(SchemaSyncLock.LockName));
        Assert.NotEqual(SchemaSyncLock.KeyFor("a"), SchemaSyncLock.KeyFor("b"));
    }

    // ── Smart cache pools ────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Smart_cache_pool_serves_and_refreshes()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_mode\" CASCADE");
        Assert.True((await lib.SyncTableAsync<ModeRow>(createBackup: false)).IsSuccess);
        await lib.GetRepository<ModeRow>().InsertManyAsync([
            new ModeRow { Keep = "x" }, new ModeRow { Keep = "y" },
        ]);

        var pool = $"it-pool-{Guid.NewGuid():N}";
        lib.RegisterCachePool(pool, TimeSpan.FromMinutes(10));

        var first = await lib.Query<ModeRow>().SmartCache(pool).ToListAsync();
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.Equal(2, first.Value!.Count);

        // Behind the pool's back, so a pool hit is observable.
        await lib.ExecuteSqlAsync("INSERT INTO public.it_mode (keep) VALUES ('z')");

        var cached = await lib.Query<ModeRow>().SmartCache(pool).ToListAsync();
        Assert.Equal(2, cached.Value!.Count);

        // An explicit refresh re-runs the underlying query.
        await lib.RefreshCachePoolAsync(pool);
        var refreshed = await lib.Query<ModeRow>().SmartCache(pool).ToListAsync();
        Assert.Equal(3, refreshed.Value!.Count);

        var stats = lib.GetCachePoolStats().SingleOrDefault(p => p.Name == pool);
        Assert.NotNull(stats);
        Assert.True(stats!.EntryCount > 0);
    }

    /// <summary>
    /// A query naming a pool that was never registered must still return correct data,
    /// uncached, rather than throwing.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Unregistered_pool_falls_back_to_an_uncached_query()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_mode\" CASCADE");
        Assert.True((await lib.SyncTableAsync<ModeRow>(createBackup: false)).IsSuccess);
        await lib.GetRepository<ModeRow>().InsertAsync(new ModeRow { Keep = "only" });

        var rows = await lib.Query<ModeRow>().SmartCache("never-registered").ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Single(rows.Value!);
    }

    // ── Observability ────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Query_and_table_sync_publish_events()
    {
        var lib = Lib;
        var bus = lib.Events ?? throw new InvalidOperationException("library has no event bus");

        var executed = new List<QueryExecutedEvent>();
        var synced = new List<TableSyncedEvent>();
        bus.Subscribe<QueryExecutedEvent>(e => { lock (executed) executed.Add(e); });
        bus.Subscribe<TableSyncedEvent>(e => { lock (synced) synced.Add(e); });

        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_mode\" CASCADE");
        Assert.True((await lib.SyncTableAsync<ModeRow>(createBackup: false)).IsSuccess);
        await lib.GetRepository<ModeRow>().InsertAsync(new ModeRow { Keep = "evt" });
        await lib.Query<ModeRow>().ToListAsync();

        // Events are published without awaiting the subscribers, so allow a beat.
        for (var i = 0; i < 40 && (executed.Count == 0 || synced.Count == 0); i++)
            await Task.Delay(25);

        Assert.NotEmpty(synced);
        Assert.Equal("public", synced[0].SchemaName);
        Assert.Equal("it_mode", synced[0].TableName);

        Assert.NotEmpty(executed);
        Assert.Contains(executed, e => e.Query.Contains("it_mode", StringComparison.OrdinalIgnoreCase));
        Assert.All(executed, e => Assert.True(e.ElapsedMs >= 0));
    }
}
