using CL.Storage.Providers;
using Xunit;

namespace Storage.Tests;

/// <summary>
/// Covers the pool that keeps FTP and SFTP sessions alive between operations. The backends used to
/// connect and authenticate on every call, so a paged listing or a multi-step transfer paid one
/// handshake per round trip.
/// </summary>
public sealed class ProviderClientPoolTests
{
    [Fact]
    public async Task ReusesOneConnectionAcrossSequentialOperations()
    {
        var pool = CreatePool(out var connects);

        for (var operation = 0; operation < 50; operation++)
        {
            var client = await pool.RentAsync(CancellationToken.None);
            await pool.ReturnAsync(client);
        }

        Assert.Equal(1, connects());
        Assert.Equal(1, pool.ConnectionsOpened);
    }

    [Fact]
    public async Task ConnectsOncePerConcurrentlyHeldClient()
    {
        var pool = CreatePool(out var connects);

        var held = new List<FakeClient>();
        for (var index = 0; index < 3; index++) held.Add(await pool.RentAsync(CancellationToken.None));
        foreach (var client in held) await pool.ReturnAsync(client);
        await pool.ReturnAsync(await pool.RentAsync(CancellationToken.None));

        Assert.Equal(3, connects());
    }

    [Fact]
    public async Task ReplacesAPooledClientWhoseSessionTheServerDropped()
    {
        var pool = CreatePool(out var connects);

        var first = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(first);
        first.Connected = false;

        var second = await pool.RentAsync(CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.True(first.Destroyed);
        Assert.Equal(2, connects());
    }

    [Fact]
    public async Task DiscardRetiresAClientInsteadOfPoolingIt()
    {
        // Native leases hand the session to caller code, which may leave it in an unexpected state.
        var pool = CreatePool(out var connects);

        var leased = await pool.RentAsync(CancellationToken.None);
        await pool.DiscardAsync(leased);
        await pool.ReturnAsync(await pool.RentAsync(CancellationToken.None));

        Assert.True(leased.Destroyed);
        Assert.Equal(2, connects());
    }

    [Fact]
    public async Task DisposeClosesEveryPooledConnection()
    {
        var pool = CreatePool(out _);

        var held = new List<FakeClient>();
        for (var index = 0; index < 3; index++) held.Add(await pool.RentAsync(CancellationToken.None));
        foreach (var client in held) await pool.ReturnAsync(client);
        await pool.DisposeAsync();

        Assert.All(held, client => Assert.True(client.Destroyed));
    }

    [Fact]
    public async Task DoesNotPoolBeyondItsIdleBound()
    {
        var pool = CreatePool(out _, maxIdle: 2);

        var held = new List<FakeClient>();
        for (var index = 0; index < 4; index++) held.Add(await pool.RentAsync(CancellationToken.None));
        foreach (var client in held) await pool.ReturnAsync(client);

        Assert.Equal(2, held.Count(client => client.Destroyed));
    }

    [Fact]
    public async Task NeverOpensMoreSessionsThanTheLimit()
    {
        var pool = CreatePool(out var connects, new ProviderPoolOptions(MaxSessions: 2, MaxIdle: 2, AcquireTimeout: TimeSpan.FromMilliseconds(50)));

        var first = await pool.RentAsync(CancellationToken.None);
        var second = await pool.RentAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ProviderPoolExhaustedException>(() => pool.RentAsync(CancellationToken.None));
        Assert.Equal(2, connects());
        await pool.ReturnAsync(first);
        await pool.ReturnAsync(second);
    }

    [Fact]
    public async Task AWaitingRenterReceivesTheNextReturnedSession()
    {
        // The waiter must be woken by the return, not by its own acquire timeout.
        var pool = CreatePool(out var connects, new ProviderPoolOptions(MaxSessions: 1, MaxIdle: 1, AcquireTimeout: TimeSpan.FromSeconds(30)));
        var held = await pool.RentAsync(CancellationToken.None);

        var waiting = pool.RentAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        await pool.ReturnAsync(held);
        var handedOver = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(held, handedOver);
        Assert.Equal(1, connects());
    }

    [Fact]
    public async Task FailedConnectReleasesItsSessionSlot()
    {
        var attempts = 0;
        var pool = new ProviderClientPool<FakeClient>(
            () => new FakeClient(),
            (client, _) => ++attempts == 1 ? throw new IOException("refused") : MarkConnected(client),
            client => client.Connected,
            client => { client.Destroyed = true; return ValueTask.CompletedTask; },
            new ProviderPoolOptions(MaxSessions: 1, MaxIdle: 1, AcquireTimeout: TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsAsync<IOException>(() => pool.RentAsync(CancellationToken.None));
        var client = await pool.RentAsync(CancellationToken.None);

        Assert.True(client.Connected);
    }

    [Fact]
    public async Task FaultedSessionsAreDestroyedInsteadOfReused()
    {
        var pool = CreatePool(out var connects);

        var first = await pool.RentAsync(CancellationToken.None);
        pool.MarkFaulted(first);
        await pool.ReturnAsync(first);
        var second = await pool.RentAsync(CancellationToken.None);

        Assert.True(first.Destroyed);
        Assert.NotSame(first, second);
        Assert.Equal(2, connects());
    }

    [Fact]
    public async Task ProbesSessionsIdleLongerThanTheThresholdAndReplacesDeadOnes()
    {
        var time = new ManualTime();
        var count = 0;
        var probes = 0;
        var pool = new ProviderClientPool<FakeClient>(
            () => new FakeClient(),
            (client, _) => { count++; return MarkConnected(client); },
            client => client.Connected,
            client => { client.Destroyed = true; return ValueTask.CompletedTask; },
            new ProviderPoolOptions(ProbeAfterIdle: TimeSpan.FromSeconds(10)),
            (client, _) => { probes++; return Task.FromResult(client.ServerAlive); },
            time);

        var first = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(first);
        await pool.ReturnAsync(await pool.RentAsync(CancellationToken.None));
        Assert.Equal(0, probes);

        first.ServerAlive = false;
        time.Advance(TimeSpan.FromSeconds(11));
        var replacement = await pool.RentAsync(CancellationToken.None);

        Assert.Equal(1, probes);
        Assert.NotSame(first, replacement);
        Assert.True(first.Destroyed);
        Assert.Equal(2, count);
        Assert.Equal(1, pool.Stats.ProbeFailures);
    }

    [Fact]
    public async Task StatsReportIdleAndInUseSessions()
    {
        var pool = CreatePool(out _);

        var a = await pool.RentAsync(CancellationToken.None);
        var b = await pool.RentAsync(CancellationToken.None);
        await pool.ReturnAsync(a);

        Assert.Equal(1, pool.Stats.Idle);
        Assert.Equal(1, pool.Stats.InUse);
        Assert.Equal(2, pool.Stats.Opened);
        await pool.ReturnAsync(b);
    }

    private static Task MarkConnected(FakeClient client)
    {
        client.Connected = true;
        return Task.CompletedTask;
    }

    private static ProviderClientPool<FakeClient> CreatePool(out Func<int> connects, int maxIdle = 4) =>
        CreatePool(out connects, new ProviderPoolOptions(MaxIdle: maxIdle));

    private static ProviderClientPool<FakeClient> CreatePool(out Func<int> connects, ProviderPoolOptions options)
    {
        var count = 0;
        connects = () => Volatile.Read(ref count);
        return new ProviderClientPool<FakeClient>(
            () => new FakeClient(),
            (client, _) => { Interlocked.Increment(ref count); client.Connected = true; return Task.CompletedTask; },
            client => client.Connected,
            client => { client.Destroyed = true; client.Connected = false; return ValueTask.CompletedTask; },
            options);
    }

    private sealed class FakeClient
    {
        public bool Connected { get; set; }
        public bool Destroyed { get; set; }
        public bool ServerAlive { get; set; } = true;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
