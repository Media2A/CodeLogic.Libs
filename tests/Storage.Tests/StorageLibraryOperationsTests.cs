using System.Collections.Concurrent;
using System.Diagnostics;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace Storage.Tests;

public sealed class StorageLibraryOperationsTests
{
    [Fact]
    public async Task Stable_proxy_drains_an_active_common_operation_before_owned_replacement_disposal()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldBackend = new FakeStorageBackend(
            "Live",
            root: "/old",
            upload: async (path, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return Result<StorageItem>.Success(Item(path));
            });
        var replacement = new FakeStorageBackend("Live", root: "/new");
        Assert.True(library.RegisterBackend("Live", oldBackend).IsSuccess);
        var stableProxy = library.GetStorage("live");
        var upload = stableProxy.UploadBytesAsync("active.bin", [1]);
        await entered.Task;

        var swap = Task.Run(() => library.RegisterBackend("LIVE", replacement));
        await Task.Delay(50);

        Assert.False(swap.IsCompleted);
        Assert.Equal(0, oldBackend.DisposeCount);
        release.TrySetResult();
        Assert.True((await upload).IsSuccess);
        Assert.True((await swap).IsSuccess);
        Assert.Equal(1, oldBackend.DisposeCount);
        Assert.Same(stableProxy, library.GetStorage("Live"));
        Assert.Equal("/new", stableProxy.Root);
    }

    [Fact]
    public async Task Concurrent_stop_callers_share_the_same_operation_drain()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeStorageBackend(
            "Stopping",
            upload: async (path, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return Result<StorageItem>.Success(Item(path));
            });
        Assert.True(library.RegisterBackend("Stopping", backend).IsSuccess);
        var upload = library.GetStorage("Stopping").UploadBytesAsync("active.bin", [1]);
        await entered.Task;

        var firstStop = library.OnStopAsync();
        var secondStop = library.OnStopAsync();
        await Task.Delay(50);

        Assert.False(firstStop.IsCompleted);
        Assert.False(secondStop.IsCompleted);
        Assert.Equal(0, backend.DisposeCount);
        release.TrySetResult();
        Assert.True((await upload).IsSuccess);
        await Task.WhenAll(firstStop, secondStop);
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public async Task Concurrent_lookup_and_swap_never_exposes_a_disposed_backend()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        Assert.True(library.RegisterBackend("Concurrent", new FakeStorageBackend("Concurrent")).IsSuccess);
        var failures = new ConcurrentQueue<Exception>();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var index = 0; index < 100; index++)
            {
                try { Assert.True((await library.GetStorage("concurrent").ExistsAsync("item")).IsSuccess); }
                catch (Exception error) { failures.Enqueue(error); }
            }
        }));
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 40; index++)
                Assert.True(library.RegisterBackend("CONCURRENT", new FakeStorageBackend("Concurrent", root: $"/{index}")).IsSuccess);
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Successful_mutations_publish_typed_generic_events_but_failures_do_not()
    {
        using var directory = new TestDirectory();
        var eventBus = new EventBus();
        var published = new List<IEvent>();
        using var written = eventBus.Subscribe<StorageItemWrittenEvent>(published.Add);
        using var deleted = eventBus.Subscribe<StorageItemDeletedEvent>(published.Add);
        using var copied = eventBus.Subscribe<StorageItemCopiedEvent>(published.Add);
        using var moved = eventBus.Subscribe<StorageItemMovedEvent>(published.Add);
        var context = StorageLibraryTestSupport.CreateContext(directory.Path, eventBus);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var backend = new FakeStorageBackend(
            "Events",
            provider: StorageProvider.S3,
            upload: (path, _) => Task.FromResult(path == "failed.bin"
                ? Result<StorageItem>.Failure(StorageErrors.Unavailable("offline"))
                : Result<StorageItem>.Success(Item(path))));
        Assert.True(library.RegisterBackend("Events", backend).IsSuccess);
        var service = library.GetStorage("Events");

        Assert.True((await service.UploadBytesAsync("written.bin", [1])).IsSuccess);
        Assert.True((await service.DeleteAsync("deleted.bin")).IsSuccess);
        Assert.True((await service.CopyAsync("source.bin", "copy.bin")).IsSuccess);
        Assert.True((await service.MoveAsync("old.bin", "new.bin")).IsSuccess);
        Assert.True((await service.UploadBytesAsync("failed.bin", [1])).IsFailure);

        Assert.Collection(
            published,
            item => Assert.Equal("written.bin", Assert.IsType<StorageItemWrittenEvent>(item).Path),
            item => Assert.Equal("deleted.bin", Assert.IsType<StorageItemDeletedEvent>(item).Path),
            item => Assert.Equal(("source.bin", "copy.bin"),
                (Assert.IsType<StorageItemCopiedEvent>(item).SourcePath, Assert.IsType<StorageItemCopiedEvent>(item).DestinationPath)),
            item => Assert.Equal(("old.bin", "new.bin"),
                (Assert.IsType<StorageItemMovedEvent>(item).SourcePath, Assert.IsType<StorageItemMovedEvent>(item).DestinationPath)));
        Assert.All(published, item =>
        {
            var timestamp = item switch
            {
                StorageItemWrittenEvent value => value.Timestamp,
                StorageItemDeletedEvent value => value.Timestamp,
                StorageItemCopiedEvent value => value.Timestamp,
                StorageItemMovedEvent value => value.Timestamp,
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        });
    }

    [Fact]
    public async Task Event_publication_failure_is_logged_without_changing_successful_result()
    {
        using var directory = new TestDirectory();
        var eventBus = new ThrowingEventBus();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path, eventBus);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        Assert.True(library.RegisterBackend("Events", new FakeStorageBackend("Events")).IsSuccess);

        var result = await library.GetStorage("Events").UploadBytesAsync("saved.bin", [1]);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(eventBus.Published);
        Assert.Contains(((TestLogger)context.Logger).Errors, message => message.Contains("event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Proxy_propagates_cancellation_and_never_disposes_the_caller_upload_stream()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var backend = new FakeStorageBackend(
            "Cancellation",
            upload: (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Result<StorageItem>.Success(Item("never")));
            });
        Assert.True(library.RegisterBackend("Cancellation", backend).IsSuccess);
        using var source = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            library.GetStorage("Cancellation").UploadAsync("item.bin", source, cancellationToken: cancellation.Token));

        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Health_probes_run_concurrently_and_timeout_without_hanging()
    {
        using var directory = new TestDirectory();
        var defaultRoot = directory.CreateDirectory("default");
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            storage => storage.HealthCheckTimeoutSeconds = 1,
            local => local.Connections["Default"] = new() { RootPath = defaultRoot });
        var slowOne = new FakeStorageBackend("SlowOne");
        var slowTwo = new FakeStorageBackend("SlowTwo");
        Assert.True(library.RegisterBackend("SlowOne", slowOne).IsSuccess);
        Assert.True(library.RegisterBackend("SlowTwo", slowTwo).IsSuccess);
        slowOne.SetHealth(_ => new TaskCompletionSource<Result>().Task);
        slowTwo.SetHealth(_ => new TaskCompletionSource<Result>().Task);
        var stopwatch = Stopwatch.StartNew();

        var health = await library.HealthCheckAsync();

        stopwatch.Stop();
        Assert.Equal(HealthStatusLevel.Degraded, health.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1800), $"Health check took {stopwatch.Elapsed}.");
        Assert.Contains("SlowOne", health.Message, StringComparison.Ordinal);
        Assert.Contains("SlowTwo", health.Message, StringComparison.Ordinal);
        var failed = Assert.IsType<Dictionary<string, object>>(health.Data!["failedConnections"]);
        Assert.Equal("storage.timeout", failed["SlowOne"]);
        Assert.Equal("storage.timeout", failed["SlowTwo"]);
    }

    [Fact]
    public async Task Health_is_unhealthy_when_all_effective_connections_fail_or_default_is_removed()
    {
        using var directory = new TestDirectory();
        var defaultRoot = directory.CreateDirectory("default");
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = defaultRoot });
        var failedDefault = new FakeStorageBackend("Default");
        Assert.True(library.RegisterBackend("Default", failedDefault).IsSuccess);
        failedDefault.SetHealth(_ => Task.FromResult(Result.Failure(StorageErrors.Unavailable("offline"))));

        Assert.Equal(HealthStatusLevel.Unhealthy, (await library.HealthCheckAsync()).Status);
        Assert.True((await library.RemoveConnectionAsync("Default", persist: false)).IsSuccess);
        var missingDefault = await library.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Unhealthy, missingDefault.Status);
        Assert.Contains("Default", missingDefault.Message, StringComparison.Ordinal);
    }

    private static StorageItem Item(string path) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        ItemType = StorageItemType.File
    };
}
