using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

/// <summary>Limits, retry behaviour, and storage for a <see cref="StorageTransferQueue"/>.</summary>
public sealed record StorageTransferQueueOptions
{
    /// <summary>Gets how many jobs run at once across all connections (the ceiling under adaptive concurrency).</summary>
    public int MaxConcurrentTransfers { get; init; } = 2;
    /// <summary>Gets how many jobs may use one connection at once, like FileZilla's per-server limit.</summary>
    public int MaxTransfersPerConnection { get; init; } = 2;
    /// <summary>Gets how often a job that failed transiently is retried automatically before it is marked failed.</summary>
    public int AutomaticRetries { get; init; } = 3;
    /// <summary>Gets the first retry delay; later retries double it, with jitter, up to <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Gets the longest retry delay; a server's <c>Retry-After</c> is honoured even when longer.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets whether the queue starts paused.</summary>
    public bool StartPaused { get; init; }
    /// <summary>Gets where jobs are kept; in memory when null. A durable store makes jobs survive restarts.</summary>
    public IStorageTransferJobStore? Store { get; init; }
    /// <summary>Gets this queue's worker identity for leases. Keep it stable across restarts to reclaim your own jobs at once.</summary>
    public string WorkerId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Gets how long a running job's lease lasts between renewals (renewed every third of it).</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Gets whether a job found running from an earlier process goes straight back to the queue when its
    /// checkpoint shows the destination was never touched; otherwise, and always when it may have been, it
    /// becomes <see cref="StorageTransferState.Interrupted"/>.
    /// </summary>
    public bool RequeueInterruptedWhenSafe { get; init; } = true;
    /// <summary>Gets how many finished jobs are kept; the oldest beyond it are removed. Null keeps all.</summary>
    public int? MaxFinishedJobs { get; init; } = 1_000;
    /// <summary>
    /// Gets whether concurrency adapts: it starts at one, adds a slot after each success, and halves after a
    /// transient failure, never exceeding <see cref="MaxConcurrentTransfers"/>.
    /// </summary>
    public bool AdaptiveConcurrency { get; init; }
    /// <summary>Gets the minimum time between two progress reports of one job (the last report always arrives).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Gets a context to raise <see cref="StorageTransferQueue.JobChanged"/> and <see cref="StorageTransferQueue.ProgressChanged"/> on, such as a UI thread.</summary>
    public SynchronizationContext? EventContext { get; init; }
    /// <summary>Gets how often to read the store for jobs changed by other processes; never when null.</summary>
    public TimeSpan? StoreRefreshInterval { get; init; }

    internal Result Validate()
    {
        if (MaxConcurrentTransfers is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("MaxConcurrentTransfers must be between 1 and 64."));
        if (MaxTransfersPerConnection is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("MaxTransfersPerConnection must be between 1 and 64."));
        if (AutomaticRetries is < 0 or > 50) return Result.Failure(StorageErrors.InvalidContent("AutomaticRetries must be between 0 and 50."));
        if (RetryBaseDelay < TimeSpan.Zero || RetryMaxDelay < RetryBaseDelay) return Result.Failure(StorageErrors.InvalidContent("Retry delays must be non-negative, and RetryMaxDelay at least RetryBaseDelay."));
        if (LeaseDuration < TimeSpan.FromSeconds(1)) return Result.Failure(StorageErrors.InvalidContent("LeaseDuration must be at least one second."));
        if (MaxFinishedJobs is < 0) return Result.Failure(StorageErrors.InvalidContent("MaxFinishedJobs cannot be negative."));
        if (string.IsNullOrWhiteSpace(WorkerId)) return Result.Failure(StorageErrors.InvalidContent("WorkerId is required."));
        return Result.Success();
    }
}

/// <summary>
/// Runs transfers in the background, like FileZilla's queue: a global and a per-connection limit (optionally
/// adaptive), integer priorities, pause and resume for the queue or one job, reordering, cancellation,
/// exponential backoff for transient failures, and states for jobs that need a person (blocked on trust or
/// credentials, or needing reconciliation). Jobs are data and live in an <see cref="IStorageTransferJobStore"/>,
/// so a durable store makes them survive restarts; leases with fencing keep two processes from running one job.
/// </summary>
public sealed class StorageTransferQueue : IAsyncDisposable
{
    private readonly StorageLibrary _library;
    private readonly StorageTransferQueueOptions _options;
    private readonly IStorageTransferJobStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _activeByConnection = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _attempts = [];
    private TaskCompletionSource _idle = CompletedIdle();
    private Timer? _refresh;
    private int _running;
    private int _adaptiveLimit;
    private long _order;
    private bool _paused;
    private bool _disposed;

    private StorageTransferQueue(StorageLibrary library, StorageTransferQueueOptions options)
    {
        _library = library;
        _options = options;
        _store = options.Store ?? new InMemoryStorageTransferJobStore();
        _paused = options.StartPaused;
        _adaptiveLimit = options.AdaptiveConcurrency ? 1 : options.MaxConcurrentTransfers;
    }

