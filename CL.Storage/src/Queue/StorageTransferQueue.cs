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
    private bool _recovering;
    private DateTimeOffset? _wakeAt;
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
        try
        {
            await queue.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<StorageTransferQueue>.Failure(StoreError(error));
        }
        if (options.StoreRefreshInterval is { } interval)
            queue._refresh = new Timer(_ => _ = queue.RefreshAsync(CancellationToken.None), null, interval, interval);
        queue.Pump();
        return Result<StorageTransferQueue>.Success(queue);
    }

    /// <summary>Raised when a job is added, changes state, or is removed. Handlers must be quick; see <see cref="StorageTransferQueueOptions.EventContext"/>.</summary>
    public event Action<StorageTransferJob>? JobChanged;

    /// <summary>Raised when a job leaves the queue: removed, pruned from history, or gone from the store.</summary>
    public event Action<StorageTransferJob>? JobRemoved;

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
        StorageTransferJobRecord? existing;
        try { existing = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested) { return Result<StorageTransferJob>.Failure(StoreError(error)); }
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
        StorageTransferJobRecord? added;
        try { added = await _store.AddAsync(record, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested) { return Result<StorageTransferJob>.Failure(StoreError(error)); }
        if (added is null)
        {
            var (_, raced) = await TryGetAsync(id).ConfigureAwait(false);
            if (raced is null) return Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{id}' could not be stored."));
            Adopt(raced);
            return SameOrConflict(raced, spec);
        }
        record = added;
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

    /// <summary>
    /// Re-queues a failed, cancelled, blocked, interrupted, or reconciled job. Its retries and attempt count
    /// start over, so backoff starts from the base delay again.
    /// </summary>
    public Task<Result> RetryAsync(string jobId, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry => entry.Record.State is StorageTransferState.Failed or StorageTransferState.Cancelled or
            StorageTransferState.Blocked or StorageTransferState.NeedsReconciliation or StorageTransferState.Interrupted
            ? (Result.Success(), entry.Record with
            {
                State = StorageTransferState.Queued,
                BlockReason = null,
                Attempts = 0,
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

    /// <summary>Changes a waiting (queued or paused) job's priority; higher starts first.</summary>
    public Task<Result> SetPriorityAsync(string jobId, int priority, CancellationToken cancellationToken = default) =>
        ControlAsync(jobId, cancellationToken, entry => entry.Record.State is StorageTransferState.Queued or StorageTransferState.Paused
            ? (Result.Success(), entry.Record with { Priority = priority })
            : (Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is {entry.Record.State}; only waiting jobs can be reprioritized.")), null));

    /// <summary>Moves a waiting job one place earlier among jobs of the same priority.</summary>
    public Task<Result> MoveUpAsync(string jobId, CancellationToken cancellationToken = default) => SwapAsync(jobId, earlier: true, cancellationToken);

    /// <summary>Moves a waiting job one place later among jobs of the same priority.</summary>
    public Task<Result> MoveDownAsync(string jobId, CancellationToken cancellationToken = default) => SwapAsync(jobId, earlier: false, cancellationToken);

    /// <summary>
    /// Removes a job and raises <see cref="JobRemoved"/>. A running job is cancelled first and removed when its
    /// attempt stops.
    /// </summary>
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
        }
        try
        {
            await _store.RemoveAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(StoreError(error));
        }
        lock (_gate) _jobs.Remove(jobId);
        Raise(JobRemoved, entry.Snapshot());
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
    /// <see cref="StorageTransferState.Interrupted"/>. A job that finished as it was stopped keeps its result.
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
            // A job left running by an earlier process, including one that used this worker id, is recovered.
            if (current.State == StorageTransferState.Running && !LeasedElsewhere(current))
                current = await TryRecoverAsync(current, cancellationToken).ConfigureAwait(false) ?? current;
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
        return record with { State = state, NextAttemptAt = null };
    }

    /// <summary>Claims a job left running and records its restart state; null when that did not succeed.</summary>
    private async Task<StorageTransferJobRecord?> TryRecoverAsync(StorageTransferJobRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var lease = await _store.TryClaimAsync(record.Id, _options.WorkerId, record.Revision, _options.LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (lease is null) return null;
            return await _store.SaveAsync(Recover(record), lease, releaseLease: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private bool LeasedElsewhere(StorageTransferJobRecord record) =>
        record.LeaseOwner is not null && record.LeaseOwner != _options.WorkerId && record.LeaseExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>Takes in a record from the store unless this process is running the job or already has a newer one.</summary>
    private void Adopt(StorageTransferJobRecord record)
    {
        Entry entry;
        lock (_gate)
        {
            if (_jobs.TryGetValue(record.Id, out entry!))
            {
                if (entry.Attempt is not null || record.Revision < entry.Record.Revision || entry.Record == record) return;
                if (record.Revision == entry.Record.Revision && record.LeaseOwner == entry.Record.LeaseOwner && record.LeaseExpiresAt == entry.Record.LeaseExpiresAt) return;
                entry.Record = record;
            }
            else
            {
                entry = new Entry(record);
                _jobs[record.Id] = entry;
            }
            if (!record.IsFinished) MarkBusy();
        }
        Raise(JobChanged, entry.Snapshot());
        UpdateIdle();
    }

    /// <summary>Removes a job the store no longer has.</summary>
    private void Forget(string jobId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry) || entry.Attempt is not null) return;
            _jobs.Remove(jobId);
        }
        Raise(JobRemoved, entry.Snapshot());
        UpdateIdle();
    }

    private static Result<StorageTransferJob> SameOrConflict(StorageTransferJobRecord existing, StorageTransferJobSpec spec) =>
        existing.Spec.SameWorkAs(spec)
            ? Result<StorageTransferJob>.Success(new StorageTransferJob { Record = existing })
            : Result<StorageTransferJob>.Failure(StorageErrors.Conflict($"Job '{existing.Id}' already exists with different work."));

    private static Error StoreError(Exception error) =>
        StorageErrors.Unavailable($"The transfer job store failed: {error.GetType().Name}.");

    /// <summary>Reads one job from the store: (false, null) when the store failed, (true, null) when it is gone.</summary>
    private async Task<(bool Read, StorageTransferJobRecord? Record)> TryGetAsync(string jobId)
    {
        try { return (true, await _store.GetAsync(jobId, CancellationToken.None).ConfigureAwait(false)); }
        catch (Exception) { return (false, null); }
    }

    /// <summary>Saves a change made by a person. The save is compare-and-swap, so a stale copy reloads instead.</summary>
    private async Task<Result> ControlAsync(
        string jobId,
        CancellationToken cancellationToken,
        Func<Entry, (Result Result, StorageTransferJobRecord? Next)> change,
        bool raiseCancelled = false)
    {
        Entry? entry;
        StorageTransferJobRecord next;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Record.State == StorageTransferState.Running && entry.Attempt is null)
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is running in another process."));
            var (result, proposed) = change(entry);
            if (result.IsFailure || proposed is null) return result;
            next = proposed;
        }
        StorageTransferJobRecord? stored;
        try
        {
            stored = await _store.SaveAsync(next, null, releaseLease: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(StoreError(error));
        }
        if (stored is null)
        {
            var (read, fresh) = await TryGetAsync(jobId).ConfigureAwait(false);
            if (read && fresh is null) Forget(jobId);
            else if (fresh is not null) Adopt(fresh);
            return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' was changed by another worker; its current state has been reloaded."));
        }
        lock (_gate)
        {
            // An attempt that started meanwhile claimed the old revision; its claim fails and it adopts this one.
            if (entry.Attempt is null) entry.Record = stored;
            if (stored.State == StorageTransferState.Queued) MarkBusy();
        }
        Raise(JobChanged, entry.Snapshot());
        if (raiseCancelled && stored.State == StorageTransferState.Cancelled)
            await PublishAsync(new StorageTransferCancelledEvent(stored.Id, stored.Spec.Kind, stored.Spec.SourceLabel, stored.Spec.DestinationLabel, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        UpdateIdle();
        Pump();
        return Result.Success();
    }

    private async Task<Result> SwapAsync(string jobId, bool earlier, CancellationToken cancellationToken)
    {
        StorageTransferJobRecord mine, theirs;
        Entry entry, other;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry!)) return Result.Failure(StorageErrors.NotFound($"Job '{jobId}' was not found."));
            if (entry.Record.State is not (StorageTransferState.Queued or StorageTransferState.Paused))
                return Result.Failure(StorageErrors.Conflict($"Job '{jobId}' is not waiting."));
            var peers = _jobs.Values
                .Where(candidate => candidate.Attempt is null && candidate.Record.Priority == entry.Record.Priority &&
                                    candidate.Record.State is StorageTransferState.Queued or StorageTransferState.Paused)
                .OrderBy(candidate => candidate.Record.Order)
                .ToList();
            var index = peers.IndexOf(entry);
            var neighbour = earlier ? index - 1 : index + 1;
            if (neighbour < 0 || neighbour >= peers.Count) return Result.Success();
            other = peers[neighbour];
            mine = entry.Record with { Order = other.Record.Order };
            theirs = other.Record with { Order = entry.Record.Order };
        }
        StorageTransferJobRecord? savedMine = null, savedTheirs = null;
        try
        {
            savedMine = await _store.SaveAsync(mine, null, releaseLease: false, cancellationToken).ConfigureAwait(false);
            if (savedMine is not null)
                savedTheirs = await _store.SaveAsync(theirs, null, releaseLease: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            await ReloadAsync(mine.Id, theirs.Id).ConfigureAwait(false);
            return Result.Failure(StoreError(error));
        }
        if (savedMine is null || savedTheirs is null)
        {
            await ReloadAsync(mine.Id, theirs.Id).ConfigureAwait(false);
            return Result.Failure(StorageErrors.Conflict("The jobs were changed by another worker; their current order has been reloaded."));
        }
        lock (_gate)
        {
            if (entry.Attempt is null) entry.Record = savedMine;
            if (other.Attempt is null) other.Record = savedTheirs;
        }
        Raise(JobChanged, entry.Snapshot());
        Raise(JobChanged, other.Snapshot());
        return Result.Success();
    }

    private async Task ReloadAsync(params string[] jobIds)
    {
        foreach (var id in jobIds)
        {
            var (read, fresh) = await TryGetAsync(id).ConfigureAwait(false);
            if (read && fresh is null) Forget(id);
            else if (fresh is not null) Adopt(fresh);
        }
    }

    /// <summary>Starts as many eligible jobs as the limits allow: highest priority first, then queue order.</summary>
    private void Pump()
    {
        DateTimeOffset? wakeAt = null;
        var recover = false;
        lock (_gate)
        {
            if (_paused || _disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _jobs.Values
                .Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Queued &&
                                (entry.Record.NextAttemptAt ?? DateTimeOffset.MinValue) <= now && !LeasedElsewhere(entry.Record))
                .OrderByDescending(entry => entry.Record.Priority)
                .ThenBy(entry => entry.Record.Order)
                .ToList())
            {
                if (_running >= _adaptiveLimit) break;
                if (entry.Record.Spec.Connections.Any(id => _activeByConnection.GetValueOrDefault(id) >= _options.MaxTransfersPerConnection))
                    continue;
                _running++;
                foreach (var id in entry.Record.Spec.Connections)
                    _activeByConnection[id] = _activeByConnection.GetValueOrDefault(id) + 1;
                var claimed = entry.Record;
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
                entry.Attempt = Task.Run(() => RunAsync(run, claimed));
                _attempts.Add(entry.Attempt);
            }
            // Timers can fire slightly early, so the wake-up comes from the earliest due time.
            wakeAt = _jobs.Values
                .Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Queued && entry.Record.NextAttemptAt > now)
                .Select(entry => entry.Record.NextAttemptAt)
                .Min();
            // Jobs another process left running are recovered once their lease has lapsed, one pass at a time.
            var lapsed = _jobs.Values.Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Running && !LeasedElsewhere(entry.Record)).ToList();
            if (!_recovering && lapsed.Any(entry => entry.RecoverAfter <= now))
            {
                _recovering = true;
                recover = true;
            }
            var retryRecovery = lapsed.Where(entry => entry.RecoverAfter > now).Select(entry => (DateTimeOffset?)entry.RecoverAfter).Min();
            var leaseEnds = _jobs.Values.Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Running && LeasedElsewhere(entry.Record))
                .Select(entry => entry.Record.LeaseExpiresAt).Min();
            wakeAt = new[] { wakeAt, retryRecovery, leaseEnds }.Where(time => time is not null).Min();
        }
        if (recover)
            _ = Task.Run(RecoverLapsedAsync);
        if (wakeAt is { } due)
            ScheduleWake(due);
    }

    /// <summary>Arms one timer for the earliest time something becomes due; later requests reuse it.</summary>
    private void ScheduleWake(DateTimeOffset due)
    {
        lock (_gate)
        {
            if (_disposed || (_wakeAt is { } armed && armed <= due)) return;
            _wakeAt = due;
        }
        _ = WakeLaterAsync(due);
    }

    private async Task WakeLaterAsync(DateTimeOffset due)
    {
        try { await Task.Delay(Max(due - DateTimeOffset.UtcNow, TimeSpan.Zero) + TimeSpan.FromMilliseconds(5), _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        lock (_gate)
        {
            if (_wakeAt != due) return;
            _wakeAt = null;
        }
        Pump();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task RecoverLapsedAsync()
    {
        var changed = false;
        try
        {
            Entry[] lapsed;
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
                lapsed = [.. _jobs.Values.Where(entry => entry.Attempt is null && entry.Record.State == StorageTransferState.Running &&
                                                         !LeasedElsewhere(entry.Record) && entry.RecoverAfter <= now)];
            foreach (var entry in lapsed)
            {
                var (read, fresh) = await TryGetAsync(entry.Record.Id).ConfigureAwait(false);
                StorageTransferJobRecord? recovered = null;
                if (read && fresh is null)
                {
                    Forget(entry.Record.Id);
                    continue;
                }
                if (fresh is not null && (fresh.State != StorageTransferState.Running || LeasedElsewhere(fresh)))
                {
                    Adopt(fresh);
                    changed = true;
                    continue;
                }
                if (fresh is not null)
                    recovered = await TryRecoverAsync(fresh, CancellationToken.None).ConfigureAwait(false);
                if (recovered is not null)
                {
                    Adopt(recovered);
                    changed = true;
                }
                else
                {
                    // The store failed or another worker got there first: try again later, not in a loop.
                    lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + _options.LeaseDuration;
                }
            }
        }
        finally
        {
            lock (_gate) _recovering = false;
        }
        Pump();
        if (changed) UpdateIdle();
    }

    private async Task RunAsync(Entry entry, StorageTransferJobRecord claimed)
    {
        var spec = claimed.Spec;
        StorageTransferLease? lease;
        try
        {
            lease = await _store.TryClaimAsync(claimed.Id, _options.WorkerId, claimed.Revision, _options.LeaseDuration, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The store failed: wait before trying this job again instead of spinning on it.
            await StepAsideAsync(entry, claimed, storeFailed: true).ConfigureAwait(false);
            return;
        }
        if (lease is null)
        {
            await StepAsideAsync(entry, claimed).ConfigureAwait(false);
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var running = claimed with
        {
            State = StorageTransferState.Running,
            Attempts = claimed.Attempts + 1,
            StartedAt = startedAt,
            BlockReason = null,
            Checkpoint = claimed.Checkpoint with { Phase = StorageTransferPhase.NotStarted }
        };
        StorageTransferJobRecord? saved;
        try { saved = await _store.SaveAsync(running, lease, releaseLease: false, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception)
        {
            await StepAsideAsync(entry, claimed, storeFailed: true).ConfigureAwait(false);
            return;
        }
        if (saved is null)
        {
            await StepAsideAsync(entry, claimed).ConfigureAwait(false);
            return;
        }
        lock (_gate) entry.Record = saved;
        Raise(JobChanged, entry.Snapshot());
        await PublishAsync(new StorageTransferStartedEvent(saved.Id, spec.Kind, spec.SourceLabel, spec.DestinationLabel, startedAt)).ConfigureAwait(false);

        StorageTransferJobRecord? final = null;
        StorageTransferReport? report = null;
        var result = Result.Failure(StorageErrors.Unavailable("The transfer did not finish."));
        (StorageTransferJobRecord? Next, bool Transient) outcome = (null, false);
        var recorded = false;
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, _shutdown.Token);
            var holder = new LeaseHolder(lease);
            var renewal = RenewAsync(holder, attempt);
            var phase = StorageTransferPhase.NotStarted;
            async Task RecordPhaseAsync(StorageTransferPhase next, CancellationToken token)
            {
                phase = next;
                StorageTransferJobRecord current;
                lock (_gate) current = entry.Record;
                StorageTransferJobRecord? stored = null;
                try
                {
                    stored = await _store.SaveAsync(current with { Checkpoint = current.Checkpoint with { Phase = next } }, holder.Lease, releaseLease: false, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Without a recorded phase a crash could be misjudged, so the attempt stops and is retried.
                    holder.StoreFailed = true;
                }
                if (stored is null)
                {
                    // A worker that lost its lease must not go on to change the destination.
                    if (!holder.StoreFailed) holder.Lost = true;
                    await attempt.CancelAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                lock (_gate) entry.Record = stored!;
            }

            try
            {
                (result, report) = await ExecuteAsync(entry, spec, RecordPhaseAsync, attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                result = Result.Failure(StorageErrors.Unavailable(holder.StoreFailed
                    ? "The transfer was stopped because the job store failed."
                    : "The transfer was stopped."));
            }
            catch (Exception error)
            {
                result = Result.Failure(StorageErrors.FromException(error, "Queued transfer"));
            }
            await attempt.CancelAsync().ConfigureAwait(false);
            try { await renewal.ConfigureAwait(false); } catch (Exception) { }

            outcome = Decide(entry, result, report, phase, holder.Lost, entry.Cancellation.IsCancellationRequested && !holder.StoreFailed);
            if (outcome.Next is { } next)
            {
                StorageTransferJobRecord? stored = null;
                try { stored = await _store.SaveAsync(next, holder.Lease, releaseLease: true, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { }
                if (stored is not null)
                {
                    final = stored;
                }
                else
                {
                    var (read, fresh) = await TryGetAsync(next.Id).ConfigureAwait(false);
                    // The store refused (another worker owns the job now) or failed; its record, if readable, is the truth.
                    final = read ? fresh : next with { State = StorageTransferState.Running };
                    if (!read) lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + _options.LeaseDuration;
                }
            }
            else
            {
                // Removed, or the lease was lost: the store is the truth.
                if (entry.RemoveRequested)
                {
                    try { await _store.RemoveAsync(claimed.Id, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                }
                var (read, fresh) = entry.RemoveRequested ? (true, null) : await TryGetAsync(claimed.Id).ConfigureAwait(false);
                final = read ? fresh : entry.Record with { State = StorageTransferState.Running };
            }
            Finish(entry, final, report, success: result.IsSuccess, transient: outcome.Transient);
            recorded = true;
        }
        finally
        {
            // Whatever failed above, the slot is released and the job left in a state the queue can act on.
            if (!recorded)
                Finish(entry, entry.Record with { State = StorageTransferState.Running }, report, success: false, transient: false);
        }
        await PublishOutcomeAsync(final?.State == outcome.Next?.State ? final : null, result.Error, report).ConfigureAwait(false);
        await PruneAsync().ConfigureAwait(false);
        UpdateIdle();
        Pump();
    }

    /// <summary>
    /// Gives up a job this worker could not claim or start, taking the store's record. After a store failure
    /// the job waits (locally) before it is tried again, so an unavailable store is not hammered.
    /// </summary>
    private async Task StepAsideAsync(Entry entry, StorageTransferJobRecord claimed, bool storeFailed = false)
    {
        var (read, fresh) = await TryGetAsync(claimed.Id).ConfigureAwait(false);
        var record = read ? fresh : claimed;
        if (record is not null && (storeFailed || !read))
        {
            var wait = _options.RetryBaseDelay > TimeSpan.FromSeconds(1) ? _options.RetryBaseDelay : TimeSpan.FromSeconds(1);
            record = record with { NextAttemptAt = DateTimeOffset.UtcNow + wait };
            lock (_gate) entry.RecoverAfter = DateTimeOffset.UtcNow + wait;
        }
        Release(entry, record);
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
                StorageResumeToken? resume;
                lock (_gate) resume = entry.Record.Checkpoint.ResumeToken;
                if (resume is not null)
                    options = options with { ResumeToken = resume };
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

    /// <summary>
    /// Decides a job's next record after an attempt; null when the job was removed or the lease lost. A
    /// transfer that succeeded is Completed even if it was being paused, cancelled, or shut down as it finished.
    /// </summary>
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
        StorageTransferJobRecord current;
        lock (_gate) current = entry.Record;
        var record = current with
        {
            Checkpoint = new StorageTransferCheckpoint(phase, report?.ResumeToken ?? (result.IsFailure ? current.Checkpoint.ResumeToken : null)),
            Failure = StorageTransferFailure.From(result.Error)
        };
        if (result.IsSuccess)
            return (record with { State = StorageTransferState.Completed, FinishedAt = now, Failure = null, Checkpoint = new StorageTransferCheckpoint(phase) }, false);
        if (entry.PauseRequested && stoppedByCaller)
            return (record with { State = StorageTransferState.Paused, Failure = null }, false);
        if (stoppedByCaller)
            return (record with { State = StorageTransferState.Cancelled, FinishedAt = now, Failure = null }, false);
        if (_shutdown.IsCancellationRequested)
            return (Recover(record) with { Failure = null }, false);

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
        Raise(next is null ? JobRemoved : JobChanged, entry.Snapshot());
    }

    private void Release(Entry entry, StorageTransferJobRecord? record)
    {
        lock (_gate)
        {
            _running--;
            foreach (var id in entry.Record.Spec.Connections)
                _activeByConnection[id] = Math.Max(0, _activeByConnection.GetValueOrDefault(id) - 1);
            if (entry.Attempt is not null) _attempts.Remove(entry.Attempt);
            entry.Attempt = null;
            if (record is null) _jobs.Remove(entry.Record.Id);
            else entry.Record = record;
        }
        Raise(record is null ? JobRemoved : JobChanged, entry.Snapshot());
        UpdateIdle();
    }

    /// <summary>
    /// Renews the lease every third of its duration. A store that fails is tried again while the lease still
    /// holds; only a refused renewal, or a lease about to lapse, stops the attempt.
    /// </summary>
    private async Task RenewAsync(LeaseHolder holder, CancellationTokenSource attempt)
    {
        var interval = _options.LeaseDuration / 3;
        while (!attempt.IsCancellationRequested)
        {
            try { await Task.Delay(interval, attempt.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            StorageTransferLease? renewed;
            try
            {
                renewed = await _store.RenewAsync(holder.Lease, _options.LeaseDuration, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (DateTimeOffset.UtcNow + interval < holder.Lease.ExpiresAt) continue;
                holder.Lost = true;
                await attempt.CancelAsync().ConfigureAwait(false);
                return;
            }
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
        Entry[] excess;
        lock (_gate)
        {
            var finished = _jobs.Values.Where(entry => entry.Record.IsFinished).OrderByDescending(entry => entry.Record.FinishedAt).ToList();
            if (finished.Count <= keep) return;
            excess = [.. finished.Skip(keep)];
            foreach (var entry in excess) _jobs.Remove(entry.Record.Id);
        }
        foreach (var entry in excess)
        {
            try { await _store.RemoveAsync(entry.Record.Id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* Pruning is housekeeping; the next pass tries again. */ }
            Raise(JobRemoved, entry.Snapshot());
        }
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
        public bool StoreFailed { get; set; }
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
        /// <summary>When recovering a job left running may be tried again.</summary>
        public DateTimeOffset RecoverAfter { get; set; } = DateTimeOffset.MinValue;

        public StorageTransferJob Snapshot() => new() { Record = Record, Progress = Progress, LastReport = LastReport };
    }
}
