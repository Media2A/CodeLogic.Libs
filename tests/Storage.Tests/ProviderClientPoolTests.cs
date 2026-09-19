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

    private static ProviderClientPool<FakeClient> CreatePool(out Func<int> connects, int maxIdle = 4)
    {
        var count = 0;
        connects = () => Volatile.Read(ref count);
        return new ProviderClientPool<FakeClient>(
            () => new FakeClient(),
            (client, _) => { Interlocked.Increment(ref count); client.Connected = true; return Task.CompletedTask; },
            client => client.Connected,
            client => { client.Destroyed = true; client.Connected = false; return ValueTask.CompletedTask; },
            maxIdle);
    }

    private sealed class FakeClient
    {
        public bool Connected { get; set; }
        public bool Destroyed { get; set; }
    }
}
