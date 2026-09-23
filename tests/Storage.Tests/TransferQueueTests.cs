using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Queue;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers the background transfer queue: limits, priorities, cancellation, and retries.</summary>
public sealed class TransferQueueTests
{
    [Fact]
    public async Task Queued_copies_all_complete_and_report_state_changes()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 6; i++) await fixture.Source.UploadBytesAsync($"f{i}.bin", [1, 2, 3]);
        await using var queue = fixture.Library.CreateTransferQueue();
        var changes = new List<StorageTransferJob>();
        queue.JobChanged += job => { lock (changes) changes.Add(job); };

        for (var i = 0; i < 6; i++) queue.EnqueueCopy("Source", $"f{i}.bin", "Destination", $"copy/f{i}.bin");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
        Assert.All(Enumerable.Range(0, 6), i => Assert.True(fixture.Destination.ExistsAsync($"copy/f{i}.bin").Result.Value));
        lock (changes) Assert.Contains(changes, job => job.State == StorageTransferState.Running);
    }

    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_limits()
    {
        await using var fixture = await Fixture.CreateAsync();
        StorageTransferPipeline.SetLimits(fixture.Destination, uploadBytesPerSecond: 400_000, downloadBytesPerSecond: null);
        for (var i = 0; i < 6; i++) await fixture.Source.UploadBytesAsync($"f{i}.bin", new byte[60_000]);
        await using var queue = fixture.Library.CreateTransferQueue(new StorageTransferQueueOptions { MaxConcurrentTransfers = 4, MaxTransfersPerConnection = 2 });
        var peak = 0;
        queue.JobChanged += _ => { lock (queue) peak = Math.Max(peak, queue.Jobs.Count(job => job.State == StorageTransferState.Running)); };

        for (var i = 0; i < 6; i++) queue.EnqueueCopy("Source", $"f{i}.bin", "Destination", $"copy/f{i}.bin");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.InRange(peak, 1, 2);
        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
    }

    [Fact]
    public async Task High_priority_jobs_start_first()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = fixture.Library.CreateTransferQueue(new StorageTransferQueueOptions { MaxConcurrentTransfers = 1, StartPaused = true });
        var starts = new List<string>();
        queue.JobChanged += job => { if (job.State == StorageTransferState.Running) lock (starts) starts.Add(job.Destination); };

        queue.EnqueueCopy("Source", "a.bin", "Destination", "low.bin", priority: StorageTransferPriority.Low);
        queue.EnqueueCopy("Source", "a.bin", "Destination", "normal.bin");
        queue.EnqueueCopy("Source", "a.bin", "Destination", "high.bin", priority: StorageTransferPriority.High);
        Assert.True(queue.IsPaused);
        queue.Resume();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["Destination:high.bin", "Destination:normal.bin", "Destination:low.bin"], starts);
    }

    [Fact]
    public async Task Cancelled_queued_job_never_runs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = fixture.Library.CreateTransferQueue(new StorageTransferQueueOptions { StartPaused = true });

        var job = queue.EnqueueCopy("Source", "a.bin", "Destination", "never.bin");
        Assert.True(queue.Cancel(job.Id));
        queue.Resume();
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StorageTransferState.Cancelled, Assert.Single(queue.Jobs).State);
        Assert.False((await fixture.Destination.ExistsAsync("never.bin")).Value);
        Assert.False(queue.Cancel(job.Id));
    }

    [Fact]
    public async Task Permanent_failures_go_to_the_failed_list_and_can_be_retried()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = fixture.Library.CreateTransferQueue();

        queue.EnqueueCopy("Source", "missing.bin", "Destination", "copy.bin");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var failed = Assert.Single(queue.FailedJobs);
        Assert.Equal(StorageErrors.NotFoundCode, failed.Error?.Code);
        Assert.Equal(1, failed.Attempts);

        await fixture.Source.UploadBytesAsync("missing.bin", [7]);
        Assert.Equal(1, queue.RetryFailed());
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StorageTransferState.Completed, Assert.Single(queue.Jobs).State);
        Assert.Equal(1, queue.ClearFinished());
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Transient_failures_are_requeued_automatically()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var calls = 0;
        var fake = new FakeStorageBackend(
            "Flaky",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
            copy: (_, _, _) => Task.FromResult(++calls == 1
                ? Result.Failure(StorageErrors.ConnectionLost("dropped"))
                : Result.Success()));
        Assert.True(library.RegisterBackend("Flaky", fake).IsSuccess);
        await using var queue = library.CreateTransferQueue(new StorageTransferQueueOptions { RetryDelay = TimeSpan.FromMilliseconds(20) });

        queue.EnqueueCopy("Flaky", "a.bin", "Flaky", "b.bin");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var job = Assert.Single(queue.Jobs);
        Assert.Equal(StorageTransferState.Completed, job.State);
        Assert.Equal(2, job.Attempts);
    }

    [Fact]
    public async Task Upload_and_download_jobs_report_progress()
    {
        await using var fixture = await Fixture.CreateAsync();
        var local = Path.Combine(fixture.Directory.Path, "local.bin");
        await File.WriteAllBytesAsync(local, new byte[200_000]);
        await using var queue = fixture.Library.CreateTransferQueue();
        var progress = 0;
        queue.ProgressChanged += _ => Interlocked.Increment(ref progress);

        queue.EnqueueUpload(local, "Destination", "up.bin");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        queue.EnqueueDownload("Destination", "up.bin", Path.Combine(fixture.Directory.Path, "down.bin"));
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
        Assert.Equal(200_000, new FileInfo(Path.Combine(fixture.Directory.Path, "down.bin")).Length);
        Assert.True(progress > 0);
        Assert.All(queue.Jobs, job => Assert.True(job.Progress?.IsCompleted));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDirectory Directory { get; init; }
        public required global::CL.Storage.StorageLibrary Library { get; init; }
        public required LocalStorageBackend Source { get; init; }
        public required LocalStorageBackend Destination { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = new TestDirectory();
            var context = StorageLibraryTestSupport.CreateContext(directory.Path);
            var library = new global::CL.Storage.StorageLibrary();
            await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
            var source = new LocalStorageBackend("Source", new LocalConnectionConfig { RootPath = directory.CreateDirectory("src") });
            var destination = new LocalStorageBackend("Destination", new LocalConnectionConfig { RootPath = directory.CreateDirectory("dst") });
            Assert.True(library.RegisterBackend("Source", source).IsSuccess);
            Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
            return new Fixture { Directory = directory, Library = library, Source = source, Destination = destination };
        }

        public ValueTask DisposeAsync()
        {
            Library.Dispose();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
