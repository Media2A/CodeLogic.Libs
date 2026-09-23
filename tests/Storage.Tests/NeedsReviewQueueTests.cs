using System.Collections.Concurrent;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Queue;
using CL.Storage.Registry;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Queue findings of the round-3 review (needs-review.md, sections A, B, C, and E).</summary>
public sealed class NeedsReviewQueueTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static StorageTransferJobRecord Job(string id, StorageTransferState state = StorageTransferState.Queued, StorageTransferPhase phase = StorageTransferPhase.NotStarted, string source = "a.bin") => new()
    {
        Id = id,
        Spec = new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = "Source", SourcePath = source, DestinationConnectionId = "Destination", DestinationPath = $"{id}.bin" },
        State = state,
        Checkpoint = new StorageTransferCheckpoint(phase),
        RetriesLeft = 3,
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    private static StorageItem File(string path, long size = 1) => new() { Path = path, Name = path, ItemType = StorageItemType.File, Size = size };

    /// <summary>A same-connection backend whose native copy waits until <paramref name="gate"/> opens (or it is cancelled, when it honours cancellation).</summary>
    private static FakeStorageBackend Gated(string id, TaskCompletionSource gate, Action? started = null, bool honourCancel = true, Func<Result>? result = null) => new(
        id,
        capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
        getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path))),
        copy: async (_, _, token) =>
        {
            started?.Invoke();
            if (honourCancel) await gate.Task.WaitAsync(token);
            else await gate.Task;
            return result?.Invoke() ?? Result.Success();
        });

    // ---------------------------------------------------------------- A4 (queue side)

    // needs-review A4
    [Fact]
    public async Task A_stored_resume_token_is_not_replayed_for_a_job_that_must_not_overwrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1, 2, 3]);
        await fixture.Destination.UploadBytesAsync("x.bin", [9]);
        var store = new InMemoryStorageTransferJobStore();
        var token = new StorageResumeToken { DestinationPath = "x.bin", StagingPath = ".cl-storage-part-x", BytesStaged = 0, SourcePath = "a.bin", SourceLength = 3 };
        await store.AddAsync(Job("x") with
        {
            Spec = Job("x").Spec with { TransferOptions = new StorageTransferOptions { Overwrite = false } },
            Checkpoint = new StorageTransferCheckpoint(StorageTransferPhase.NotStarted, token),
            RetriesLeft = 0
        }, default);

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var job = queue.Get("x")!;
        Assert.Equal((StorageTransferState.Failed, StorageErrors.ConflictCode), (job.State, job.Error?.Code));
        Assert.Equal([9], (await fixture.Destination.DownloadBytesAsync("x.bin")).Value!);
        Assert.True(StorageTransferQueue.ResumeAllowed(new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume, Overwrite = false }));
        Assert.False(StorageTransferQueue.ResumeAllowed(new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Fail }));
        Assert.False(StorageTransferQueue.ResumeAllowed(new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Rename }));
    }

    // ---------------------------------------------------------------- A15

    // needs-review A15
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancel_or_pause_after_a_move_committed_keeps_needs_reconciliation(bool pause)
    {
        await using var fixture = await Fixture.CreateAsync();
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sticky = new FakeStorageBackend(
            "Sticky",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path))),
            downloadWithOptions: (_, _, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1]))),
            delete: async (_, token) =>
            {
                deleting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Result.Success();
            });
        Assert.True(fixture.Library.RegisterBackend("Sticky", sticky).IsSuccess);
        await using var queue = await fixture.OpenAsync();

        var job = (await queue.EnqueueMoveAsync("Sticky", "a.bin", "Destination", "a.bin")).Value!;
        await deleting.Task.WaitAsync(Wait);
        var stop = pause ? await queue.PauseJobAsync(job.Id) : await queue.CancelAsync(job.Id);
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.NeedsReconciliation, queue.Get(job.Id)!.State);
        Assert.True(stop.IsFailure);
        Assert.True((await fixture.Destination.ExistsAsync("a.bin")).Value);
    }

    // ---------------------------------------------------------------- A16

    // needs-review A16
    [Fact]
    public async Task A_directory_copy_never_records_an_earlier_phase_after_committing()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var i = 0; i < 3; i++) await fixture.Source.UploadBytesAsync($"tree/f{i}.bin", [1, 2, 3]);
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueCopyAsync("Source", "tree", "Destination", "tree", jobId: "tree");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, queue.Get("tree")!.State);
        var phases = store.RunningPhases("tree");
        Assert.Contains(StorageTransferPhase.Committing, phases);
        for (var i = 1; i < phases.Count; i++)
            Assert.True(phases[i] >= phases[i - 1], $"Phase went back: {string.Join(", ", phases)}");
    }

    // needs-review A16
    [Fact]
    public async Task Upload_and_download_jobs_record_that_the_destination_may_be_written()
    {
        await using var fixture = await Fixture.CreateAsync();
        var local = Path.Combine(fixture.Directory.Path, "local.bin");
        await System.IO.File.WriteAllBytesAsync(local, [1, 2, 3]);
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueUploadAsync(local, "Destination", "up.bin", jobId: "up");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        await queue.EnqueueDownloadAsync("Destination", "up.bin", Path.Combine(fixture.Directory.Path, "down.bin"), jobId: "down");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Contains(StorageTransferPhase.Committing, store.RunningPhases("up"));
        Assert.Contains(StorageTransferPhase.Committing, store.RunningPhases("down"));
    }

    // ---------------------------------------------------------------- A17

    // needs-review A17
    [Fact]
    public async Task Removing_a_queued_job_as_it_would_start_raises_job_removed_once_and_it_never_runs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        var removed = 0;
        queue.JobRemoved += _ => Interlocked.Increment(ref removed);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterClaim = (_, _) => { claimed.TrySetResult(); return Task.CompletedTask; };
        store.BeforeRemove = async _ =>
        {
            queue.Resume();
            await Task.WhenAny(claimed.Task, Task.Delay(500));
        };

        Assert.True((await queue.RemoveAsync("x")).IsSuccess);
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref removed));
        Assert.Null(queue.Get("x"));
        Assert.False((await fixture.Destination.ExistsAsync("x.bin")).Value);
    }

    // needs-review A17
    [Fact]
    public async Task Removing_a_running_job_reports_a_store_failure_and_keeps_the_job()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Library.RegisterBackend("Slow", Gated("Slow", gate, () => started.TrySetResult())).IsSuccess);
        var store = new ScriptedStore { BeforeRemove = _ => throw new IOException("store down") };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        var removed = 0;
        queue.JobRemoved += _ => Interlocked.Increment(ref removed);

        await queue.EnqueueCopyAsync("Slow", "a.bin", "Slow", "b.bin", jobId: "x");
        await started.Task.WaitAsync(Wait);
        var remove = await queue.RemoveAsync("x");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.True(remove.IsFailure);
        Assert.Equal(0, Volatile.Read(ref removed));
        Assert.NotNull(queue.Get("x"));
        var stored = await store.Inner.GetAsync("x", default);
        Assert.NotEqual(StorageTransferState.Running, stored!.State);
    }

    // needs-review A17
    [Fact]
    public async Task Pruning_does_not_remove_a_job_another_worker_retried()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, MaxFinishedJobs = 1, AutomaticRetries = 0 });
        await queue.EnqueueCopyAsync("Source", "missing.bin", "Destination", "old.bin", jobId: "old");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        Assert.Equal(StorageTransferState.Failed, queue.Get("old")!.State);
        var once = 1;
        store.BeforeRemove = async id =>
        {
            if (id != "old" || Interlocked.Exchange(ref once, 0) == 0) return;
            // Another process retries the job just before this one prunes it, and starts running it.
            var current = (await store.Inner.GetAsync(id, default))!;
            var retried = (await store.Inner.SaveAsync(current with { State = StorageTransferState.Queued, FinishedAt = null }, null, false, default))!;
            Assert.NotNull(await store.Inner.TryClaimAsync(id, "other", retried.Revision, TimeSpan.FromMinutes(5), default));
        };

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "new.bin", jobId: "new");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var old = await store.Inner.GetAsync("old", default);
        Assert.NotNull(old);
        Assert.Equal(StorageTransferState.Queued, old.State);
    }

    // needs-review A17
    [Fact]
    public async Task Removing_a_job_another_process_changed_is_refused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        var current = (await store.Inner.GetAsync("x", default))!;
        await store.Inner.SaveAsync(current with { Priority = 9 }, null, false, default);

        var removed = await queue.RemoveAsync("x");

        Assert.Equal(StorageErrors.ConflictCode, removed.Error?.Code);
        Assert.NotNull(await store.Inner.GetAsync("x", default));
        Assert.Equal(9, queue.Get("x")!.Priority);
    }

    // ---------------------------------------------------------------- A18

    // needs-review A18
    [Fact]
    public async Task A_cancel_during_a_refused_claim_is_not_dropped()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 1;
        store.BeforeClaim = async (_, _) =>
        {
            if (Interlocked.Exchange(ref first, 0) == 0) return true;
            claiming.TrySetResult();
            await release.Task;
            return false;
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await claiming.Task.WaitAsync(Wait);
        var cancel = queue.CancelAsync("x");
        await Task.Delay(50);
        release.TrySetResult();
        var cancelled = await cancel.WaitAsync(Wait);
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.True(cancelled.IsSuccess, cancelled.Error?.ToString());
        Assert.Equal(StorageTransferState.Cancelled, queue.Get("x")!.State);
        Assert.False((await fixture.Destination.ExistsAsync("x.bin")).Value);
    }

    // needs-review A18
    [Fact]
    public async Task A_cancel_while_the_outcome_is_saved_stops_the_retry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var flaky = new FakeStorageBackend(
            "Flaky",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path))),
            copy: (_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Result.Failure(StorageErrors.ServerBusy("busy")) : Result.Success()));
        Assert.True(fixture.Library.RegisterBackend("Flaky", flaky).IsSuccess);
        var store = new ScriptedStore();
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = async (record, _) =>
        {
            if (record.State == StorageTransferState.Queued && record.NextAttemptAt is not null && saving.TrySetResult())
                await release.Task;
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, RetryBaseDelay = TimeSpan.Zero });

        await queue.EnqueueCopyAsync("Flaky", "a.bin", "Flaky", "b.bin", jobId: "x");
        await saving.Task.WaitAsync(Wait);
        var cancel = queue.CancelAsync("x");
        await Task.Delay(50);
        release.TrySetResult();
        var cancelled = await cancel.WaitAsync(Wait);
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.True(cancelled.IsSuccess, cancelled.Error?.ToString());
        Assert.Equal(StorageTransferState.Cancelled, queue.Get("x")!.State);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    // ---------------------------------------------------------------- B27

    // needs-review B27
    [Fact]
    public async Task A_queued_job_claimed_elsewhere_leaves_the_queue_idle_and_runs_when_the_lease_lapses()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        var added = (await store.AddAsync(Job("x"), default))!;
        Assert.NotNull(await store.TryClaimAsync("x", "other", added.Revision, TimeSpan.FromSeconds(1.5), default));

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(1));

        await Eventually(() => queue.Get("x")!.State == StorageTransferState.Completed, TimeSpan.FromSeconds(10));
    }

    // ---------------------------------------------------------------- B28

    // needs-review B28
    [Fact]
    public async Task A_claim_refused_on_an_unchanged_record_backs_off()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore { BeforeClaim = (_, _) => Task.FromResult(false) };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await Task.Delay(700);

        Assert.InRange(store.Claims, 1, 5);
    }

    // ---------------------------------------------------------------- B29 / B30

    // needs-review B29
    [Fact]
    public async Task A_failed_final_save_is_tried_again_while_the_lease_holds()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var failures = 2;
        var store = new ScriptedStore
        {
            BeforeSave = (record, _) => record.State == StorageTransferState.Completed && Interlocked.Decrement(ref failures) >= 0
                ? throw new IOException("store down")
                : Task.CompletedTask
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, queue.Get("x")!.State);
        Assert.Equal(StorageTransferState.Completed, (await store.Inner.GetAsync("x", default))!.State);
    }

    // needs-review B30
    [Fact]
    public async Task A_store_that_commits_and_then_throws_does_not_interrupt_the_job()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var once = 1;
        var store = new ScriptedStore
        {
            AfterSave = (record, _) => record.Checkpoint.Phase == StorageTransferPhase.Committing && record.State == StorageTransferState.Running &&
                                       Interlocked.Exchange(ref once, 0) == 1
                ? throw new IOException("connection reset after commit")
                : Task.CompletedTask
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, AutomaticRetries = 0 });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, queue.Get("x")!.State);
    }

    // ---------------------------------------------------------------- B31

    // needs-review B31
    [Fact]
    public async Task Finished_jobs_are_pruned_after_a_cancel_and_on_load()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        await using (var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true, MaxFinishedJobs = 1 }))
        {
            for (var i = 0; i < 3; i++) await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", $"{i}.bin", jobId: $"c{i}");
            for (var i = 0; i < 3; i++) Assert.True((await queue.CancelAsync($"c{i}")).IsSuccess);
            Assert.Single(queue.Jobs);
        }
        for (var i = 0; i < 3; i++)
            await store.AddAsync(Job($"done{i}", StorageTransferState.Completed) with { FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-i) }, default);

        await using var reopened = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true, MaxFinishedJobs = 1 });
        await Eventually(() => reopened.Jobs.Count == 1);
        Assert.Single(await store.LoadAsync(default));
    }

    // ---------------------------------------------------------------- B32

    // needs-review B32
    [Fact]
    public async Task Refresh_forgets_jobs_removed_by_another_process()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        var options = new StorageTransferQueueOptions { Store = store, StartPaused = true };
        await using var first = await fixture.OpenAsync(options with { WorkerId = "one" });
        await using var second = await fixture.OpenAsync(options with { WorkerId = "two" });
        await first.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await second.RefreshAsync();
        Assert.NotNull(second.Get("x"));
        var removed = new List<string>();
        second.JobRemoved += job => { lock (removed) removed.Add(job.Id); };

        Assert.True((await first.RemoveAsync("x")).IsSuccess);
        await second.RefreshAsync();

        Assert.Null(second.Get("x"));
        lock (removed) Assert.Equal(["x"], removed);
    }

    // needs-review B32
    [Fact]
    public async Task A_stale_refresh_does_not_bring_back_a_removed_job()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnLoad = async snapshot =>
        {
            loaded.TrySetResult();
            await release.Task;
            return snapshot;
        };

        var refresh = queue.RefreshAsync();
        await loaded.Task.WaitAsync(Wait);
        Assert.True((await queue.RemoveAsync("x")).IsSuccess);
        release.TrySetResult();
        await refresh.WaitAsync(Wait);

        Assert.Null(queue.Get("x"));
    }

    // ---------------------------------------------------------------- B33

    // needs-review B33
    [Fact]
    public async Task Enqueue_does_not_replace_a_newer_copy_adopted_meanwhile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        store.AfterAdd = async record =>
        {
            // While this enqueue waits for the store, a refresh picks up a newer revision of the job.
            await store.Inner.SaveAsync(record with { Priority = 7 }, null, false, default);
            await queue.RefreshAsync();
        };

        var added = await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");

        Assert.True(added.IsSuccess);
        Assert.Equal(2, queue.Get("x")!.Record.Revision);
        Assert.Equal(7, queue.Get("x")!.Priority);
    }

    // ---------------------------------------------------------------- B34

    // needs-review B34
    [Fact]
    public async Task A_cancelled_enqueue_that_the_store_committed_returns_the_job()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        var store = new ScriptedStore
        {
            AfterAdd = _ =>
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });

        var added = await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x", cancellationToken: cancel.Token);

        Assert.True(added.IsSuccess, added.Error?.ToString());
        Assert.NotNull(queue.Get("x"));
    }

    // needs-review B34
    [Fact]
    public async Task A_store_read_failure_on_enqueue_is_reported_as_unavailable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        store.RefuseAdd = true;
        var reads = 0;
        // The first read (does the job exist?) works; the read after the refused add fails.
        store.BeforeGet = _ => Interlocked.Increment(ref reads) > 1 ? throw new IOException("store down") : Task.CompletedTask;

        var added = await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");

        Assert.Equal(StorageErrors.UnavailableCode, added.Error?.Code);
    }

    // ---------------------------------------------------------------- B35

    // needs-review B35
    [Fact]
    public async Task A_throwing_event_context_does_not_break_the_queue()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { EventContext = new ThrowingContext(), MaxConcurrentTransfers = 1 });
        queue.JobChanged += _ => { };
        queue.ProgressChanged += _ => { };

        for (var i = 0; i < 3; i++) await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", $"{i}.bin", jobId: $"j{i}");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
    }

    // ---------------------------------------------------------------- B36

    // needs-review B36
    [Fact]
    public async Task Control_calls_after_dispose_do_not_write_to_the_store()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await queue.DisposeAsync();

        var cancelled = await queue.CancelAsync("x");
        var removed = await queue.RemoveAsync("x");

        Assert.True(cancelled.IsFailure);
        Assert.True(removed.IsFailure);
        Assert.Equal(StorageTransferState.Queued, (await store.GetAsync("x", default))!.State);
    }

    // needs-review B36
    [Fact]
    public async Task WaitForIdle_returns_when_the_queue_is_disposed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin");
        var idle = queue.WaitForIdleAsync();
        Assert.False(idle.IsCompleted);

        await queue.DisposeAsync();

        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // needs-review B36
    [Fact]
    public async Task An_attempt_that_outlives_the_shutdown_timeout_still_gives_its_job_back()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore
        {
            BeforeClaim = async (_, _) =>
            {
                claiming.TrySetResult();
                await release.Task;
                return true;
            }
        };
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await claiming.Task.WaitAsync(Wait);

        await queue.DisposeAsync().AsTask().WaitAsync(Wait);
        release.TrySetResult();

        await Eventually(() => store.Inner.GetAsync("x", default).Result is { LeaseOwner: null, State: StorageTransferState.Queued });
        Assert.False((await fixture.Destination.ExistsAsync("x.bin")).Value);
    }

    // ---------------------------------------------------------------- B37

    // needs-review B37
    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task An_invalid_refresh_interval_is_rejected(int seconds)
    {
        await using var fixture = await Fixture.CreateAsync();

        var opened = await fixture.Library.OpenTransferQueueAsync(new StorageTransferQueueOptions { StoreRefreshInterval = TimeSpan.FromSeconds(seconds) });

        Assert.Equal(StorageErrors.InvalidContentCode, opened.Error?.Code);
    }

    // ---------------------------------------------------------------- B38

    // needs-review B38
    [Fact]
    public async Task Orders_stay_unique_across_queues_sharing_a_store()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        var options = new StorageTransferQueueOptions { Store = store, StartPaused = true };
        await using var first = await fixture.OpenAsync(options with { WorkerId = "one" });
        await using var second = await fixture.OpenAsync(options with { WorkerId = "two" });
        await first.EnqueueCopyAsync("Source", "a.bin", "Destination", "1.bin", jobId: "one");
        await second.RefreshAsync();

        await second.EnqueueCopyAsync("Source", "a.bin", "Destination", "2.bin", jobId: "two");

        Assert.True(second.Get("two")!.Record.Order > second.Get("one")!.Record.Order);
    }

    // needs-review B38
    [Fact]
    public async Task Moving_a_job_among_equal_orders_changes_the_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        await store.AddAsync(Job("a") with { Order = 5 }, default);
        await store.AddAsync(Job("b") with { Order = 5 }, default);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        var before = queue.Jobs.Select(job => job.Id).ToList();

        Assert.True((await queue.MoveUpAsync(before[1])).IsSuccess);

        Assert.Equal([before[1], before[0]], queue.Jobs.Select(job => job.Id));
    }

    // ---------------------------------------------------------------- B39

    // needs-review B39
    [Fact]
    public async Task A_job_store_failure_does_not_use_up_retries()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        // More failures than one attempt tries the save, so the first attempt stops because of the store.
        var failures = 6;
        var store = new ScriptedStore
        {
            BeforeSave = (record, _) => record.State == StorageTransferState.Running && record.Checkpoint.Phase == StorageTransferPhase.Transferring &&
                                        Interlocked.Decrement(ref failures) >= 0
                ? throw new IOException("store down")
                : Task.CompletedTask
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, AutomaticRetries = 0, RetryBaseDelay = TimeSpan.Zero });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var job = queue.Get("x")!;
        Assert.Equal((StorageTransferState.Completed, 2), (job.State, job.Attempts));
    }

    // ---------------------------------------------------------------- B40

    // needs-review B40
    [Fact]
    public void Same_work_ignores_default_options_and_connection_id_case()
    {
        var bare = new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = "Src", SourcePath = "a", DestinationConnectionId = "Dst", DestinationPath = "b" };
        var explicitDefaults = bare with { SourceConnectionId = "src", DestinationConnectionId = "DST", TransferOptions = new StorageTransferOptions() };

        Assert.True(bare.SameWorkAs(explicitDefaults));
        Assert.False(bare.SameWorkAs(bare with { TransferOptions = new StorageTransferOptions { Overwrite = false } }));
    }

    // needs-review B40
    [Fact]
    public async Task Enqueue_rejects_connections_and_options_that_do_not_apply_to_the_kind()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });
        var upload = new StorageTransferJobSpec { Kind = StorageTransferKind.UploadFile, SourcePath = "local.bin", DestinationConnectionId = "Destination", DestinationPath = "x.bin" };

        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.EnqueueAsync(upload with { SourceConnectionId = "Source" })).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.EnqueueAsync(upload with { TransferOptions = new StorageTransferOptions() })).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.EnqueueAsync(upload with { DownloadConflictPolicy = StorageConflictPolicy.Skip })).Error?.Code);
        Assert.True((await queue.EnqueueAsync(upload)).IsSuccess);
    }

    // ---------------------------------------------------------------- B41

    // needs-review B41
    [Fact]
    public async Task Fencing_tokens_do_not_restart_when_a_job_id_is_added_again()
    {
        var store = new InMemoryStorageTransferJobStore();
        var first = (await store.AddAsync(Job("x"), default))!;
        var stale = (await store.TryClaimAsync("x", "old-worker", first.Revision, TimeSpan.FromMinutes(5), default))!;
        await store.SaveAsync(first, stale, releaseLease: true, default);
        await StoreRemove(store, "x");

        var again = (await store.AddAsync(Job("x"), default))!;
        var fresh = (await store.TryClaimAsync("x", "old-worker", again.Revision, TimeSpan.FromMinutes(5), default))!;

        Assert.True(fresh.FencingToken > stale.FencingToken);
    }

    // needs-review B41
    [Fact]
    public async Task Records_from_a_newer_schema_are_left_alone()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        await store.AddAsync(Job("future") with { SchemaVersion = StorageTransferJobRecord.CurrentSchemaVersion + 1 }, default);
        await store.AddAsync(Job("future-spec") with { Spec = Job("x").Spec with { SchemaVersion = StorageTransferJobSpec.CurrentSchemaVersion + 1 } }, default);

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Null(queue.Get("future"));
        Assert.Null(queue.Get("future-spec"));
        Assert.Equal(StorageTransferState.Queued, (await store.GetAsync("future", default))!.State);
        Assert.False((await fixture.Destination.ExistsAsync("future.bin")).Value);
    }

    // needs-review B41
    [Fact]
    public async Task A_lease_can_be_released_without_a_save()
    {
        var store = new InMemoryStorageTransferJobStore();
        var added = (await store.AddAsync(Job("x"), default))!;
        var lease = (await store.TryClaimAsync("x", "one", added.Revision, TimeSpan.FromMinutes(5), default))!;

        Assert.True(await store.ReleaseAsync(lease, default));

        var after = (await store.GetAsync("x", default))!;
        Assert.Equal((added.Revision, (string?)null), (after.Revision, after.LeaseOwner));
        Assert.NotNull(await store.TryClaimAsync("x", "two", added.Revision, TimeSpan.FromMinutes(5), default));
        Assert.False(await store.ReleaseAsync(lease, default));
    }

    // needs-review B41
    [Fact]
    public void Records_round_trip_as_json_and_newer_ones_are_refused()
    {
        var record = Job("x", StorageTransferState.Running, StorageTransferPhase.Committing) with
        {
            Revision = 4,
            FencingToken = 7,
            LeaseOwner = "w",
            LeaseExpiresAt = DateTimeOffset.UnixEpoch,
            Failure = new StorageTransferFailure("storage.unavailable", "down", "retryAfterMs=5"),
            Checkpoint = new StorageTransferCheckpoint(StorageTransferPhase.Committing, new StorageResumeToken { DestinationPath = "x.bin", StagingPath = ".cl-storage-part-x", BytesStaged = 10 })
        };

        var back = StorageTransferJobRecord.FromJson(record.ToJson());

        Assert.Equal(record.ToJson(), back.ToJson());
        Assert.Equal((4L, 7L, StorageTransferPhase.Committing, 10L), (back.Revision, back.FencingToken, back.Checkpoint.Phase, back.Checkpoint.ResumeToken!.BytesStaged));
        Assert.Throws<System.Text.Json.JsonException>(() => StorageTransferJobRecord.FromJson((record with { SchemaVersion = 99 }).ToJson()));
    }

    // ---------------------------------------------------------------- B42

    // needs-review B42
    [Fact]
    public async Task The_last_progress_of_a_copy_always_arrives_even_when_it_fails()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("big.bin", new byte[4_000_000]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { ProgressInterval = TimeSpan.FromMinutes(1), AutomaticRetries = 0 });
        StorageTransferJob? last = null;
        queue.ProgressChanged += job => Volatile.Write(ref last, job);

        // The content does not match the expected digest, so the copy fails after every byte was read.
        await queue.EnqueueCopyAsync("Source", "big.bin", "Destination", "big.bin", new StorageTransferOptions { ExpectedSha256 = new string('0', 64) }, jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Failed, queue.Get("x")!.State);

        Assert.Equal(4_000_000, Volatile.Read(ref last)?.Progress?.BytesTransferred);
    }

    // ---------------------------------------------------------------- B43

    // needs-review B43
    [Fact]
    public async Task Stopping_the_library_stops_its_queues_without_failing_running_jobs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Library.RegisterBackend("Slow", Gated("Slow", gate, () => started.TrySetResult())).IsSuccess);
        var store = new InMemoryStorageTransferJobStore();
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.EnqueueCopyAsync("Slow", "a.bin", "Slow", "b.bin", jobId: "x");
        await started.Task.WaitAsync(Wait);

        await fixture.Library.OnStopAsync().WaitAsync(Wait);

        var stored = (await store.GetAsync("x", default))!;
        Assert.Contains(stored.State, new[] { StorageTransferState.Interrupted, StorageTransferState.Queued });
        Assert.Null(stored.LeaseOwner);
        await queue.DisposeAsync();
    }

    // ---------------------------------------------------------------- C

    // needs-review C
    [Fact]
    public async Task Pausing_a_paused_job_succeeds_and_clear_accepts_null()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");

        Assert.True((await queue.PauseJobAsync("x")).IsSuccess);
        Assert.True((await queue.PauseJobAsync("x")).IsSuccess);
        Assert.Equal(0, await queue.ClearAsync(null!));
    }

    // needs-review C
    [Fact]
    public async Task A_job_recovered_as_interrupted_is_published_on_the_bus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        await store.AddAsync(Job("x", StorageTransferState.Running, StorageTransferPhase.Committing), default);
        var interrupted = new ConcurrentQueue<StorageTransferInterruptedEvent>();
        using var subscription = fixture.Events.Subscribe<StorageTransferInterruptedEvent>(interrupted.Enqueue);

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await Eventually(() => interrupted.Count == 1);
        Assert.Equal(("x", StorageTransferPhase.Committing), interrupted.Select(e => (e.JobId, e.Phase)).Single());
    }

    // ---------------------------------------------------------------- E (missing tests)

    // needs-review E
    [Fact]
    public async Task A_renewal_that_throws_once_does_not_stop_the_job()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Library.RegisterBackend("Slow", Gated("Slow", gate)).IsSuccess);
        var renewals = 0;
        var store = new ScriptedStore
        {
            BeforeRenew = _ => Interlocked.Increment(ref renewals) == 1 ? throw new IOException("store down") : Task.CompletedTask
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, LeaseDuration = TimeSpan.FromSeconds(1.5) });

        await queue.EnqueueCopyAsync("Slow", "a.bin", "Slow", "b.bin", jobId: "x");
        await Eventually(() => Volatile.Read(ref renewals) >= 3, TimeSpan.FromSeconds(10));
        gate.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var job = queue.Get("x")!;
        Assert.Equal((StorageTransferState.Completed, 1), (job.State, job.Attempts));
    }

    // needs-review E
    [Fact]
    public async Task A_renewal_that_keeps_throwing_stops_the_attempt_before_the_lease_lapses()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeStorageBackend(
            "Slow",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path))),
            copy: async (_, _, token) =>
            {
                try { await gate.Task.WaitAsync(token); }
                catch (OperationCanceledException) { stopped.TrySetResult(); throw; }
                return Result.Success();
            });
        Assert.True(fixture.Library.RegisterBackend("Slow", slow).IsSuccess);
        var store = new ScriptedStore { BeforeRenew = _ => throw new IOException("store down") };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, LeaseDuration = TimeSpan.FromSeconds(1.5) });

        await queue.EnqueueCopyAsync("Slow", "a.bin", "Slow", "b.bin", jobId: "x");

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.TrySetResult();
    }

    // needs-review E
    [Fact]
    public async Task Shutdown_during_a_successful_copy_keeps_the_result()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Library.RegisterBackend("Stubborn", Gated("Stubborn", gate, () => started.TrySetResult(), honourCancel: false)).IsSuccess);
        var store = new InMemoryStorageTransferJobStore();
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.EnqueueCopyAsync("Stubborn", "a.bin", "Stubborn", "b.bin", jobId: "x");
        await started.Task.WaitAsync(Wait);

        var disposing = queue.DisposeAsync().AsTask();
        await Task.Delay(50);
        gate.TrySetResult();
        await disposing.WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Completed, (await store.GetAsync("x", default))!.State);
    }

    // needs-review E
    [Fact]
    public async Task A_worker_whose_lease_was_taken_over_records_nothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeStorageBackend(
            "Slow",
            capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path))),
            copy: async (_, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { stopped.TrySetResult(); throw; }
                return Result.Success();
            });
        Assert.True(fixture.Library.RegisterBackend("Slow", slow).IsSuccess);
        var time = new ShiftedTime();
        var store = new InMemoryStorageTransferJobStore(time);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, LeaseDuration = TimeSpan.FromSeconds(1.5) });
        await queue.EnqueueCopyAsync("Slow", "a.bin", "Slow", "b.bin", jobId: "x");
        await started.Task.WaitAsync(Wait);

        // The store's clock says the lease lapsed; another worker takes the job over.
        time.Shift = TimeSpan.FromMinutes(10);
        var current = (await store.GetAsync("x", default))!;
        var foreign = await store.TryClaimAsync("x", "other", current.Revision, TimeSpan.FromHours(1), default);
        Assert.NotNull(foreign);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var after = (await store.GetAsync("x", default))!;
        Assert.Equal((current.Revision, "other", StorageTransferState.Running), (after.Revision, after.LeaseOwner, after.State));
    }

    // needs-review E
    [Fact]
    public async Task Without_requeue_when_safe_an_untouched_job_left_running_is_interrupted()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new InMemoryStorageTransferJobStore();
        await store.AddAsync(Job("x", StorageTransferState.Running, StorageTransferPhase.Transferring), default);

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, RequeueInterruptedWhenSafe = false });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageTransferState.Interrupted, queue.Get("x")!.State);
        Assert.False((await fixture.Destination.ExistsAsync("x.bin")).Value);
    }

    // needs-review E
    [Fact]
    public async Task Directory_jobs_run_through_the_queue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var local = fixture.Directory.CreateDirectory("local-tree");
        System.IO.File.WriteAllBytes(Path.Combine(local, "a.bin"), [1]);
        Directory.CreateDirectory(Path.Combine(local, "sub"));
        System.IO.File.WriteAllBytes(Path.Combine(local, "sub", "b.bin"), [2]);
        await using var queue = await fixture.OpenAsync();

        await queue.EnqueueUploadDirectoryAsync(local, "Destination", "up", jobId: "up");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        var down = Path.Combine(fixture.Directory.Path, "down-tree");
        await queue.EnqueueDownloadDirectoryAsync("Destination", "up", down, jobId: "down");
        await queue.EnqueueCopyAsync("Destination", "up", "Source", "copied", jobId: "copy");
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.All(queue.Jobs, job => Assert.Equal(StorageTransferState.Completed, job.State));
        Assert.True((await fixture.Destination.ExistsAsync("up/sub/b.bin")).Value);
        Assert.True(System.IO.File.Exists(Path.Combine(down, "sub", "b.bin")));
        Assert.True((await fixture.Source.ExistsAsync("copied/sub/b.bin")).Value);
    }

    // needs-review E
    [Fact]
    public async Task Queue_outcomes_are_published_on_the_bus()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var events = new ConcurrentQueue<IEvent>();
        using var s1 = fixture.Events.Subscribe<StorageTransferStartedEvent>(events.Enqueue);
        using var s2 = fixture.Events.Subscribe<StorageTransferCompletedEvent>(events.Enqueue);
        using var s3 = fixture.Events.Subscribe<StorageTransferFailedEvent>(events.Enqueue);
        using var s4 = fixture.Events.Subscribe<StorageTransferCancelledEvent>(events.Enqueue);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { AutomaticRetries = 0 });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "ok.bin", jobId: "ok");
        await queue.EnqueueCopyAsync("Source", "missing.bin", "Destination", "bad.bin", jobId: "bad");
        await queue.WaitForIdleAsync().WaitAsync(Wait);
        queue.Pause();
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "never.bin", jobId: "never");
        await queue.CancelAsync("never");

        await Eventually(() => events.Count >= 5);
        Assert.Equal(2, events.OfType<StorageTransferStartedEvent>().Count());
        Assert.Equal("ok", Assert.Single(events.OfType<StorageTransferCompletedEvent>()).JobId);
        Assert.Equal((StorageErrors.NotFoundCode, "bad"), events.OfType<StorageTransferFailedEvent>().Select(e => (e.ErrorCode, e.JobId)).Single());
        Assert.Equal("never", Assert.Single(events.OfType<StorageTransferCancelledEvent>()).JobId);
    }

    private static async Task StoreRemove(IStorageTransferJobStore store, string id) =>
        Assert.True(await store.RemoveAsync(id, (await store.GetAsync(id, default))!.Revision, null, default));

    private static async Task Eventually(Func<bool> condition, TimeSpan? within = null)
    {
        var until = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class ThrowingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => throw new InvalidOperationException("The UI has shut down.");
    }

    private sealed class ShiftedTime : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    /// <summary>An in-memory store with hooks around every call.</summary>
    private sealed class ScriptedStore : IStorageTransferJobStore
    {
        private int _claims;
        public InMemoryStorageTransferJobStore Inner { get; } = new();
        public ConcurrentQueue<StorageTransferJobRecord> Saved { get; } = new();
        public int Claims => Volatile.Read(ref _claims);
        public bool RefuseAdd { get; set; }
        public Func<IReadOnlyList<StorageTransferJobRecord>, Task<IReadOnlyList<StorageTransferJobRecord>>>? OnLoad { get; set; }
        public Func<StorageTransferJobRecord, Task>? AfterAdd { get; set; }
        public Func<string, Task>? BeforeGet { get; set; }
        public Func<string, string, Task<bool>>? BeforeClaim { get; set; }
        public Func<string, StorageTransferLease?, Task>? AfterClaim { get; set; }
        public Func<StorageTransferLease, Task>? BeforeRenew { get; set; }
        public Func<StorageTransferJobRecord, StorageTransferLease?, Task>? BeforeSave { get; set; }
        public Func<StorageTransferJobRecord, StorageTransferJobRecord?, Task>? AfterSave { get; set; }
        public Func<string, Task>? BeforeRemove { get; set; }

        public List<StorageTransferPhase> RunningPhases(string id) =>
            [.. Saved.Where(record => record.Id == id && record.State == StorageTransferState.Running).Select(record => record.Checkpoint.Phase)];

        public async Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken)
        {
            var records = await Inner.LoadAsync(cancellationToken);
            return OnLoad is null ? records : await OnLoad(records);
        }

        public async Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken)
        {
            if (RefuseAdd) return null;
            var added = await Inner.AddAsync(record, cancellationToken);
            if (added is not null && AfterAdd is not null) await AfterAdd(added);
            return added;
        }

        public async Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
        {
            if (BeforeGet is not null) await BeforeGet(jobId);
            return await Inner.GetAsync(jobId, cancellationToken);
        }

        public async Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _claims);
            if (BeforeClaim is not null && !await BeforeClaim(jobId, workerId)) return null;
            var lease = await Inner.TryClaimAsync(jobId, workerId, expectedRevision, duration, cancellationToken);
            if (AfterClaim is not null) await AfterClaim(jobId, lease);
            return lease;
        }

        public async Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (BeforeRenew is not null) await BeforeRenew(lease);
            return await Inner.RenewAsync(lease, duration, cancellationToken);
        }

        public async Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken)
        {
            if (BeforeSave is not null) await BeforeSave(record, lease);
            var stored = await Inner.SaveAsync(record, lease, releaseLease, cancellationToken);
            if (stored is not null) Saved.Enqueue(stored);
            if (AfterSave is not null) await AfterSave(record, stored);
            return stored;
        }

        public Task<bool> ReleaseAsync(StorageTransferLease lease, CancellationToken cancellationToken) => Inner.ReleaseAsync(lease, cancellationToken);

        public async Task<bool> RemoveAsync(string jobId, long expectedRevision, StorageTransferLease? lease, CancellationToken cancellationToken)
        {
            if (BeforeRemove is not null) await BeforeRemove(jobId);
            return await Inner.RemoveAsync(jobId, expectedRevision, lease, cancellationToken);
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
