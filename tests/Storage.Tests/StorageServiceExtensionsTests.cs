using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StorageServiceExtensionsTests
{
    [Fact]
    public async Task Enumerate_pages_preserves_options_and_follows_opaque_tokens()
    {
        var observed = new List<StorageListOptions>();
        var backend = new FakeStorageBackend(
            "Pages",
            list: (_, options, _) =>
            {
                observed.Add(options!);
                return Task.FromResult(Result<StoragePage>.Success(
                    options!.ContinuationToken switch
                    {
                        "start" => new StoragePage([File("one")], "opaque-2"),
                        "opaque-2" => new StoragePage([File("two")], "opaque-3"),
                        _ => new StoragePage([File("three")])
                    }));
            });

        var pages = await backend.EnumeratePagesAsync(
            "root",
            new StorageListOptions
            {
                Recursive = true,
                PageSize = 17,
                ContinuationToken = "start"
            }).ToListAsync();

        Assert.Equal(3, pages.Count);
        Assert.All(pages, page => Assert.True(page.IsSuccess, page.Error?.Message));
        Assert.Equal(["one", "two", "three"], pages.SelectMany(page => page.Value!.Items).Select(item => item.Path));
        Assert.Equal(["start", "opaque-2", "opaque-3"], observed.Select(option => option.ContinuationToken));
        Assert.All(observed, option =>
        {
            Assert.True(option.Recursive);
            Assert.Equal(17, option.PageSize);
        });
    }

    [Fact]
    public async Task Enumerate_items_surfaces_provider_failure_once_and_stops()
    {
        var calls = 0;
        var backend = new FakeStorageBackend(
            "Items",
            list: (_, options, _) =>
            {
                calls++;
                return Task.FromResult(options!.ContinuationToken is null
                    ? Result<StoragePage>.Success(new StoragePage([File("one"), File("two")], "next"))
                    : Result<StoragePage>.Failure(StorageErrors.Unavailable("offline")));
            });

        var items = await backend.EnumerateItemsAsync("root").ToListAsync();

        Assert.Equal(3, items.Count);
        Assert.Equal(["one", "two"], items.Take(2).Select(item => item.Value!.Path));
        Assert.True(items[2].IsFailure);
        Assert.Equal(StorageErrors.UnavailableCode, items[2].Error!.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Enumerate_pages_detects_a_repeated_continuation_token()
    {
        var backend = new FakeStorageBackend(
            "Loop",
            list: (_, _, _) => Task.FromResult(Result<StoragePage>.Success(
                new StoragePage([], "same-token"))));

        var pages = await backend.EnumeratePagesAsync("root").ToListAsync();

        Assert.Equal(3, pages.Count);
        Assert.True(pages[0].IsSuccess);
        Assert.True(pages[1].IsSuccess);
        Assert.True(pages[2].IsFailure);
        Assert.Equal(StorageErrors.ProviderErrorCode, pages[2].Error!.Code);
    }

    [Fact]
    public async Task Get_info_batch_is_bounded_ordered_and_keeps_per_item_failures()
    {
        var active = 0;
        var maximumActive = 0;
        var backend = new FakeStorageBackend(
            "Batch",
            getInfo: async (path, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximumActive, current);
                try
                {
                    await Task.Delay(5, cancellationToken);
                    return path == "item-5"
                        ? Result<StorageItem>.Failure(StorageErrors.NotFound("missing"))
                        : Result<StorageItem>.Success(File(path));
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        var paths = Enumerable.Range(0, 12).Select(index => $"item-{index}").ToArray();

        var batch = await backend.GetInfoBatchAsync(
            paths,
            new StorageBatchOptions { MaxConcurrency = 3 });

        Assert.True(batch.IsSuccess, batch.Error?.Message);
        var results = batch.Value!;
        Assert.Equal(paths, results.Select(item => item.Path));
        Assert.Equal(Enumerable.Range(0, paths.Length), results.Select(item => item.Index));
        Assert.Equal(StorageErrors.NotFoundCode, results[5].Result.Error!.Code);
        Assert.All(results.Where(item => item.Index != 5), item => Assert.True(item.Result.IsSuccess));
        Assert.InRange(maximumActive, 2, 3);
    }

    [Fact]
    public async Task Mutation_batch_maps_unexpected_item_exception_without_losing_other_results()
    {
        var backend = new FakeStorageBackend(
            "DeleteBatch",
            delete: (path, _) => path switch
            {
                "throws" => throw new IOException("raw provider detail"),
                "fails" => Task.FromResult(Result.Failure(StorageErrors.Conflict("in use"))),
                _ => Task.FromResult(Result.Success())
            });

        var batch = await backend.DeleteBatchAsync(["ok", "throws", "fails"]);

        Assert.True(batch.IsSuccess, batch.Error?.Message);
        Assert.True(batch.Value![0].Result.IsSuccess);
        Assert.Equal(StorageErrors.ProviderErrorCode, batch.Value[1].Result.Error!.Code);
        Assert.DoesNotContain("raw provider detail", batch.Value[1].Result.Error!.Details, StringComparison.Ordinal);
        Assert.Equal(StorageErrors.ConflictCode, batch.Value[2].Result.Error!.Code);
    }

    private static StorageItem File(string path) => new()
    {
        Path = path,
        Name = path,
        ItemType = StorageItemType.File
    };

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
            }
        }
    }
}
