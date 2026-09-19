using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>
/// Covers the shared listing cursor used by the providers without native server-side paging.
/// These run entirely in memory: the point of the fix is that paging logic no longer needs a live
/// server to be exercised.
/// </summary>
public sealed class ProviderPagingTests
{
    private const string Scope = "connection|1|folder";

    [Fact]
    public async Task PagesEveryEntryExactlyOnceInOrdinalOrder()
    {
        var source = Listing(1_000);
        var cache = new ProviderListingCache();

        var (delivered, _, _) = await PageThroughAsync(source, pageSize: 40, cache);

        var expected = source.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, delivered);
    }

    [Fact]
    public async Task WalksTheListingOnceForTheWholePassRatherThanOncePerPage()
    {
        // The regression this guards: an offset token re-materialised the listing on every call, so
        // a 1,000-entry folder at 40 per page cost 25 complete walks instead of one.
        var source = Listing(1_000);
        var cache = new ProviderListingCache();

        var (delivered, pages, walks) = await PageThroughAsync(source, pageSize: 40, cache);

        Assert.Equal(25, pages);
        Assert.Equal(1_000, delivered.Count);
        Assert.Equal(1, walks);
    }

    [Fact]
    public async Task NeverRepeatsAContinuationToken()
    {
        // At least one consumer treats a repeated token as a provider integrity fault.
        var cache = new ProviderListingCache();
        var (_, _, _, tokens) = await PageThroughCollectingTokensAsync(Listing(500), pageSize: 7, cache);

        Assert.NotEmpty(tokens);
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task SmallPageSizesStillDeliverTheWholeListingInOrder()
    {
        var source = Listing(101);
        var cache = new ProviderListingCache();

        var (delivered, pages, walks) = await PageThroughAsync(source, pageSize: 1, cache);

        Assert.Equal(101, pages);
        Assert.Equal(1, walks);
        Assert.Equal(delivered.OrderBy(path => path, StringComparer.Ordinal).ToArray(), delivered);
    }

    [Fact]
    public async Task ReWalksAndStillDeliversEachEntryOnceWhenTheSnapshotIsGone()
    {
        // Documented degradation: a token presented after its snapshot expired costs another walk
        // but must never drop or duplicate an entry.
        var source = Listing(120);
        var walks = 0;
        var delivered = new List<string>();
        string? token = null;

        do
        {
            // A fresh cache per call models a snapshot that is never there when the token returns.
            var page = await ProviderPaging.CreateAsync(
                Scope,
                new StorageListOptions { PageSize = 25, ContinuationToken = token },
                _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source)); },
                CancellationToken.None,
                new ProviderListingCache());

            Assert.True(page.IsSuccess);
            delivered.AddRange(page.Value!.Items.Select(item => item.Path));
            token = page.Value.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(5, walks);
        Assert.Equal(source.Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray(), delivered);
    }

    [Fact]
    public async Task ATokenFromOneListingDoesNotReuseAnothersSnapshotAndResumesFromItsPath()
    {
        // The documented contract: a foreign token is a cache miss, not an error. The other listing
        // is walked, then resumed from the path the token carries, so the caller sees a listing
        // truncated at that path. Rejecting it instead would make the same call fail or succeed
        // depending on whether the snapshot happened to still be cached.
        var source = Listing(100);
        var cache = new ProviderListingCache();
        var first = await ProviderPaging.CreateAsync(
            Scope,
            new StorageListOptions { PageSize = 10 },
            _ => Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source.AsEnumerable())),
            CancellationToken.None,
            cache);

        var carriedPath = first.Value!.Items[^1].Path;
        var walks = 0;
        var second = await ProviderPaging.CreateAsync(
            "connection|1|other-folder",
            new StorageListOptions { PageSize = 10, ContinuationToken = first.Value.ContinuationToken },
            _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source.AsEnumerable())); },
            CancellationToken.None,
            cache);

        Assert.True(second.IsSuccess);
        Assert.Equal(1, walks);

        // Resumed strictly after the carried path rather than from the start of the other listing.
        var expected = source.Select(item => item.Path)
            .Where(path => StringComparer.Ordinal.Compare(path, carriedPath) > 0)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(10)
            .ToArray();
        Assert.Equal(expected, second.Value!.Items.Select(item => item.Path));
    }

    [Fact]
    public async Task ASinglePageListingIsNotCached()
    {
        // Nobody can resume a listing that returned no continuation token, so caching it would only
        // churn a bounded, process-wide cache.
        var cache = new ProviderListingCache();

        var page = await ProviderPaging.CreateAsync(
            Scope,
            new StorageListOptions { PageSize = 50 },
            _ => Task.FromResult(Result<IEnumerable<StorageItem>>.Success(Listing(20).AsEnumerable())),
            CancellationToken.None,
            cache);

        Assert.True(page.IsSuccess);
        Assert.Null(page.Value!.ContinuationToken);
        Assert.Equal(0, cache.Count);
    }

    [Theory]
    [InlineData("not base64 at all !!")]
    [InlineData("dGhpcyBpcyBub3QgYSB0b2tlbg==")]
    public async Task RejectsAMalformedToken(string token)
    {
        var result = await ProviderPaging.CreateAsync(
            Scope,
            new StorageListOptions { PageSize = 10, ContinuationToken = token },
            _ => Task.FromResult(Result<IEnumerable<StorageItem>>.Success(Listing(10).AsEnumerable())),
            CancellationToken.None,
            new ProviderListingCache());

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task DoesNotCacheListingsLargerThanTheBudget()
    {
        // Oversized listings degrade to the old re-walk behaviour instead of evicting everything.
        var source = Listing(ProviderListingCache.MaxItems + 1);
        var cache = new ProviderListingCache();

        var (_, pages, walks) = await PageThroughAsync(source, pageSize: 100_000, cache);

        Assert.Equal(walks, pages);
    }

    private static StorageItem[] Listing(int count) =>
        [.. Enumerable.Range(0, count).Select(index =>
        {
            // Deliberately unsorted input with mixed depth, so ordinal ordering is actually tested.
            var name = $"{(index * 7919) % count:D6}/file-{index:D6}.bin";
            return new StorageItem { Path = name, Name = name, ItemType = StorageItemType.File, Size = index };
        })];

    private static async Task<(List<string> Delivered, int Pages, int Walks)> PageThroughAsync(
        StorageItem[] source, int pageSize, ProviderListingCache cache)
    {
        var (delivered, pages, walks, _) = await PageThroughCollectingTokensAsync(source, pageSize, cache);
        return (delivered, pages, walks);
    }

    private static async Task<(List<string> Delivered, int Pages, int Walks, List<string> Tokens)> PageThroughCollectingTokensAsync(
        StorageItem[] source, int pageSize, ProviderListingCache cache)
    {
        var delivered = new List<string>();
        var tokens = new List<string>();
        var walks = 0;
        var pages = 0;
        string? token = null;

        do
        {
            var page = await ProviderPaging.CreateAsync(
                Scope,
                new StorageListOptions { PageSize = pageSize, ContinuationToken = token },
                _ => { walks++; return Task.FromResult(Result<IEnumerable<StorageItem>>.Success(source)); },
                CancellationToken.None,
                cache);

            Assert.True(page.IsSuccess);
            pages++;
            delivered.AddRange(page.Value!.Items.Select(item => item.Path));
            token = page.Value.ContinuationToken;
            if (token is not null) tokens.Add(token);
        }
        while (token is not null);

        return (delivered, pages, walks, tokens);
    }
}