    /// <summary>Creates a queue and loads the store's jobs, applying restart rules to jobs left running.</summary>
    internal static async Task<Result<StorageTransferQueue>> OpenAsync(StorageLibrary library, StorageTransferQueueOptions options, CancellationToken cancellationToken)
    {
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageTransferQueue>.Failure(valid.Error!);
        var queue = new StorageTransferQueue(library, options);
        await queue.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (options.StoreRefreshInterval is { } interval)
            queue._refresh = new Timer(_ => _ = queue.RefreshAsync(CancellationToken.None), null, interval, interval);
        queue.Pump();
        return Result<StorageTransferQueue>.Success(queue);
    }

    /// <summary>Raised when a job is added, changes state, or is removed. Handlers must be quick; see <see cref="StorageTransferQueueOptions.EventContext"/>.</summary>
    public event Action<StorageTransferJob>? JobChanged;

    /// <summary>Raised with throttled progress of running jobs.</summary>
    public event Action<StorageTransferJob>? ProgressChanged;

    /// <summary>Gets whether the queue is paused; running jobs finish, but no new job starts.</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    /// <summary>Gets the current concurrency limit (it changes under <see cref="StorageTransferQueueOptions.AdaptiveConcurrency"/>).</summary>
    public int ConcurrencyLimit { get { lock (_gate) return _adaptiveLimit; } }

    /// <summary>Gets a snapshot of every job: highest priority first, then in queue order.</summary>
    public IReadOnlyList<StorageTransferJob> Jobs
    {
        get { lock (_gate) return [.. _jobs.Values.OrderByDescending(entry => entry.Record.Priority).ThenBy(entry => entry.Record.Order).Select(entry => entry.Snapshot())]; }
    }

    /// <summary>Gets a snapshot of failed jobs, which <see cref="RetryFailedAsync"/> re-queues.</summary>
    public IReadOnlyList<StorageTransferJob> FailedJobs => [.. Jobs.Where(job => job.State == StorageTransferState.Failed)];

    /// <summary>Returns one job, or null.</summary>
    /// <param name="jobId">The job.</param>
    public StorageTransferJob? Get(string jobId)
    {
        lock (_gate) return _jobs.TryGetValue(jobId, out var entry) ? entry.Snapshot() : null;
    }

