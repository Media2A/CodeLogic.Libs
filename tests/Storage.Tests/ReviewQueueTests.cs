using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Providers.Local;
using CL.Storage.Queue;
using CodeLogic.Core.Events;
using Xunit;

namespace Storage.Tests;

/// <summary>Queue defects found in review: restarts, stale copies, shared stores, and failing stores.</summary>
public sealed class ReviewQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static StorageTransferJobRecord Job(string id, StorageTransferState state = StorageTransferState.Queued, StorageTransferPhase phase = StorageTransferPhase.NotStarted) => new()
    {
        Id = id,
        Spec = new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = "Source", SourcePath = "a.bin", DestinationConnectionId = "Destination", DestinationPath = $"{id}.bin" },
        State = state,
        Checkpoint = new StorageTransferCheckpoint(phase),
        RetriesLeft = 3,
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Restarting_with_the_same_worker_id_takes_back_its_own_live_lease()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        var added = (await store.AddAsync(Job("mine", StorageTransferState.Running, StorageTransferPhase.Transferring), default))!;
        // The previous process of this worker crashed while holding a lease that has not expired yet.
        Assert.NotNull(await store.TryClaimAsync("mine", "worker-1", added.Revision, TimeSpan.FromMinutes(10), default));

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, WorkerId = "worker-1" });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, queue.Get("mine")!.State);
        Assert.True((await fixture.Destination.ExistsAsync("mine.bin")).Value);
    }

    [Fact]
    public async Task A_job_finished_by_another_process_is_not_run_again_and_stale_changes_reload()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "done.bin", jobId: "done");
        // Another process completes the job while this queue still holds its queued copy.
        var stored = (await store.GetAsync("done", default))!;
        Assert.NotNull(await store.SaveAsync(stored with { State = StorageTransferState.Completed, FinishedAt = DateTimeOffset.UtcNow }, null, false, default));

        var reprioritized = await queue.SetPriorityAsync("done", 5);
        Assert.Equal(StorageErrors.ConflictCode, reprioritized.Error?.Code);
        Assert.Equal(StorageTransferState.Completed, queue.Get("done")!.State);

        queue.Resume();
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.False((await fixture.Destination.ExistsAsync("done.bin")).Value);
        Assert.Equal(StorageTransferState.Completed, (await store.GetAsync("done", default))!.State);
    }

    [Fact]
    public async Task Two_queues_on_one_store_run_each_job_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1, 2, 3]);
        var store = new InMemoryStorageTransferJobStore();
        var started = new List<string>();
        using var subscription = fixture.Events.Subscribe<StorageTransferStartedEvent>(value => { lock (started) started.Add(value.JobId); });
        var options = new StorageTransferQueueOptions { Store = store, StartPaused = true, MaxConcurrentTransfers = 4, MaxTransfersPerConnection = 4 };
        await using var first = await fixture.OpenAsync(options with { WorkerId = "one" });
        await using var second = await fixture.OpenAsync(options with { WorkerId = "two" });
        for (var i = 0; i < 12; i++)
            await first.EnqueueCopyAsync("Source", "a.bin", "Destination", $"copy/{i}.bin", jobId: $"job-{i}");
        await second.RefreshAsync();

        first.Resume();
        second.Resume();
        await first.WaitForIdleAsync().WaitAsync(Wait);
        await second.WaitForIdleAsync().WaitAsync(Wait);

        var records = await store.LoadAsync(default);
        Assert.All(records, record => Assert.Equal(StorageTransferState.Completed, record.State));
        lock (started) Assert.Equal(12, started.Distinct().Count());
        lock (started) Assert.Equal(12, started.Count);
    }

    [Fact]
    public async Task A_store_that_throws_does_not_wedge_the_queue()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new FlakyStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, RetryBaseDelay = TimeSpan.Zero, LeaseDuration = TimeSpan.FromSeconds(2) });

        store.FailNextPhaseSave = true;   // recording the transfer phase fails once: the attempt stops and is retried
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "phase.bin", jobId: "phase");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.Equal(StorageTransferState.Completed, queue.Get("phase")!.State);

        store.FailNextClaim = true;       // claiming fails once: the job waits a moment, then runs
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "claim.bin", jobId: "claim");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.Equal(StorageTransferState.Completed, queue.Get("claim")!.State);

        store.FailNextFinalSave = true;   // the outcome cannot be recorded: the job is recovered, not lost
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "final.bin", jobId: "final");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.Equal(StorageTransferState.Interrupted, queue.Get("final")!.State);
        Assert.True((await fixture.Destination.ExistsAsync("final.bin")).Value);
    }

    [Fact]
    public async Task Removing_a_job_raises_job_removed_not_a_cancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });
        var changed = new List<StorageTransferJob>();
        var removed = new List<StorageTransferJob>();
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        queue.JobChanged += job => { lock (changed) changed.Add(job); };
        queue.JobRemoved += job => { lock (removed) removed.Add(job); };

        Assert.True((await queue.RemoveAsync("x")).IsSuccess);

        Assert.Empty(changed);
        Assert.Equal("x", Assert.Single(removed).Id);
        Assert.Null(queue.Get("x"));
    }

    [Fact]
    public async Task A_manual_retry_resets_attempts_and_priorities_change_only_while_waiting()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { AutomaticRetries = 0 });
        await queue.EnqueueCopyAsync("Source", "missing.bin", "Destination", "x.bin", jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.Equal(StorageTransferState.Failed, queue.Get("x")!.State);
        Assert.Equal(StorageErrors.ConflictCode, (await queue.SetPriorityAsync("x", 3)).Error?.Code);

        queue.Pause();
        Assert.True((await queue.RetryAsync("x")).IsSuccess);

        Assert.Equal(0, queue.Get("x")!.Attempts);
        Assert.True((await queue.SetPriorityAsync("x", 3)).IsSuccess);
    }

    [Fact]
    public void A_spec_from_a_newer_schema_is_refused()
    {
        var json = new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = "a", SourcePath = "x", DestinationConnectionId = "b", DestinationPath = "y" }
            .ToJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal);

        Assert.Throws<System.Text.Json.JsonException>(() => StorageTransferJobSpec.FromJson(json));
    }

    /// <summary>An in-memory store that fails chosen calls once.</summary>
    private sealed class FlakyStore : IStorageTransferJobStore
    {
        private readonly InMemoryStorageTransferJobStore _inner = new();
        public volatile bool FailNextClaim;
        public volatile bool FailNextPhaseSave;
        public volatile bool FailNextFinalSave;

        public Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);
        public Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken) => _inner.AddAsync(record, cancellationToken);
        public Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken) => _inner.GetAsync(jobId, cancellationToken);
        public Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken) => _inner.RenewAsync(lease, duration, cancellationToken);
        public Task RemoveAsync(string jobId, CancellationToken cancellationToken) => _inner.RemoveAsync(jobId, cancellationToken);

        public Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (FailNextClaim) { FailNextClaim = false; throw new IOException("store down"); }
            return _inner.TryClaimAsync(jobId, workerId, expectedRevision, duration, cancellationToken);
        }

        public Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken)
        {
            if (FailNextPhaseSave && record.State == StorageTransferState.Running && record.Checkpoint.Phase == StorageTransferPhase.Transferring)
            { FailNextPhaseSave = false; throw new IOException("store down"); }
            if (FailNextFinalSave && record.State == StorageTransferState.Completed)
            { FailNextFinalSave = false; throw new IOException("store down"); }
            return _inner.SaveAsync(record, lease, releaseLease, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required TestDirectory Directory { get; init; }
        public required global::CL.Storage.StorageLibrary Library { get; init; }
        public required LocalStorageBackend Source { get; init; }
        public required LocalStorageBackend Destination { get; init; }
        public required EventBus Events { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = new TestDirectory();
            var events = new EventBus();
            var context = StorageLibraryTestSupport.CreateContext(directory.Path, events);
            var library = new global::CL.Storage.StorageLibrary();
            await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
            var source = new LocalStorageBackend("Source", new LocalConnectionConfig { RootPath = directory.CreateDirectory("src") });
            var destination = new LocalStorageBackend("Destination", new LocalConnectionConfig { RootPath = directory.CreateDirectory("dst") });
            Assert.True(library.RegisterBackend("Source", source).IsSuccess);
            Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
            return new Fixture { Directory = directory, Library = library, Source = source, Destination = destination, Events = events };
        }

        public async Task<StorageTransferQueue> OpenAsync(StorageTransferQueueOptions? options = null)
        {
            var opened = await Library.OpenTransferQueueAsync(options);
            Assert.True(opened.IsSuccess, opened.Error?.ToString());
            return opened.Value!;
        }

        public ValueTask DisposeAsync()
        {
            Library.Dispose();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
