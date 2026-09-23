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

/// <summary>Remaining review test gaps: resume across library instances, and baselines after partial runs.</summary>
public sealed class ReviewGapTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_resume_token_continues_in_a_new_library_instance()
    {
        using var directory = new TestDirectory();
        var a = directory.CreateDirectory("a");
        var b = directory.CreateDirectory("b");
        var content = Enumerable.Range(0, 250_000).Select(i => (byte)(i % 249)).ToArray();
        var attempts = 0;
        FakeStorageBackend Source() => new(
            "Src",
            getInfo: (path, _) => Task.FromResult(CodeLogic.Core.Results.Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = content.Length, ETag = "\"v1\"" })),
            downloadWithOptions: (_, options, _) =>
            {
                var rest = content[(int)(options?.Offset ?? 0)..];
                Stream stream = Interlocked.Increment(ref attempts) == 1 ? new FailingStream(rest, failAfter: 100_000) : new MemoryStream(rest);
                return Task.FromResult(CodeLogic.Core.Results.Result<Stream>.Success(stream));
            });
        async Task<global::CL.Storage.StorageLibrary> OpenAsync(string name)
        {
            var library = new global::CL.Storage.StorageLibrary();
            await StorageLibraryTestSupport.InitializeAsync(library, StorageLibraryTestSupport.CreateContext(directory.CreateDirectory(name)), configureLocal: local =>
            {
                local.Connections["Default"] = new() { RootPath = a };
                local.Connections["B"] = new() { RootPath = b };
            });
            Assert.True(library.RegisterBackend("Src", Source()).IsSuccess);
            return library;
        }
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };

        StorageResumeToken token;
        using (var first = await OpenAsync("one"))
            token = (await first.CopyAsync("Src", "big.bin", "B", "big.bin", options)).ResumeToken!;
        using var second = await OpenAsync("two");
        // The token is data: it can be stored, and read back by another process.
        var stored = System.Text.Json.JsonSerializer.Deserialize<StorageResumeToken>(System.Text.Json.JsonSerializer.Serialize(token))!;
        var resumed = await second.CopyAsync("Src", "big.bin", "B", "big.bin", options with { ResumeToken = stored });

        Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
        Assert.Equal(token.BytesStaged, resumed.BytesResumed);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(b, "big.bin")));
    }

    [Fact]
    public async Task A_step_that_failed_or_was_cancelled_is_tried_again_by_the_next_run()
    {
        using var directory = new TestDirectory();
        var a = new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") });
        var local = new LocalStorageBackend("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") });
        var store = new InMemoryStorageSyncStateStore();
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, StateStore = store, SyncId = "s", ItemRetries = 0 };
        await a.UploadBytesAsync("ok.txt", [1]);
        await a.UploadBytesAsync("blocked/f.txt", [2]);
        await a.SetTimestampsAsync("ok.txt", Old);
        await a.SetTimestampsAsync("blocked/f.txt", Old);
        var failing = true;
        var b = new HookedBackend(local, (_, _) => Task.CompletedTask, path =>
            failing && path.StartsWith("blocked/", StringComparison.Ordinal)
                ? CodeLogic.Core.Results.Result<StorageItem>.Failure(StorageErrors.PermissionDenied("no"))
                : (CodeLogic.Core.Results.Result<StorageItem>?)null);

        var first = (await a.SyncAsync("", b, "", options)).Value!;
        Assert.Single(first.Failed);
        Assert.DoesNotContain("blocked/f.txt", (await store.LoadAsync("s", default))!.Entries.Keys);

        // A run cancelled before it plans changes nothing and saves nothing.
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.SyncAsync("", b, "", options, cancelled.Token));
        }

        failing = false;
        var second = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Empty(second.Failed);
        Assert.Contains(second.Results, result => result.Action.RelativePath == "blocked/f.txt" && result.Outcome == StorageSyncActionOutcome.Applied);
        Assert.Equal([2], await File.ReadAllBytesAsync(Path.Combine(directory.Path, "b", "blocked", "f.txt")));
    }
}