    /// <summary>
    /// Adds a job. With a caller-chosen <paramref name="jobId"/> the call is idempotent: the same id with the
    /// same spec returns the existing job; the same id with a different spec fails with <c>storage.conflict</c>.
    /// </summary>
    /// <param name="spec">The work.</param>
    /// <param name="priority">Higher starts first; 0 by default.</param>
    /// <param name="jobId">A stable id, or null to generate one.</param>
    /// <param name="cancellationToken">Token used to cancel storing the job.</param>
    public async Task<Result<StorageTransferJob>> EnqueueAsync(StorageTransferJobSpec spec, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var valid = spec.Validate();
        if (valid.IsFailure) return Result<StorageTransferJob>.Failure(valid.Error!);
        if (jobId is not null && string.IsNullOrWhiteSpace(jobId))
            return Result<StorageTransferJob>.Failure(StorageErrors.InvalidContent("A job id cannot be blank."));
        var id = jobId ?? Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs.TryGetValue(id, out var local))
                return SameOrConflict(local.Record, spec);
        }
        var existing = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            Adopt(existing);
            return SameOrConflict(existing, spec);
        }

        var record = new StorageTransferJobRecord
        {
            Id = id,
            Spec = spec,
            Priority = priority,
            Order = Interlocked.Increment(ref _order),
            State = StorageTransferState.Queued,
            RetriesLeft = _options.AutomaticRetries,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        if (!await _store.AddAsync(record, cancellationToken).ConfigureAwait(false))
        {
            var raced = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (raced is null) return Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{id}' could not be stored."));
            Adopt(raced);
            return SameOrConflict(raced, spec);
        }
        Entry entry;
        lock (_gate)
        {
            entry = new Entry(record);
            _jobs[id] = entry;
            MarkBusy();
        }
        Raise(JobChanged, entry.Snapshot());
        Pump();
        return Result<StorageTransferJob>.Success(entry.Snapshot());
    }

    /// <summary>Queues a copy between (or within) connections.</summary>
    public Task<Result<StorageTransferJob>> EnqueueCopyAsync(string sourceConnectionId, string sourcePath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.Copy, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues a move between (or within) connections.</summary>
    public Task<Result<StorageTransferJob>> EnqueueMoveAsync(string sourceConnectionId, string sourcePath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.Move, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues an upload of a local file.</summary>
    public Task<Result<StorageTransferJob>> EnqueueUploadAsync(string localFilePath, string destinationConnectionId, string destinationPath, StorageUploadOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.UploadFile, SourcePath = localFilePath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, UploadOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues a download to a local file; <paramref name="options"/> can pick a version or a range.</summary>
    public Task<Result<StorageTransferJob>> EnqueueDownloadAsync(string sourceConnectionId, string sourcePath, string localFilePath, StorageDownloadOptions? options = null, StorageConflictPolicy? conflictPolicy = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.DownloadFile, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationPath = localFilePath, DownloadOptions = options, DownloadConflictPolicy = conflictPolicy }, priority, jobId, cancellationToken);

    /// <summary>Queues an upload of a local directory tree.</summary>
    public Task<Result<StorageTransferJob>> EnqueueUploadDirectoryAsync(string localDirectoryPath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.UploadDirectory, SourcePath = localDirectoryPath, DestinationConnectionId = destinationConnectionId, DestinationPath = destinationPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Queues a download of a directory tree to a local directory.</summary>
    public Task<Result<StorageTransferJob>> EnqueueDownloadDirectoryAsync(string sourceConnectionId, string sourcePath, string localDirectoryPath, StorageTransferOptions? options = null, int priority = 0, string? jobId = null, CancellationToken cancellationToken = default) =>
        EnqueueAsync(new StorageTransferJobSpec { Kind = StorageTransferKind.DownloadDirectory, SourceConnectionId = sourceConnectionId, SourcePath = sourcePath, DestinationPath = localDirectoryPath, TransferOptions = options }, priority, jobId, cancellationToken);

    /// <summary>Stops starting new jobs; running jobs finish.</summary>
    public void Pause()
    {
        lock (_gate) _paused = true;
    }

    /// <summary>Resumes starting queued jobs.</summary>
    public void Resume()
    {
        lock (_gate) _paused = false;
        Pump();
    }

    /// <summary>
    /// Holds one job. A running job's attempt is stopped; a resumable transfer keeps its staged data and
    /// continues from it when resumed.
    /// </summary>
    public Task<Result> PauseJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry =>
        {
            if (entry.Record.State == StorageTransferState.Running)
            {
                entry.PauseRequested = true;
                entry.Cancellation.Cancel();
                return (Result.Success(), null);
            }
            return entry.Record.State == StorageTransferState.Queued
                ? (Result.Success(), entry.Record with { State = StorageTransferState.Paused })
                : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is {entry.Record.State} and cannot be paused.")), null);
        });

    /// <summary>Releases a paused job back into the queue.</summary>
    public Task<Result> ResumeJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry => entry.Record.State == StorageTransferState.Paused
            ? (Result.Success(), entry.Record with { State = StorageTransferState.Queued, NextAttemptAt = null })
            : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is not paused.")), null));

    /// <summary>Cancels a queued, paused, or running job.</summary>
    public Task<Result> CancelAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry =>
        {
            if (entry.Record.IsFinished)
                return (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' has already finished.")), null);
            if (entry.Record.State == StorageTransferState.Running)
            {
                entry.Cancellation.Cancel();
                return (Result.Success(), null);
            }
            return (Result.Success(), entry.Record with { State = StorageTransferState.Cancelled, FinishedAt = DateTimeOffset.UtcNow });
        }, raiseCancelled: true);

    /// <summary>Re-queues a failed, cancelled, blocked, interrupted, or reconciled job, with its retries reset.</summary>
    public Task<Result> RetryAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry => entry.Record.State is StorageTransferState.Failed or StorageTransferState.Cancelled or
            StorageTransferState.Blocked or StorageTransferState.NeedsReconciliation or StorageTransferState.Interrupted
            ? (Result.Success(), entry.Record with
            {
                State = StorageTransferState.Queued,
                BlockReason = null,
                RetriesLeft = _options.AutomaticRetries,
                NextAttemptAt = null,
                Failure = null,
                FinishedAt = null
            })
            : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is {entry.Record.State}; only stopped jobs can be retried.")), null));

    /// <summary>Re-queues every failed job.</summary>
    /// <returns>The number of jobs re-queued.</returns>
    public async Task<int> RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var job in FailedJobs)
            if ((await RetryAsync(job.Id, cancellationToken).ConfigureAwait(false)).IsSuccess) count++;
        return count;
    }

    /// <summary>Changes a waiting job's priority; higher starts first.</summary>
    public Task<Result> SetPriorityAsync(string jobId, int priority, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry => (Result.Success(), entry.Record with { Priority = priority }));

    /// <summary>Moves a waiting job one place earlier among jobs of the same priority.</summary>
    public Task<Result> MoveUpAsync(string jobId, CancellationToken cancellationToken = default) => SwapAsync(jobId, earlier: true, cancellationToken);

    /// <summary>Moves a waiting job one place later among jobs of the same priority.</summary>
    public Task<Result> MoveDownAsync(string jobId, CancellationToken cancellationToken = default) => SwapAsync(jobId, earlier: false, cancellationToken);

    /// <summary>Removes a job. A running job is cancelled first and removed when its attempt stops.</summary>
    public async Task<Result> RemoveAsync(string jobId, CancellationToken cancellationToken = default)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Record.State == StorageTransferState.Running && entry.Attempt is not null)
            {
                entry.RemoveRequested = true;
                entry.Cancellation.Cancel();
                return Result.Success();
            }
            if (entry.Record.State == StorageTransferState.Running)
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is running in another process."));
            _jobs.Remove(jobId);
        }
        await _store.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
        Raise(JobChanged, entry.Snapshot() with { Record = entry.Record with { State = StorageTransferState.Cancelled } });
        UpdateIdle();
        return Result.Success();
    }

    /// <summary>Removes jobs in the given states — by default every finished one (completed, failed, cancelled).</summary>
    /// <returns>The number of jobs removed.</returns>
    public async Task<int> ClearAsync(params StorageTransferState[] states)
    {
        var targets = states.Length == 0
            ? [StorageTransferState.Completed, StorageTransferState.Failed, StorageTransferState.Cancelled]
            : states.Where(state => state != StorageTransferState.Running).ToArray();
        string[] ids;
        lock (_gate)
            ids = [.. _jobs.Values.Where(entry => targets.Contains(entry.Record.State)).Select(entry => entry.Record.Id)];
        var removed = 0;
        foreach (var id in ids)
            if ((await RemoveAsync(id).ConfigureAwait(false)).IsSuccess) removed++;
        return removed;
    }

    /// <summary>Reads the store again to pick up jobs added or changed by other processes.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StorageTransferJobRecord> records;
        try { records = await _store.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return; }
        foreach (var record in records) Adopt(record);
        Pump();
    }

    /// <summary>Waits until no job is queued or running here. A paused queue with waiting jobs is not idle.</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        return idle.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops running jobs and waits for them to record where they stopped. Queued jobs stay queued in the
    /// store; a job stopped before it touched its destination returns to the queue, any other becomes
    /// <see cref="StorageTransferState.Interrupted"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task[] attempts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            attempts = [.. _attempts];
        }
        if (_refresh is not null) await _refresh.DisposeAsync().ConfigureAwait(false);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var records = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _order = records.Count == 0 ? 0 : records.Max(record => record.Order);
        foreach (var record in records)
        {
            var current = record;
            if (current.State == StorageTransferState.Running && !LeasedElsewhere(current))
            {
                var recovered = Recover(current);
                if (await _store.SaveAsync(recovered, null, cancellationToken).ConfigureAwait(false))
                    current = recovered;
            }
            lock (_gate)
            {
                _jobs[current.Id] = new Entry(current);
                if (!current.IsFinished) MarkBusy();
            }
        }
        UpdateIdle();
    }

    /// <summary>Restart rule: back to the queue when the destination was never touched, otherwise Interrupted.</summary>
    private StorageTransferJobRecord Recover(StorageTransferJobRecord record)
    {
        var untouched = record.Checkpoint.Phase is StorageTransferPhase.NotStarted or StorageTransferPhase.Transferring;
        var state = untouched && _options.RequeueInterruptedWhenSafe ? StorageTransferState.Queued : StorageTransferState.Interrupted;
        return record with { State = state, LeaseOwner = null, LeaseExpiresAt = null, NextAttemptAt = null };
    }

    private bool LeasedElsewhere(StorageTransferJobRecord record) =>
        record.LeaseOwner is not null && record.LeaseOwner != _options.WorkerId && record.LeaseExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>Takes in a record from the store unless this process is running the job.</summary>
    private void Adopt(StorageTransferJobRecord record)
    {
        var changed = false;
        Entry entry;
        lock (_gate)
        {
            if (_jobs.TryGetValue(record.Id, out entry!))
            {
                if (entry.Attempt is not null || entry.Record == record) return;
                entry.Record = record;
            }
            else
            {
                entry = new Entry(record);
                _jobs[record.Id] = entry;
            }
            if (!record.IsFinished) MarkBusy();
            changed = true;
        }
        if (changed) Raise(JobChanged, entry.Snapshot());
        UpdateIdle();
    }

    private static Result<StorageTransferJob> SameOrConflict(StorageTransferJobRecord existing, StorageTransferJobSpec spec) =>
        existing.Spec.SameWorkAs(spec)
            ? Result<StorageTransferJob>.Success(new StorageTransferJob { Record = existing })
            : Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{existing.Id}' already exists with different work."));

    private async Task<Result> ControlAsync(
        string jobId,
        CancellationToken cancellationToken,
        Func<Entry, (Result Result, StorageTransferJobRecord? Next)> change,
        bool raiseCancelled = false)
    {
        Entry? entry;
        StorageTransferJobRecord? next;
        StorageTransferJobRecord previous;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Record.State == StorageTransferState.Running && entry.Attempt is null)
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is running in another process."));
            previous = entry.Record;
            var (result, proposed) = change(entry);
            if (result.IsFailure || proposed is null) return result;
            next = proposed;
            entry.Record = next;
            if (next.State == StorageTransferState.Queued) MarkBusy();
        }
        if (!await _store.SaveAsync(next, null, cancellationToken).ConfigureAwait(false))
        {
            lock (_gate) entry.Record = previous;
            return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is owned by another worker."));
        }
        Raise(JobChanged, entry.Snapshot());
        if (raiseCancelled && next.State == StorageTransferState.Cancelled)
            await PublishAsync(new StorageTransferCancelledEvent(next.Id, next.Spec.Kind, next.Spec.SourceLabel, next.Spec.DestinationLabel, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        UpdateIdle();
        Pump();
        return Result.Success();
    }

    private async Task<Result> SwapAsync(string jobId, bool earlier, CancellationToken cancellationToken)
    {
        StorageTransferJobRecord mine, theirs;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Record.State is not (StorageTransferState.Queued or StorageTransferState.Paused))
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is not waiting."));
            var peers = _jobs.Values
                .Where(other => other.Record.Priority == entry.Record.Priority && other.Record.State is StorageTransferState.Queued or StorageTransferState.Paused)
                .OrderBy(other => other.Record.Order)
                .ToList();
            var index = peers.IndexOf(entry);
            var neighbour = earlier ? index - 1 : index + 1;
            if (neighbour < 0 || neighbour >= peers.Count) return Result.Success();
            var other = peers[neighbour];
            mine = entry.Record with { Order = other.Record.Order };
            theirs = other.Record with { Order = entry.Record.Order };
            entry.Record = mine;
            other.Record = theirs;
        }
        await _store.SaveAsync(mine, null, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(theirs, null, cancellationToken).ConfigureAwait(false);
        Raise(JobChanged, Get(mine.Id)!);
        Raise(JobChanged, Get(theirs.Id)!);
        return Result.Success();
    }

    /// <summary>Starts as many eligible jobs as the limits allow: highest priority first, then queue order.</summary>
    private void Pump()
    {
        var started = new List<Entry>();
        TimeSpan? wakeIn = null;
        lock (_gate)
        {
            if (_paused || _disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _jobs.Values
                .Where(entry => entry.Record.State == StorageTransferState.Queued && (entry.Record.NextAttemptAt ?? DateTimeOffset.MinValue) <= now && !LeasedElsewhere(entry.Record))
                .OrderByDescending(entry => entry.Record.Priority)
                .ThenBy(entry => entry.Record.Order))
            {
                if (_running >= _adaptiveLimit) break;
                if (entry.Record.Spec.Connections.Any(id => _activeByConnection.GetValueOrDefault(id) >= _options.MaxTransfersPerConnection))
                    continue;
                _running++;
                foreach (var id in entry.Record.Spec.Connections)
                    _activeByConnection[id] = _activeByConnection.GetValueOrDefault(id) + 1;
                entry.Record = entry.Record with { State = StorageTransferState.Running };
                entry.PauseRequested = false;
                entry.RemoveRequested = false;
                if (entry.Cancellation.IsCancellationRequested)
                {
                    entry.Cancellation.Dispose();
                    entry.Cancellation = new CancellationTokenSource();
                }
                // Started under the gate, so the attempt cannot finish before it is recorded here.
                var run = entry;
                entry.Attempt = Task.Run(() => RunAsync(run));
                _attempts.Add(entry.Attempt);
                started.Add(entry);
            }
            // Timers can fire slightly early, so the wake-up comes from the earliest due time.
            var nextDue = _jobs.Values
                .Where(entry => entry.Record.State == StorageTransferState.Queued && entry.Record.NextAttemptAt > now)
                .Select(entry => entry.Record.NextAttemptAt)
                .Min();
            if (nextDue is { } due)
                wakeIn = due - now + TimeSpan.FromMilliseconds(5);
            // Jobs another process left running are recovered once their lease has lapsed.
            if (_jobs.Values.Any(entry => entry.Record.State == StorageTransferState.Running && entry.Attempt is null && !LeasedElsewhere(entry.Record) && !started.Contains(entry)))
                _ = RecoverLapsedAsync();
        }
        if (wakeIn is { } delay)
            _ = WakeLaterAsync(delay);
    }

    private async Task RecoverLapsedAsync()
    {
        Entry[] lapsed;
        lock (_gate)
            lapsed = [.. _jobs.Values.Where(entry => entry.Record.State == StorageTransferState.Running && entry.Attempt is null && !LeasedElsewhere(entry.Record))];
        foreach (var entry in lapsed)
        {
            var fresh = await _store.GetAsync(entry.Record.Id, CancellationToken.None).ConfigureAwait(false);
            if (fresh is null || fresh.State != StorageTransferState.Running || LeasedElsewhere(fresh)) continue;
            var recovered = Recover(fresh);
            if (await _store.SaveAsync(recovered, null, CancellationToken.None).ConfigureAwait(false))
                Adopt(recovered);
        }
        Pump();
    }

    private async Task RunAsync(Entry entry)
    {
        StorageTransferJobSpec spec;
        lock (_gate) spec = entry.Record.Spec;
        var lease = await _store.TryClaimAsync(entry.Record.Id, _options.WorkerId, _options.LeaseDuration, CancellationToken.None).ConfigureAwait(false);
        if (lease is null)
        {
            // Another worker owns it; take its record and step aside.
            var owned = await _store.GetAsync(entry.Record.Id, CancellationToken.None).ConfigureAwait(false);
            Release(entry, owned ?? entry.Record with { State = StorageTransferState.Queued });
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var running = entry.Record with
        {
            State = StorageTransferState.Running,
            Attempts = entry.Record.Attempts + 1,
            StartedAt = startedAt,
            BlockReason = null,
            LeaseOwner = _options.WorkerId,
            Checkpoint = entry.Record.Checkpoint with { Phase = StorageTransferPhase.NotStarted }
        };
        if (!await _store.SaveAsync(running, lease, CancellationToken.None).ConfigureAwait(false))
        {
            Release(entry, await _store.GetAsync(entry.Record.Id, CancellationToken.None).ConfigureAwait(false) ?? entry.Record);
            return;
        }
        lock (_gate) entry.Record = running;
        Raise(JobChanged, entry.Snapshot());
        await PublishAsync(new StorageTransferStartedEvent(running.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, startedAt)).ConfigureAwait(false);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, _shutdown.Token);
        var holder = new LeaseHolder(lease);
        var renewal = RenewAsync(holder, attempt);
        var phase = StorageTransferPhase.NotStarted;
        async Task RecordPhaseAsync(StorageTransferPhase next, CancellationToken token)
        {
            phase = next;
            StorageTransferJobRecord record;
            lock (_gate) record = entry.Record = entry.Record with { Checkpoint = entry.Record.Checkpoint with { Phase = next } };
            if (!await _store.SaveAsync(record, holder.Lease, token).ConfigureAwait(false))
            {
                // A worker that lost its lease must not go on to change the destination.
                holder.Lost = true;
                await attempt.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }

        StorageTransferReport? report = null;
        Result result;
        try
        {
            (result, report) = await ExecuteAsync(entry, spec, RecordPhaseAsync, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            result = Result.Failure(StorageErrors.Unavailable("The transfer was stopped."));
        }
        catch (Exception error)
        {
            result = Result.Failure(StorageErrors.FromException(error, "Queued transfer"));
        }
        await attempt.CancelAsync().ConfigureAwait(false);
        try { await renewal.ConfigureAwait(false); } catch (OperationCanceledException) { }

        var outcome = Decide(entry, result, report, phase, holder.Lost, entry.Cancellation.IsCancellationRequested);
        if (outcome.Next is { } next)
        {
            if (!await _store.SaveAsync(next, holder.Lease, CancellationToken.None).ConfigureAwait(false))
                next = await _store.GetAsync(next.Id, CancellationToken.None).ConfigureAwait(false) ?? next;
            Finish(entry, next, report, success: result.IsSuccess, transient: outcome.Transient);
        }
        else
        {
            // Removed, or the lease was lost: the store is the truth.
            if (entry.RemoveRequested)
                await _store.RemoveAsync(entry.Record.Id, CancellationToken.None).ConfigureAwait(false);
            var fresh = entry.RemoveRequested ? null : await _store.GetAsync(entry.Record.Id, CancellationToken.None).ConfigureAwait(false);
            Finish(entry, fresh, report, success: false, transient: false);
        }
        await PublishOutcomeAsync(outcome.Next, result.Error, report).ConfigureAwait(false);
        await PruneAsync().ConfigureAwait(false);
        UpdateIdle();
        Pump();
    }

    /// <summary>Runs one attempt of a job.</summary>
    private async Task<(Result Result, StorageTransferReport? Report)> ExecuteAsync(
        Entry entry,
        StorageTransferJobSpec spec,
        Func<StorageTransferPhase, CancellationToken, Task> phase,
        CancellationToken cancellationToken)
    {
        var progress = new Throttled(this, entry);
        switch (spec.Kind)
        {
            case StorageTransferKind.Copy or StorageTransferKind.Move:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                if (entry.Record.Checkpoint.ResumeToken is { } token)
                    options = options with { ResumeToken = token };
                var report = spec.Kind == StorageTransferKind.Copy
                    ? await _library.CopyAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false)
                    : await _library.MoveAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (report.ToResult(), report);
            }
            case StorageTransferKind.UploadFile:
            {
                // Uploads stage inside the provider, so the destination is untouched until they finish.
                await phase(StorageTransferPhase.Transferring, cancellationToken).ConfigureAwait(false);
                var uploaded = await _library.GetStorage(spec.DestinationConnectionId!)
                    .UploadFileAsync(spec.DestinationPath, spec.SourcePath, (spec.UploadOptions ?? new StorageUploadOptions()) with { Progress = progress }, cancellationToken).ConfigureAwait(false);
                return (uploaded.IsSuccess ? Result.Success() : Result.Failure(uploaded.Error!), null);
            }
            case StorageTransferKind.DownloadFile:
            {
                await phase(StorageTransferPhase.Transferring, cancellationToken).ConfigureAwait(false);
                var downloaded = await _library.GetStorage(spec.SourceConnectionId!)
                    .DownloadToFileAsync(spec.SourcePath, spec.DestinationPath, (spec.DownloadOptions ?? new StorageDownloadOptions()) with { Progress = progress },
                        conflictPolicy: spec.DownloadConflictPolicy, cancellationToken: cancellationToken).ConfigureAwait(false);
                return (downloaded.IsSuccess ? Result.Success() : Result.Failure(downloaded.Error!), null);
            }
            case StorageTransferKind.UploadDirectory:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                var uploaded = await _library.UploadDirectoryAsync(spec.SourcePath, spec.DestinationConnectionId!, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (uploaded.IsSuccess ? Result.Success() : Result.Failure(uploaded.Error!), null);
            }
            default:
            {
                var options = (spec.TransferOptions ?? new StorageTransferOptions()) with { Progress = progress, PhaseChanged = phase };
                var downloaded = await _library.DownloadDirectoryAsync(spec.SourceConnectionId!, spec.SourcePath, spec.DestinationPath, options, cancellationToken).ConfigureAwait(false);
                return (downloaded.IsSuccess ? Result.Success() : Result.Failure(downloaded.Error!), null);
            }
        }
    }

    /// <summary>Decides a job's next record after an attempt; null when the job was removed or the lease lost.</summary>
    private (StorageTransferJobRecord? Next, bool Transient) Decide(
        Entry entry,
        Result result,
        StorageTransferReport? report,
        StorageTransferPhase phase,
        bool leaseLost,
        bool stoppedByCaller)
    {
        if (leaseLost || entry.RemoveRequested) return (null, false);
        var now = DateTimeOffset.UtcNow;
        var record = entry.Record with
        {
            LeaseOwner = null,
            LeaseExpiresAt = null,
            Checkpoint = new StorageTransferCheckpoint(phase, report?.ResumeToken ?? (result.IsFailure ? entry.Record.Checkpoint.ResumeToken : null)),
            Failure = StorageTransferFailure.From(result.Error)
        };
        if (entry.PauseRequested && stoppedByCaller)
            return (record with { State = StorageTransferState.Paused, Failure = null }, false);
        if (stoppedByCaller)
            return (record with { State = StorageTransferState.Cancelled, FinishedAt = now, Failure = null }, false);
        if (_shutdown.IsCancellationRequested)
            return (Recover(record with { State = StorageTransferState.Running }) with { Failure = null }, false);
        if (result.IsSuccess)
            return (record with { State = StorageTransferState.Completed, FinishedAt = now, Failure = null, Checkpoint = new StorageTransferCheckpoint(phase) }, false);

        var error = result.Error!;
        if (report?.Outcome == StorageTransferOutcome.NeedsReconciliation || error.Code == StorageErrors.PartialFailureCode)
            return (record with { State = StorageTransferState.NeedsReconciliation }, false);
        if (BlockReasonFor(error) is { } reason)
            return (record with { State = StorageTransferState.Blocked, BlockReason = reason }, false);
        if (StorageErrorInfo.IsTransient(error) && record.RetriesLeft > 0)
        {
            return (record with
            {
                State = StorageTransferState.Queued,
                RetriesLeft = record.RetriesLeft - 1,
                NextAttemptAt = now + Backoff(record.Attempts, error)
            }, true);
        }
        return (record with { State = StorageTransferState.Failed, FinishedAt = now }, StorageErrorInfo.IsTransient(error));
    }

    /// <summary>Trust and credential failures need a person, not a retry.</summary>
    internal static StorageTransferBlockReason? BlockReasonFor(Error error)
    {
        if (error.Code == StorageErrors.HostKeyRejectedCode) return StorageTransferBlockReason.Trust;
        if (error.Code == StorageErrors.AuthenticationFailedCode) return StorageTransferBlockReason.Credential;
        if (error.Code != StorageErrors.TlsFailureCode) return null;
        StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out var reason);
        return reason switch
        {
            TlsDiagnosis.ServerCertificateRejected => StorageTransferBlockReason.Trust,
            TlsDiagnosis.ClientCertificateRejected => StorageTransferBlockReason.Credential,
            _ => null
        };
    }

    /// <summary>Exponential backoff with ±50% jitter, clamped, and never shorter than a server's Retry-After.</summary>
    private TimeSpan Backoff(int attempt, Error error)
    {
        var exponential = _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, Math.Min(attempt - 1, 20)));
        var jittered = exponential * (0.5 + Random.Shared.NextDouble());
        var delay = TimeSpan.FromMilliseconds(Math.Min(jittered, _options.RetryMaxDelay.TotalMilliseconds));
        return StorageErrorInfo.TryGetRetryAfter(error, out var requested) && requested > delay ? requested : delay;
    }

    private void Finish(Entry entry, StorageTransferJobRecord? next, StorageTransferReport? report, bool success, bool transient)
    {
        lock (_gate)
        {
            _running--;
            foreach (var id in entry.Record.Spec.Connections)
                _activeByConnection[id] = Math.Max(0, _activeByConnection.GetValueOrDefault(id) - 1);
            if (entry.Attempt is not null) _attempts.Remove(entry.Attempt);
            entry.Attempt = null;
            entry.Progress = null;
            entry.LastReport = report ?? entry.LastReport;
            if (_options.AdaptiveConcurrency)
            {
                if (success) _adaptiveLimit = Math.Min(_options.MaxConcurrentTransfers, _adaptiveLimit + 1);
                else if (transient) _adaptiveLimit = Math.Max(1, _adaptiveLimit / 2);
            }
            if (next is null) _jobs.Remove(entry.Record.Id);
            else entry.Record = next;
        }
        Raise(JobChanged, next is null ? entry.Snapshot() with { Record = entry.Record with { State = StorageTransferState.Cancelled } } : entry.Snapshot());
    }

    private void Release(Entry entry, StorageTransferJobRecord record)
    {
        lock (_gate)
        {
            _running--;
            foreach (var id in entry.Record.Spec.Connections)
                _activeByConnection[id] = Math.Max(0, _activeByConnection.GetValueOrDefault(id) - 1);
            if (entry.Attempt is not null) _attempts.Remove(entry.Attempt);
            entry.Attempt = null;
            entry.Record = record;
        }
        Raise(JobChanged, entry.Snapshot());
        UpdateIdle();
    }

    private async Task RenewAsync(LeaseHolder holder, CancellationTokenSource attempt)
    {
        var interval = _options.LeaseDuration / 3;
        while (!attempt.IsCancellationRequested)
        {
            try { await Task.Delay(interval, attempt.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var renewed = await _store.RenewAsync(holder.Lease, _options.LeaseDuration, CancellationToken.None).ConfigureAwait(false);
            if (renewed is null)
            {
                holder.Lost = true;
                await attempt.CancelAsync().ConfigureAwait(false);
                return;
            }
            holder.Lease = renewed;
        }
    }

    private async Task PruneAsync()
    {
        if (_options.MaxFinishedJobs is not { } keep) return;
        string[] excess;
        lock (_gate)
        {
            var finished = _jobs.Values.Where(entry => entry.Record.IsFinished).OrderByDescending(entry => entry.Record.FinishedAt).ToList();
            if (finished.Count <= keep) return;
            excess = [.. finished.Skip(keep).Select(entry => entry.Record.Id)];
            foreach (var id in excess) _jobs.Remove(id);
        }
        foreach (var id in excess)
            await _store.RemoveAsync(id, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task PublishOutcomeAsync(StorageTransferJobRecord? record, Error? error, StorageTransferReport? report)
    {
        if (record is null) return;
        var spec = record.Spec;
        var now = DateTimeOffset.UtcNow;
        switch (record.State)
        {
            case StorageTransferState.Completed:
                await PublishAsync(new StorageTransferCompletedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Attempts, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Failed:
                await PublishAsync(new StorageTransferFailedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Attempts, error?.Code ?? StorageErrors.ProviderErrorCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Cancelled:
                await PublishAsync(new StorageTransferCancelledEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Queued when record.NextAttemptAt is { } due:
                await PublishAsync(new StorageTransferRetryingEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.Attempts, due - now, error?.Code ?? StorageErrors.ProviderErrorCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.Blocked:
                await PublishAsync(new StorageTransferBlockedEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, record.BlockReason!.Value, error?.Code ?? StorageErrors.ProviderErrorCode, now)).ConfigureAwait(false);
                break;
            case StorageTransferState.NeedsReconciliation:
                await PublishAsync(new StorageTransferNeedsReconciliationEvent(record.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, error?.Code ?? StorageErrors.PartialFailureCode, now)).ConfigureAwait(false);
                break;
        }
    }

    private async Task WakeLaterAsync(TimeSpan delay)
    {
        try { await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        Pump();
    }

    /// <summary>Re-arms the idle signal when work is added or re-queued; call under the gate.</summary>
    private void MarkBusy()
    {
        if (_idle.Task.IsCompleted) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void UpdateIdle()
    {
        lock (_gate)
        {
            if (_jobs.Values.All(entry => entry.Record.State is not (StorageTransferState.Queued or StorageTransferState.Running) ||
                                          (entry.Record.State == StorageTransferState.Running && entry.Attempt is null && LeasedElsewhere(entry.Record))))
                _idle.TrySetResult();
        }
    }

    private void Raise(Action<StorageTransferJob>? handler, StorageTransferJob snapshot)
    {
        if (handler is null) return;
        void Invoke()
        {
            try { handler(snapshot); }
            catch { /* A faulty subscriber must not break the queue. */ }
        }
        if (_options.EventContext is { } context) context.Post(_ => Invoke(), null);
        else Invoke();
    }

    private async Task PublishAsync<TEvent>(TEvent @event) where TEvent : CodeLogic.Core.Events.IEvent
    {
        try { await _library.CaptureEventPublisher().PublishAsync(@event).ConfigureAwait(false); }
        catch { /* The library may be stopping; queue events are best-effort. */ }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.SetResult();
        return idle;
    }

    /// <summary>Keeps the latest report on the job and forwards it at most every <see cref="StorageTransferQueueOptions.ProgressInterval"/>.</summary>
    private sealed class Throttled(StorageTransferQueue queue, Entry entry) : IProgress<StorageTransferProgress>
    {
        private long _lastForward = long.MinValue;

        public void Report(StorageTransferProgress value)
        {
            lock (queue._gate) entry.Progress = value;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastForward);
            if (!value.IsCompleted && last != long.MinValue && now - last < queue._options.ProgressInterval.TotalMilliseconds)
                return;
            if (Interlocked.CompareExchange(ref _lastForward, now, last) != last && !value.IsCompleted)
                return;
            StorageTransferJob snapshot;
            lock (queue._gate) snapshot = entry.Snapshot();
            queue.Raise(queue.ProgressChanged, snapshot);
        }
    }

    private sealed class LeaseHolder(StorageTransferLease lease)
    {
        public StorageTransferLease Lease { get; set; } = lease;
        public bool Lost { get; set; }
    }

    private sealed class Entry(StorageTransferJobRecord record)
    {
        public StorageTransferJobRecord Record { get; set; } = record;
        public StorageTransferProgress? Progress { get; set; }
        public StorageTransferReport? LastReport { get; set; }
        public CancellationTokenSource Cancellation { get; set; } = new();
        public Task? Attempt { get; set; }
        public bool PauseRequested { get; set; }
        public bool RemoveRequested { get; set; }

        public StorageTransferJob Snapshot() => new() { Record = Record, Progress = Progress, LastReport = LastReport };
    }
}
