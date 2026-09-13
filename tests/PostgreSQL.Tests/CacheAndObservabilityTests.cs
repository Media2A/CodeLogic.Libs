using CL.PostgreSQL;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Services;
using Xunit;

namespace PostgreSQL.Tests;

// The cache store contract, the coordinator seam, and the observability recorders. All of
// this is process-local, so it runs without a database — but none of it had been called
// directly, and the coordinator seam in particular is the one that decides whether a
// multi-node deployment sees stale reads.

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

        var (found, _) = await store.TryGetAsync("k");
        Assert.False(found);
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

        var removed = await store.EvictByTableAsync("t1");

        Assert.Equal(2, removed);
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
        Assert.False((await store.TryGetAsync("a")).Found);
    }

    [Fact]
    public async Task Capacity_is_bounded_so_the_cache_cannot_grow_without_limit()
    {
        var store = Store(max: 10);
        store.Configure(10);

        for (var i = 0; i < 200; i++)
            await store.SetAsync($"k{i}", i, Ttl, "t1");

        // The exact eviction policy is the store's business; not growing unboundedly is not.
        Assert.True(store.Count <= 20, $"store grew to {store.Count} with a limit of 10");
    }
}

/// <summary>
/// Stands in for a Redis-backed coordinator: records what was broadcast and lets a test
/// deliver a peer's invalidation, which is the part a single-process test otherwise never
/// reaches.
/// </summary>
internal sealed class FakeCoordinator : ICacheCoordinator
{
    private readonly List<Action<string>> _handlers = [];
    public List<string> Published { get; } = [];
    public List<string> LeasesRequested { get; } = [];
    public bool GrantLeases { get; set; } = true;

    public Task PublishInvalidationAsync(string tableName, CancellationToken ct = default)
    {
        Published.Add(tableName);
        return Task.CompletedTask;
    }

    public void OnInvalidation(Action<string> handler) => _handlers.Add(handler);

    public Task<bool> TryAcquireRefreshLeaseAsync(string poolName, TimeSpan lease, CancellationToken ct = default)
    {
        LeasesRequested.Add(poolName);
        return Task.FromResult(GrantLeases);
    }

    /// <summary>Simulates a peer node announcing that it wrote to a table.</summary>
    public void DeliverFromPeer(string tableName)
    {
        foreach (var h in _handlers) h(tableName);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[Collection("codelogic")]
public sealed class QueryCacheFacadeTests
{
    // These mutate process-wide cache state, so they share the serialized collection.
    private readonly PostgreSQLRuntimeFixture _fx;
    public QueryCacheFacadeTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    [Fact]
    public async Task UseStore_swaps_the_backing_store()
    {
        var original = new InProcessCacheStore();
        QueryCache.UseStore(original);
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
        var after = QueryCache.GetStats().TableVersions["ver_tbl"];

        Assert.True(after > before, $"version did not advance ({before} -> {after})");
    }

    /// <summary>
    /// The case that makes multi-node caching correct: a write on another node must bump
    /// this node's table version, or this node keeps serving entries the peer invalidated.
    /// </summary>
    [Fact]
    public void A_peer_invalidation_bumps_the_local_version()
    {
        var coordinator = new FakeCoordinator();
        QueryCache.UseStore(new InProcessCacheStore());
        QueryCache.UseCoordinator(coordinator);
        try
        {
            var before = QueryCache.GetStats().TableVersions.TryGetValue("peer_tbl", out var v) ? v : 0;

            coordinator.DeliverFromPeer("peer_tbl");

            var after = QueryCache.GetStats().TableVersions.TryGetValue("peer_tbl", out var w) ? w : 0;
            Assert.True(after > before,
                $"a peer's invalidation must advance the local version ({before} -> {after})");
        }
        finally
        {
            QueryCache.UseCoordinator(NullCacheCoordinator.Instance);
        }
    }

    /// <summary>A local write must be broadcast, or peers keep serving stale reads.</summary>
    [Fact]
    public void A_local_invalidation_is_broadcast_to_peers()
    {
        var coordinator = new FakeCoordinator();
        QueryCache.UseStore(new InProcessCacheStore());
        QueryCache.UseCoordinator(coordinator);
        try
        {
            QueryCache.Invalidate("broadcast_tbl");

            // Publication is fire-and-forget; give it a beat.
            for (var i = 0; i < 40 && coordinator.Published.Count == 0; i++) Thread.Sleep(25);
            Assert.Contains("broadcast_tbl", coordinator.Published);
        }
        finally
        {
            QueryCache.UseCoordinator(NullCacheCoordinator.Instance);
        }
    }

    [Fact]
    public async Task Null_coordinator_is_inert_but_functional()
    {
        var c = NullCacheCoordinator.Instance;
        await c.PublishInvalidationAsync("t");            // no peers: must not throw
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
    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");
    public QueryObservabilityTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "needs the booted runtime for an event bus";

    [FactRequiresEnv(Gate, Reason)]
    public async Task Each_recorder_publishes_its_event()
    {
        var bus = Lib.Events ?? throw new InvalidOperationException("no event bus");

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
        QueryObservability.RecordSlow("Default", "SELECT pg_sleep(1)", 1500);
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
    /// Connection and health events. HealthChangedEvent was declared in all three libraries
    /// but never raised, so anything subscribing to it waited forever; it now fires on a
    /// transition rather than on every poll.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Connection_and_health_events_are_published()
    {
        var bus = Lib.Events ?? throw new InvalidOperationException("no event bus");

        var connected = new List<DatabaseConnectedEvent>();
        var disconnected = new List<DatabaseDisconnectedEvent>();
        var health = new List<HealthChangedEvent>();
        bus.Subscribe<DatabaseConnectedEvent>(e => { lock (connected) connected.Add(e); });
        bus.Subscribe<DatabaseDisconnectedEvent>(e => { lock (disconnected) disconnected.Add(e); });
        bus.Subscribe<HealthChangedEvent>(e => { lock (health) health.Add(e); });

        var conn = await Lib.ConnectionManager.OpenConnectionAsync();
        await Lib.ConnectionManager.CloseConnectionAsync(conn);
        await conn.DisposeAsync();

        for (var i = 0; i < 40 && (connected.Count == 0 || disconnected.Count == 0); i++)
            await Task.Delay(25);

        Assert.NotEmpty(connected);
        Assert.Equal("Default", connected[0].ConnectionId);
        Assert.False(string.IsNullOrWhiteSpace(connected[0].ServerVersion));
        Assert.NotEmpty(disconnected);

        // The library is a singleton shared across this collection, so an earlier test may
        // already have established the health state. Assert the contract that holds either
        // way: repeated checks with unchanged state announce at most one transition.
        var baseline = health.Count;
        for (var i = 0; i < 3; i++) Assert.NotNull(await Lib.HealthCheckAsync());
        await Task.Delay(200);

        var newEvents = health.Count - baseline;
        Assert.True(newEvents <= 1,
            $"three checks with unchanged state raised {newEvents} events; it should fire on transition only");
        Assert.All(health, e =>
        {
            Assert.Equal("Default", e.ConnectionId);
            Assert.False(string.IsNullOrWhiteSpace(e.Message));
        });
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Pool_registry_disposes_every_pool()
    {
        var lib = Lib;
        lib.RegisterCachePool($"disp-{Guid.NewGuid():N}", TimeSpan.FromMinutes(5));
        Assert.NotEmpty(lib.GetCachePoolStats());

        await SmartCachePoolRegistry.DisposeAllAsync();
        Assert.Empty(lib.GetCachePoolStats());
    }
}
