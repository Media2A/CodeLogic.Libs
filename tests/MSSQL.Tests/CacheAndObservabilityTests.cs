using CL.MSSQL;
using CL.MSSQL.Events;
using CL.MSSQL.Services;
using Xunit;

namespace MSSQL.Tests;

// The cache store contract, the coordinator seam, and the observability recorders — all
// process-local, none previously called directly. The coordinator seam is the one that
// decides whether a multi-node deployment serves stale reads.

public sealed class InProcessCacheStoreTests
{
    private static InProcessCacheStore Store(int max = 100) => new(max);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Set_then_get_round_trips()
    {
        var store = Store();
        await store.SetAsync("k", "value", Ttl, "t1");

        var (found, value) = await store.TryGetAsync("k");
        Assert.True(found);
        Assert.Equal("value", value);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Missing_key_is_reported_as_not_found()
    {
        var (found, value) = await Store().TryGetAsync("absent");
        Assert.False(found);
        Assert.Null(value);
    }

    [Fact]
    public async Task Expired_entry_is_not_served()
    {
        var store = Store();
        await store.SetAsync("k", "v", TimeSpan.FromMilliseconds(1), "t1");
        await Task.Delay(30);
        Assert.False((await store.TryGetAsync("k")).Found);
    }

    [Fact]
    public async Task Evict_removes_a_single_key()
    {
        var store = Store();
        await store.SetAsync("a", 1, Ttl, "t1");
        await store.SetAsync("b", 2, Ttl, "t1");
        await store.EvictAsync("a");

        Assert.False((await store.TryGetAsync("a")).Found);
        Assert.True((await store.TryGetAsync("b")).Found);
    }

    [Fact]
    public async Task EvictByTable_removes_only_that_table()
    {
        var store = Store();
        await store.SetAsync("a1", 1, Ttl, "t1");
        await store.SetAsync("a2", 2, Ttl, "t1");
        await store.SetAsync("b1", 3, Ttl, "t2");

        Assert.Equal(2, await store.EvictByTableAsync("t1"));
        Assert.False((await store.TryGetAsync("a1")).Found);
        Assert.True((await store.TryGetAsync("b1")).Found);
    }

    [Fact]
    public async Task CountByTable_reports_per_table_totals()
    {
        var store = Store();
        await store.SetAsync("a1", 1, Ttl, "t1");
        await store.SetAsync("a2", 2, Ttl, "t1");
        await store.SetAsync("b1", 3, Ttl, "t2");

        var counts = store.CountByTable();
        Assert.Equal(2, counts["t1"]);
        Assert.Equal(1, counts["t2"]);
    }

    [Fact]
    public async Task Clear_empties_the_store()
    {
        var store = Store();
        await store.SetAsync("a", 1, Ttl, "t1");
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Capacity_is_bounded()
    {
        var store = Store(max: 10);
        store.Configure(10);
        for (var i = 0; i < 200; i++) await store.SetAsync($"k{i}", i, Ttl, "t1");
        Assert.True(store.Count <= 20, $"store grew to {store.Count} with a limit of 10");
    }
}

[Collection("codelogic")]
public sealed class QueryCacheFacadeTests
{
    // These mutate process-wide cache state, so they share the serialized collection.
    private readonly MSSQLRuntimeFixture _fx;
    public QueryCacheFacadeTests(MSSQLRuntimeFixture fx) => _fx = fx;

    [Fact]
    public async Task UseStore_and_SetDirect_populate_the_stats()
    {
        QueryCache.UseStore(new InProcessCacheStore());
        try
        {
            await QueryCache.SetDirectAsync("direct-key", "v", TimeSpan.FromMinutes(1), "direct_tbl");
            var stats = QueryCache.GetStats();
            Assert.True(stats.TotalEntries > 0);
            Assert.True(stats.EntriesByTable.ContainsKey("direct_tbl"));
        }
        finally
        {
            QueryCache.UseStore(new InProcessCacheStore());
        }
    }

    [Fact]
    public async Task Invalidate_bumps_the_table_version()
    {
        QueryCache.UseStore(new InProcessCacheStore());
        await QueryCache.SetDirectAsync("k", "v", TimeSpan.FromMinutes(1), "ver_tbl");

        var before = QueryCache.GetStats().TableVersions.TryGetValue("ver_tbl", out var v) ? v : 0;
        QueryCache.Invalidate("ver_tbl");
        Assert.True(QueryCache.GetStats().TableVersions["ver_tbl"] > before);
    }

    // The multi-node fan-out in both directions is already covered by
    // MSSQLTests.CacheCoordinator_multiNode, which also asserts a peer's broadcast is not
    // re-published (no echo loop). Not duplicated here.

    [Fact]
    public async Task Null_coordinator_is_inert_but_functional()
    {
        var c = NullCacheCoordinator.Instance;
        await c.PublishInvalidationAsync("t");
        c.OnInvalidation(_ => throw new InvalidOperationException("never called"));
        Assert.True(await c.TryAcquireRefreshLeaseAsync("p", TimeSpan.FromSeconds(1)));
        await c.DisposeAsync();
    }

    [Fact]
    public void UseStore_and_UseCoordinator_reject_null()
    {
        Assert.Throws<ArgumentNullException>(() => QueryCache.UseStore(null!));
        Assert.Throws<ArgumentNullException>(() => QueryCache.UseCoordinator(null!));
    }
}

[Collection("codelogic")]
public sealed class QueryObservabilityTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mysql => _fx.Mysql;
    public QueryObservabilityTests(MSSQLRuntimeFixture fx) => _fx = fx;

    [DbFact]
    public async Task Each_recorder_publishes_its_event()
    {
        var bus = Mysql.Events ?? throw new InvalidOperationException("no event bus");

        var executed = new List<QueryExecutedEvent>();
        var slow = new List<SlowQueryEvent>();
        var hits = new List<CacheHitEvent>();
        var misses = new List<CacheMissEvent>();
        var n1 = new List<N1QueryDetectedEvent>();

        bus.Subscribe<QueryExecutedEvent>(e => { lock (executed) executed.Add(e); });
        bus.Subscribe<SlowQueryEvent>(e => { lock (slow) slow.Add(e); });
        bus.Subscribe<CacheHitEvent>(e => { lock (hits) hits.Add(e); });
        bus.Subscribe<CacheMissEvent>(e => { lock (misses) misses.Add(e); });
        bus.Subscribe<N1QueryDetectedEvent>(e => { lock (n1) n1.Add(e); });

        QueryObservability.RecordExecuted("Default", "SELECT 1", 12, 3, cacheHit: false);
        QueryObservability.RecordSlow("Default", "WAITFOR DELAY '00:00:01'", 1500);
        QueryObservability.RecordCacheHit("Default", "t", "key-1");
        QueryObservability.RecordCacheMiss("Default", "t", "key-2");
        QueryObservability.RecordN1("Default", "SELECT * FROM t WHERE id = @p", 25);

        for (var i = 0; i < 60 && (executed.Count == 0 || slow.Count == 0 || hits.Count == 0
                                   || misses.Count == 0 || n1.Count == 0); i++)
            await Task.Delay(25);

        Assert.Contains(executed, e => e.Query == "SELECT 1" && e.ElapsedMs == 12 && e.RowCount == 3);
        Assert.Contains(slow, e => e.ElapsedMs == 1500);
        Assert.Contains(hits, e => e.CacheKey == "key-1");
        Assert.Contains(misses, e => e.CacheKey == "key-2");
        Assert.Contains(n1, e => e.Count == 25);
    }

    /// <summary>
    /// HealthChangedEvent was declared in all three libraries but never raised, so a
    /// subscriber waited forever. It now fires on a transition, not on every poll.
    /// </summary>
    [DbFact]
    public async Task Connection_and_health_events_are_published()
    {
        var bus = Mysql.Events ?? throw new InvalidOperationException("no event bus");

        var connected = new List<DatabaseConnectedEvent>();
        var disconnected = new List<DatabaseDisconnectedEvent>();
        var health = new List<HealthChangedEvent>();
        bus.Subscribe<DatabaseConnectedEvent>(e => { lock (connected) connected.Add(e); });
        bus.Subscribe<DatabaseDisconnectedEvent>(e => { lock (disconnected) disconnected.Add(e); });
        bus.Subscribe<HealthChangedEvent>(e => { lock (health) health.Add(e); });

        var conn = await Mysql.ConnectionManager.OpenConnectionAsync();
        await Mysql.ConnectionManager.CloseConnectionAsync(conn);
        await conn.DisposeAsync();

        for (var i = 0; i < 40 && (connected.Count == 0 || disconnected.Count == 0); i++)
            await Task.Delay(25);

        Assert.NotEmpty(connected);
        Assert.Equal("Default", connected[0].ConnectionId);
        Assert.NotEmpty(disconnected);

        // The library is a singleton shared across this collection, so an earlier test may
        // already have established health. Assert the contract that holds either way.
        var baseline = health.Count;
        for (var i = 0; i < 3; i++) Assert.NotNull(await Mysql.HealthCheckAsync());
        await Task.Delay(200);

        var raised = health.Count - baseline;
        Assert.True(raised <= 1, $"three checks with unchanged state raised {raised} events");
        Assert.All(health, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }
}
