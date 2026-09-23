using System.Runtime.CompilerServices;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Tests;

/// <summary>Local upload conditions, watching after a native failure, and idle shared pools at shutdown.</summary>
public sealed class ReviewMiscTests
{
    [Fact]
    public async Task Local_uploads_honour_a_condition_checked_right_before_the_replace()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        await storage.UploadBytesAsync("f.txt", [1]);
        var seen = (await storage.GetInfoAsync("f.txt")).Value!;

        var matching = await storage.UploadBytesAsync("f.txt", [2, 2], new StorageUploadOptions { Condition = new StorageMutationCondition { ExpectedETag = seen.ETag } });
        var stale = await storage.UploadBytesAsync("f.txt", [3, 3, 3], new StorageUploadOptions { Condition = new StorageMutationCondition { ExpectedETag = seen.ETag } });

        Assert.True(matching.IsSuccess, matching.Error?.ToString());
        Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
        Assert.Equal([2, 2], (await storage.DownloadBytesAsync("f.txt")).Value!);
        Assert.Single((await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items);
    }

    [Fact]
    public async Task Watching_goes_on_by_polling_after_native_notifications_fail()
    {
        using var directory = new TestDirectory();
        var local = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        var storage = new FailingWatchBackend(local);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var changes = storage.WatchAsync("", new StorageWatchOptions { PollInterval = TimeSpan.FromMilliseconds(100) }, stop.Token).GetAsyncEnumerator(stop.Token);

        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(StorageChangeKind.Overflow, changes.Current.Kind);
        await Task.Delay(50);
        await local.UploadBytesAsync("after.txt", [1]);
        Assert.True(await changes.MoveNextAsync());

        Assert.Equal((StorageChangeKind.Created, "after.txt"), (changes.Current.Kind, changes.Current.Path));
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Idle_shared_resources_are_disposed_at_once_when_flushed()
    {
        var key = Guid.NewGuid().ToString("N");
        var disposed = 0;
        _ = SharedResources.Acquire(key, () => new object(), _ => { Interlocked.Increment(ref disposed); return ValueTask.CompletedTask; });
        var lingering = SharedResources.ReleaseAsync(key, TimeSpan.FromMinutes(10)).AsTask();

        await SharedResources.FlushIdleAsync();
        await lingering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, disposed);
    }

    /// <summary>A local connection whose native notifications fail at once, like a watched share that dropped.</summary>
    private sealed class FailingWatchBackend(LocalStorageBackend inner) : HookedBackend(inner, (_, _) => Task.CompletedTask), IStorageWatchService
    {
        public async IAsyncEnumerable<StorageChange> WatchNativeAsync(string path, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new IOException("The network share is no longer available.");
#pragma warning disable CS0162 // An iterator needs a yield; this one fails before reaching it.
            yield break;
#pragma warning restore CS0162
        }
    }
}
