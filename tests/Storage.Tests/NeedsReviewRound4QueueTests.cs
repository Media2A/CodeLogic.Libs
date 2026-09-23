using System.Collections.Concurrent;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Queue;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Queue findings of the round-4 review (needs-review.md, R4-B9 to R4-B16).</summary>
public sealed class NeedsReviewRound4QueueTests
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

    private static StorageItem File(string path, long size = 1) => new() { Path = path, Name = path, ItemType = StorageItemType.File, Size = size };

    /// <summary>A same-connection backend whose native copy waits until <paramref name="gate"/> opens (or it is cancelled, when it honours cancellation).</summary>
    private static FakeStorageBackend Gated(string id, TaskCompletionSource gate, Action? started = null, bool honourCancel = true) => new(
        id,
        capabilities: new StorageCapabilities(StorageFeature.FileCopy | StorageFeature.ServerSideCopy),
        getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path))),
        copy: async (_, _, token) =>
        {
            started?.Invoke();
            if (honourCancel) await gate.Task.WaitAsync(token);
            else await gate.Task;
            return Result.Success();
        });

    // ---------------------------------------------------------------- R4-B9

    // needs-review R4-B9
    [Theory]
    [InlineData("pause")]
    [InlineData("cancel")]
    [InlineData("remove")]
    public async Task Controls_on_a_job_whose_provider_ignores_cancellation_return_after_the_control_timeout(string control)
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Library.RegisterBackend("Stubborn", Gated("Stubborn", gate, () => started.TrySetResult(), honourCancel: false)).IsSuccess);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { ControlTimeout = TimeSpan.FromMilliseconds(200) });
        await queue.EnqueueCopyAsync("Stubborn", "a.bin", "Stubborn", "b.bin", jobId: "x");
        await started.Task.WaitAsync(Wait);

        var call = control switch
        {
            "pause" => queue.PauseJobAsync("x"),
            "cancel" => queue.CancelAsync("x"),
            _ => queue.RemoveAsync("x")
        };
        var answered = await call.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StorageErrors.TimeoutCode, answered.Error?.Code);
        gate.TrySetResult();
        await queue.WaitForIdleAsync().WaitAsync(Wait);
    }

    // needs-review R4-B9
    [Fact]
    public async Task An_invalid_control_timeout_is_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();

        var opened = await fixture.Library.OpenTransferQueueAsync(new StorageTransferQueueOptions { ControlTimeout = TimeSpan.FromSeconds(-1) });

        Assert.Equal(StorageErrors.InvalidContentCode, opened.Error?.Code);
    }

    // ---------------------------------------------------------------- R4-B10

    // needs-review R4-B10
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_queued_job_starts_after_a_remove_that_failed_or_was_refused(bool refuse)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        store.BeforeRemove = async id =>
        {
            // The queue resumes while the job is held for the removal, so this Pump passes it by.
            queue.Resume();
            if (!refuse) throw new IOException("store down");
            var current = (await store.Inner.GetAsync(id, default))!;
            await store.Inner.SaveAsync(current with { Priority = 1 }, null, false, default);
        };

        var removed = await queue.RemoveAsync("x");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(removed.IsFailure);
        Assert.Equal(StorageTransferState.Completed, queue.Get("x")!.State);
    }

    // ---------------------------------------------------------------- R4-B11

    // needs-review R4-B11
    [Fact]
    public async Task A_job_rewritten_by_a_newer_schema_during_a_refused_claim_is_not_run()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        var once = 1;
        store.BeforeClaim = async (id, _) =>
        {
            if (Interlocked.Exchange(ref once, 0) == 0) return true;
            var current = (await store.Inner.GetAsync(id, default))!;
            await store.Inner.SaveAsync(current with { SchemaVersion = StorageTransferJobRecord.CurrentSchemaVersion + 1 }, null, false, default);
            return false;
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await Eventually(() => queue.Get("x") is null);
        await Task.Delay(300);

        var stored = (await store.Inner.GetAsync("x", default))!;
        Assert.Equal((StorageTransferState.Queued, StorageTransferJobRecord.CurrentSchemaVersion + 1), (stored.State, stored.SchemaVersion));
        Assert.False((await fixture.Destination.ExistsAsync("x.bin")).Value);
        Assert.Null(queue.Get("x"));
    }

    // needs-review R4-B11
    [Fact]
    public async Task A_job_rewritten_by_a_newer_schema_before_the_outcome_is_saved_is_not_run_again()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        var once = 1;
        store.BeforeSave = async (record, lease) =>
        {
            if (record.State != StorageTransferState.Completed || lease is null || Interlocked.Exchange(ref once, 0) == 0) return;
            // A newer worker takes the job over (its lease ended) and rewrites it in its own schema.
            await store.Inner.ReleaseAsync(lease, default);
            var current = (await store.Inner.GetAsync(record.Id, default))!;
            await store.Inner.SaveAsync(current with { State = StorageTransferState.Queued, SchemaVersion = StorageTransferJobRecord.CurrentSchemaVersion + 1 }, null, false, default);
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await Eventually(() => queue.Get("x") is null);
        await Task.Delay(300);

        var stored = (await store.Inner.GetAsync("x", default))!;
        Assert.Equal((StorageTransferState.Queued, StorageTransferJobRecord.CurrentSchemaVersion + 1), (stored.State, stored.SchemaVersion));
        Assert.Null(queue.Get("x"));
    }

    // ---------------------------------------------------------------- R4-B12

    // needs-review R4-B12
    [Fact]
    public async Task A_save_that_always_fails_ends_the_job_after_a_bounded_number_of_attempts()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore
        {
            // Every phase save throws (a constraint, say); the other saves work.
            BeforeSave = (record, _) => record.State == StorageTransferState.Running && record.Checkpoint.Phase == StorageTransferPhase.Transferring
                ? throw new IOException("constraint violated")
                : Task.CompletedTask
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions
        {
            Store = store,
            LeaseDuration = TimeSpan.FromSeconds(1),
            RetryBaseDelay = TimeSpan.Zero,
            RetryMaxDelay = TimeSpan.Zero
        });

        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var job = queue.Get("x")!;
        Assert.Equal((StorageTransferState.Failed, StorageErrors.UnavailableCode), (job.State, job.Error?.Code));
        Assert.InRange(job.Attempts, 2, 10);
        Assert.Equal(StorageTransferState.Failed, (await store.Inner.GetAsync("x", default))!.State);
    }

    // ---------------------------------------------------------------- R4-B13

    // needs-review R4-B13
    [Fact]
    public async Task A_throw_while_the_outcome_is_saved_still_answers_the_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Library.RegisterBackend("Slow", Gated("Slow", gate, () => started.TrySetResult())).IsSuccess);
        var once = 1;
        var store = new ScriptedStore();
        store.BeforeSave = async (record, lease) =>
        {
            if (record.State != StorageTransferState.Cancelled || lease is null || Interlocked.Exchange(ref once, 0) == 0) return;
            // The store commits a record it cannot read back whole (no checkpoint), then the connection drops.
            await store.Inner.SaveAsync(record with { Checkpoint = null! }, lease, false, default);
            throw new IOException("connection reset");
        };
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.EnqueueCopyAsync("Slow", "a.bin", "Slow", "b.bin", jobId: "x");
        await started.Task.WaitAsync(Wait);

        var cancelled = await queue.CancelAsync("x").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StorageErrors.UnavailableCode, cancelled.Error?.Code);
        // The attempt ended: its slot is free and nothing holds the job.
        Assert.Equal(StorageTransferState.Running, queue.Get("x")!.State);
        Assert.Equal(StorageErrors.ConflictCode, (await queue.CancelAsync("x")).Error?.Code);
    }

    // ---------------------------------------------------------------- R4-B14

    // needs-review R4-B14
    [Fact]
    public async Task A_progress_handler_that_waits_on_cancelling_its_own_job_does_not_deadlock()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("big.bin", new byte[4 * 1024 * 1024]);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { ControlTimeout = TimeSpan.FromMilliseconds(300), ProgressInterval = TimeSpan.Zero });
        Result? answer = null;
        var once = 1;
        queue.ProgressChanged += job =>
        {
            // Inline, on the transfer's thread: the pattern the docs warn against.
            if (Interlocked.Exchange(ref once, 0) == 1) answer = queue.CancelAsync(job.Id).GetAwaiter().GetResult();
        };

        await queue.EnqueueCopyAsync("Source", "big.bin", "Destination", "big.bin", jobId: "x");
        await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.NotNull(answer);
        Assert.Equal(StorageErrors.TimeoutCode, answer.Value.Error?.Code);
        Assert.NotEqual(StorageTransferState.Running, queue.Get("x")!.State);
    }

    // ---------------------------------------------------------------- R4-B15

    // needs-review R4-B15
    [Fact]
    public async Task Revisions_do_not_restart_when_a_job_id_is_added_again()
    {
        var store = new InMemoryStorageTransferJobStore();
        var first = (await store.AddAsync(Job("x"), default))!;
        var saved = (await store.SaveAsync(first with { Priority = 2 }, null, false, default))!;
        Assert.True(await store.RemoveAsync("x", saved.Revision, null, default));

        var again = (await store.AddAsync(Job("x"), default))!;

        Assert.True(again.Revision > saved.Revision);
        Assert.Null(await store.SaveAsync(first with { Priority = 9 }, null, false, default));
        Assert.False(await store.RemoveAsync("x", first.Revision, null, default));
        Assert.NotNull(await store.GetAsync("x", default));
    }

    // ---------------------------------------------------------------- R4-B16

    // needs-review R4-B16 (B36)
    [Fact]
    public async Task The_store_is_not_used_after_dispose_returns_even_by_an_attempt_that_outlived_the_timeout()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var claiming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var late = 0;
        var store = new ScriptedStore
        {
            BeforeClaim = async (_, _) =>
            {
                claiming.TrySetResult();
                await release.Task;
                return true;
            },
            BeforeGet = _ =>
            {
                if (Volatile.Read(ref disposed) == 1) Interlocked.Increment(ref late);
                return Task.CompletedTask;
            }
        };
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "x.bin", jobId: "x");
        await claiming.Task.WaitAsync(Wait);

        await queue.DisposeAsync().AsTask().WaitAsync(Wait);
        Volatile.Write(ref disposed, 1);
        release.TrySetResult();
        await Task.Delay(500);

        Assert.Equal(0, Volatile.Read(ref late));
        Assert.Equal(StorageTransferState.Queued, (await store.Inner.GetAsync("x", default))!.State);
        Assert.False((await fixture.Destination.ExistsAsync("x.bin")).Value);
    }

    // needs-review R4-B16 (B36)
    [Fact]
    public async Task Dispose_waits_for_a_store_refresh_in_flight()
    {
        await using var fixture = await Fixture.CreateAsync();
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        var store = new ScriptedStore
        {
            OnLoad = async records =>
            {
                if (Interlocked.Increment(ref loads) > 1)
                {
                    loading.TrySetResult();
                    await release.Task;
                }
                return records;
            }
        };
        var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StoreRefreshInterval = TimeSpan.FromMilliseconds(20), ShutdownTimeout = TimeSpan.FromSeconds(20) });
        await loading.Task.WaitAsync(Wait);

        var disposing = queue.DisposeAsync().AsTask();
        await Task.Delay(200);
        Assert.False(disposing.IsCompleted);
        release.TrySetResult();

        await disposing.WaitAsync(Wait);
    }

    // needs-review R4-B16 (B38)
    [Theory]
    [InlineData("c", true, new[] { "a", "c", "b", "d" })]
    [InlineData("b", false, new[] { "a", "c", "b", "d" })]
    [InlineData("a", false, new[] { "b", "a", "c", "d" })]
    [InlineData("d", true, new[] { "a", "b", "d", "c" })]
    public async Task Moves_among_several_equal_orders_make_room_instead_of_failing(string id, bool up, string[] expected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new InMemoryStorageTransferJobStore();
        foreach (var name in new[] { "a", "b", "c", "d" })
            await store.AddAsync(Job(name) with { Order = 5 }, default);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        Assert.Equal(["a", "b", "c", "d"], queue.Jobs.Select(job => job.Id));

        var moved = up ? await queue.MoveUpAsync(id) : await queue.MoveDownAsync(id);

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.Equal(expected, queue.Jobs.Select(job => job.Id));
        var reopened = (await store.LoadAsync(default)).OrderBy(record => record.Order).ThenBy(record => record.Id, StringComparer.Ordinal).Select(record => record.Id);
        Assert.Equal(expected, reopened);
    }

    // needs-review R4-B16 (B38)
    [Fact]
    public async Task A_move_that_fails_part_way_does_not_reorder_the_queue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore();
        await store.Inner.AddAsync(Job("a") with { Order = 5 }, default);
        await store.Inner.AddAsync(Job("c") with { Order = 6 }, default);
        await store.Inner.AddAsync(Job("b") with { Order = 7 }, default);
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        var saves = 0;
        store.BeforeSave = (_, _) => Interlocked.Increment(ref saves) == 2 ? throw new IOException("store down") : Task.CompletedTask;

        var moved = await queue.MoveUpAsync("b");

        var order = queue.Jobs.Select(job => job.Id).ToArray();
        var stored = (await store.Inner.LoadAsync(default)).OrderBy(record => record.Order).ThenBy(record => record.Id, StringComparer.Ordinal).Select(record => record.Id).ToArray();
        // Either the move happened, or nothing moved; never half of it.
        Assert.Equal(moved.IsSuccess ? ["a", "b", "c"] : ["a", "c", "b"], stored);
        Assert.Equal(stored, order);
    }

    // needs-review R4-B16 (B43)
    [Fact]
    public async Task A_queue_still_opening_when_the_library_stops_is_disposed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore
        {
            OnLoad = async records =>
            {
                loading.TrySetResult();
                await release.Task;
                return records;
            }
        };
        await store.Inner.AddAsync(Job("x"), default);

        var opening = fixture.Library.OpenTransferQueueAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await loading.Task.WaitAsync(Wait);
        await fixture.Library.OnStopAsync().WaitAsync(Wait);
        release.TrySetResult();
        var opened = await opening.WaitAsync(Wait);

        Assert.Equal(StorageErrors.UnavailableCode, opened.Error?.Code);
    }

    // needs-review R4-B16 (A16)
    [Fact]
    public async Task A_new_attempt_keeps_the_phase_an_earlier_attempt_reached()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Source.UploadBytesAsync("a.bin", [1]);
        var store = new ScriptedStore();
        // An earlier attempt got as far as changing the destination, then failed transiently.
        await store.Inner.AddAsync(Job("x", phase: StorageTransferPhase.Committing) with { Attempts = 1 }, default);

        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store });
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        var start = store.Saved.First(record => record.Id == "x" && record.State == StorageTransferState.Running);
        Assert.Equal(StorageTransferPhase.Committing, start.Checkpoint.Phase);
        Assert.Equal(StorageTransferState.Completed, queue.Get("x")!.State);
    }

    // needs-review R4-B16 (refresh ghost)
    [Fact]
    public async Task A_job_removed_while_a_refresh_takes_in_other_jobs_does_not_come_back()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new ScriptedStore();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { Store = store, StartPaused = true });
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "a.bin", jobId: "a");
        await queue.EnqueueCopyAsync("Source", "a.bin", "Destination", "b.bin", jobId: "b");
        // Another process changes "a", so the refresh takes it in and raises JobChanged before it reaches "b".
        var a = (await store.Inner.GetAsync("a", default))!;
        await store.Inner.SaveAsync(a with { Priority = 3 }, null, false, default);
        Result? removed = null;
        queue.JobChanged += job =>
        {
            if (job.Id == "a" && removed is null) removed = queue.RemoveAsync("b").GetAwaiter().GetResult();
        };

        await queue.RefreshAsync();

        Assert.True(removed?.IsSuccess);
        Assert.Null(await store.Inner.GetAsync("b", default));
        Assert.Null(queue.Get("b"));
    }

    // needs-review R4-B16 (null job id)
    [Fact]
    public async Task A_null_job_id_is_a_failed_result()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var queue = await fixture.OpenAsync(new StorageTransferQueueOptions { StartPaused = true });

        Assert.Null(queue.Get(null!));
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.CancelAsync(null!)).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.PauseJobAsync(null!)).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.RemoveAsync(null!)).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.ResumeJobAsync(null!)).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.RetryAsync(null!)).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.SetPriorityAsync(null!, 1)).Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, (await queue.MoveUpAsync(null!)).Error?.Code);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task Eventually(Func<bool> condition, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? Wait);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }

    /// <summary>An in-memory store with hooks around every call.</summary>
    private sealed class ScriptedStore : IStorageTransferJobStore
    {
        public InMemoryStorageTransferJobStore Inner { get; } = new();
        public ConcurrentQueue<StorageTransferJobRecord> Saved { get; } = new();
        public Func<IReadOnlyList<StorageTransferJobRecord>, Task<IReadOnlyList<StorageTransferJobRecord>>>? OnLoad { get; set; }
        public Func<string, Task>? BeforeGet { get; set; }
        public Func<string, string, Task<bool>>? BeforeClaim { get; set; }
        public Func<StorageTransferJobRecord, StorageTransferLease?, Task>? BeforeSave { get; set; }
        public Func<string, Task>? BeforeRemove { get; set; }

        public async Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken)
        {
            var records = await Inner.LoadAsync(cancellationToken);
            return OnLoad is null ? records : await OnLoad(records);
        }

        public Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken) => Inner.AddAsync(record, cancellationToken);

        public async Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
        {
            if (BeforeGet is not null) await BeforeGet(jobId);
            return await Inner.GetAsync(jobId, cancellationToken);
        }

        public async Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (BeforeClaim is not null && !await BeforeClaim(jobId, workerId)) return null;
            return await Inner.TryClaimAsync(jobId, workerId, expectedRevision, duration, cancellationToken);
        }

        public Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken) => Inner.RenewAsync(lease, duration, cancellationToken);

        public async Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken)
        {
            if (BeforeSave is not null) await BeforeSave(record, lease);
            var stored = await Inner.SaveAsync(record, lease, releaseLease, cancellationToken);
            if (stored is not null) Saved.Enqueue(stored);
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

        public static async Task<Fixture> CreateAsync()
        {
            var directory = new TestDirectory();
            var context = StorageLibraryTestSupport.CreateContext(directory.Path, new EventBus());
            var library = new global::CL.Storage.StorageLibrary();
            await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
            var source = new LocalStorageBackend("Source", new LocalConnectionConfig { RootPath = directory.CreateDirectory("src") });
            var destination = new LocalStorageBackend("Destination", new LocalConnectionConfig { RootPath = directory.CreateDirectory("dst") });
            Assert.True(library.RegisterBackend("Source", source).IsSuccess);
            Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
            return new Fixture { Directory = directory, Library = library, Source = source, Destination = destination };
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
