using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Sync;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Watch findings from the round-3 review (needs-review.md, B59).</summary>
public sealed class NeedsReviewWatchTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------ B59: watching

    private static async Task<List<StorageChange>> WatchUntilAsync(IStorageService storage, StorageWatchOptions options, Func<StorageChange, bool> last)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var seen = new List<StorageChange>();
        await foreach (var change in storage.WatchAsync("", options, stop.Token))
        {
            seen.Add(change);
            if (last(change)) break;
        }
        return seen;
    }

    [Fact] // needs-review B59
    public async Task A_failed_first_listing_does_not_report_everything_as_created()
    {
        var storage = new MemoryStorage("w");
        storage.Put("existing.txt", "e", Old);
        var lists = 0;
        var errors = new List<Error>();
        storage.FailList = (_, page) =>
        {
            if (page != 0) return null;
            var count = Interlocked.Increment(ref lists);
            if (count == 1) return StorageErrors.Unavailable("busy");
            if (count == 4) storage.Put("new.txt", "n", Old);
            return null;
        };

        var seen = await WatchUntilAsync(storage, new StorageWatchOptions { ForcePolling = true, PollInterval = TimeSpan.FromMilliseconds(30), PollFailed = error => errors.Add(error) },
            change => change.Path == "new.txt");

        Assert.Equal([("new.txt", StorageChangeKind.Created)], seen.Select(change => (change.Path, change.Kind)));
        Assert.Equal(StorageErrors.UnavailableCode, Assert.Single(errors).Code);
    }

    [Fact] // needs-review B59
    public async Task A_folder_vanishing_part_way_through_a_poll_does_not_report_mass_deletes()
    {
        var storage = new MemoryStorage("w") { MaxPageSize = 1 };
        storage.Put("a.txt", "a", Old);
        storage.Put("b.txt", "b", Old);
        storage.Put("c.txt", "c", Old);
        var listings = 0;
        var secondPages = 0;
        storage.FailList = (_, page) =>
        {
            if (page == 0 && Interlocked.Increment(ref listings) == 4) storage.Put("new.txt", "n", Old);
            return page == 1 && Interlocked.Increment(ref secondPages) == 2 ? StorageErrors.NotFound("gone") : null;
        };

        var seen = await WatchUntilAsync(storage, new StorageWatchOptions { ForcePolling = true, PollInterval = TimeSpan.FromMilliseconds(30) },
            change => change.Path == "new.txt");

        Assert.DoesNotContain(seen, change => change.Kind == StorageChangeKind.Deleted);
        Assert.Equal(StorageChangeKind.Created, Assert.Single(seen).Kind);
    }

    [Fact] // needs-review B59
    public async Task Native_watching_that_cannot_start_falls_back_to_polling()
    {
        var storage = new MemoryStorage("w", extra: StorageFeature.ChangeNotifications)
        {
            WatchNative = (_, _, _) => throw new DirectoryNotFoundException("The folder is not there yet.")
        };

        var seen = await WatchUntilAsync(storage, new StorageWatchOptions { PollInterval = TimeSpan.FromMilliseconds(30) }, _ => true);

        Assert.Equal(StorageChangeKind.Overflow, Assert.Single(seen).Kind);
    }
}
