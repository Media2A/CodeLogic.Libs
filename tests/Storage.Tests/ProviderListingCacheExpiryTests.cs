using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>
/// Covers snapshot expiry on a manually advanced clock. Expiry runs from last use rather than from
/// creation, because a consumer doing real per-entry work between pages can easily take longer than
/// the idle lifetime to page a large listing.
/// </summary>
public sealed class ProviderListingCacheExpiryTests
{
    private const string Scope = "connection|1|folder";

    [Fact]
    public async Task ASnapshotSurvivesAPassLongerThanTheIdleLifetime()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new ProviderListingCache(time);
        var source = Listing(1_000);
        var walks = 0;
        var delivered = new List<string>();
        string? token = null;

        do
        {
            var page = await ProviderPaging.CreateAsync(
                Scope,
                new StorageListOptions { PageSize = 40, ContinuationToken = token },
                _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source.AsEnumerable())); },
                CancellationToken.None,
                cache);

            Assert.True(page.IsSuccess);
            delivered.AddRange(page.Value!.Items.Select(item => item.Path));
            token = page.Value.ContinuationToken;

            // Four minutes of caller work per page. Twenty-five pages is one hundred minutes in
            // total, far past the five-minute idle lifetime, but no single gap reaches it.
            time.Advance(TimeSpan.FromMinutes(4));
        }
        while (token is not null);

        Assert.Equal(1, walks);
        Assert.Equal(source.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray(), delivered);
    }

    [Fact]
    public async Task ASnapshotIdleBeyondTheIdleLifetimeIsReWalked()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new ProviderListingCache(time);
        var source = Listing(100);
        var walks = 0;

        var first = await ProviderPaging.CreateAsync(
            Scope,
            new StorageListOptions { PageSize = 10 },
            _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source.AsEnumerable())); },
            CancellationToken.None,
            cache);

        time.Advance(ProviderListingCache.IdleLifetime + TimeSpan.FromSeconds(1));

        var second = await ProviderPaging.CreateAsync(
            Scope,
            new StorageListOptions { PageSize = 10, ContinuationToken = first.Value!.ContinuationToken },
            _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source.AsEnumerable())); },
            CancellationToken.None,
            cache);

        Assert.True(second.IsSuccess);
        Assert.Equal(2, walks);
    }

    [Fact]
    public async Task AnActivePassKeepsItsSnapshotHoweverLongTheWholePassRuns()
    {
        // There is no absolute age ceiling: expiring mid-pass would splice two different views of
        // the directory into one paged result rather than making it fresher.
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new ProviderListingCache(time);
        var source = Listing(4_000);
        var walks = 0;
        var delivered = new List<string>();
        string? token = null;

        do
        {
            var page = await ProviderPaging.CreateAsync(
                Scope,
                new StorageListOptions { PageSize = 40, ContinuationToken = token },
                _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source.AsEnumerable())); },
                CancellationToken.None,
                cache);

            Assert.True(page.IsSuccess);
            delivered.AddRange(page.Value!.Items.Select(item => item.Path));
            token = page.Value.ContinuationToken;
            time.Advance(TimeSpan.FromMinutes(4));
        }
        while (token is not null);

        // One hundred pages four minutes apart is over six hours of wall time on one snapshot.
        Assert.Equal(1, walks);
        Assert.Equal(source.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray(), delivered);
    }

    private static StorageItem[] Listing(int count) =>
        [.. Enumerable.Range(0, count).Select(index =>
        {
            var name = $"{(index * 7919) % count:D6}/file-{index:D6}.bin";
            return new StorageItem { Path = name, Name = name, ItemType = StorageItemType.File, Size = index };
        })];

    /// <summary>Manually advanced clock, so expiry is exercised without waiting on wall time.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
