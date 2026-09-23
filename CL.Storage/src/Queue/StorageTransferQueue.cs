using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Queue;

/// <summary>What a queued job does.</summary>
public enum StorageTransferKind
{
    /// <summary>Copies an item between (or within) connections.</summary>
    Copy,
    /// <summary>Moves an item between (or within) connections.</summary>
    Move,
    /// <summary>Uploads a local file.</summary>
    UploadFile,
    /// <summary>Downloads to a local file.</summary>
    DownloadFile,
    /// <summary>Uploads a local directory tree.</summary>
    UploadDirectory,
    /// <summary>Downloads a directory tree to a local directory.</summary>
    DownloadDirectory
}

/// <summary>Order in which waiting jobs start.</summary>
public enum StorageTransferPriority
{
    /// <summary>Starts after normal jobs.</summary>
    Low,
    /// <summary>Default priority.</summary>
    Normal,
    /// <summary>Starts before normal jobs.</summary>
    High
}

/// <summary>Where a job is in its life cycle.</summary>
public enum StorageTransferState
{
    /// <summary>Waiting for a free slot.</summary>
    Queued,
    /// <summary>Transferring.</summary>
    Running,
    /// <summary>Finished successfully.</summary>
    Completed,
    /// <summary>Failed permanently or ran out of retries; can be re-queued.</summary>
    Failed,
    /// <summary>Cancelled by the caller.</summary>
    Cancelled
}

/// <summary>Limits and retry behaviour for a <see cref="StorageTransferQueue"/>.</summary>
public sealed record StorageTransferQueueOptions
{
    /// <summary>Gets how many jobs run at once across all connections.</summary>
    public int MaxConcurrentTransfers { get; init; } = 2;
    /// <summary>Gets how many jobs may use one connection at once, like FileZilla's per-server limit.</summary>
    public int MaxTransfersPerConnection { get; init; } = 2;
    /// <summary>Gets how often a job that failed transiently is re-queued automatically before it is marked failed.</summary>
    public int AutomaticRetries { get; init; } = 2;
    /// <summary>Gets the pause before an automatic retry.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Gets whether the queue starts paused.</summary>
    public bool StartPaused { get; init; }

    internal Result Validate()
    {
        if (MaxConcurrentTransfers is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("MaxConcurrentTransfers must be between 1 and 64."));
        if (MaxTransfersPerConnection is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("MaxTransfersPerConnection must be between 1 and 64."));
        if (AutomaticRetries is < 0 or > 20) return Result.Failure(StorageErrors.InvalidContent("AutomaticRetries must be between 0 and 20."));
        return RetryDelay < TimeSpan.Zero ? Result.Failure(StorageErrors.InvalidContent("RetryDelay cannot be negative.")) : Result.Success();
    }
}

/// <summary>An immutable snapshot of one queued job.</summary>
/// <param name="Id">Stable job identifier.</param>
/// <param name="Kind">What the job does.</param>
/// <param name="Priority">Start priority.</param>
/// <param name="State">Current state.</param>
/// <param name="Source">Source description (<c>connection:path</c> or a local path).</param>
/// <param name="Destination">Destination description (<c>connection:path</c> or a local path).</param>
/// <param name="Attempts">Runs so far, including automatic retries.</param>
/// <param name="Progress">Latest progress report, if any.</param>
/// <param name="Error">Failure of the last attempt, if any.</param>
/// <param name="EnqueuedAt">When the job was added.</param>
/// <param name="FinishedAt">When the job completed, failed, or was cancelled.</param>
public sealed record StorageTransferJob(
    Guid Id,
    StorageTransferKind Kind,
    StorageTransferPriority Priority,
    StorageTransferState State,
    string Source,
    string Destination,
    int Attempts,
    StorageTransferProgress? Progress,
    Error? Error,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Runs transfers in the background with a global and a per-connection concurrency limit, priorities,
/// pause and resume, per-job cancellation, automatic re-queueing of transient failures, and a failed list
/// that can be retried, like FileZilla's transfer queue. Jobs are held in memory.
/// </summary>
public sealed class StorageTransferQueue : IAsyncDisposable
{
    private readonly StorageLibrary _library;
    private readonly StorageTransferQueueOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _jobs = [];
    private readonly Dictionary<string, int> _activeByConnection = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _idle = CompletedIdle();
    private int _running;
    private long _sequence;
    private bool _paused;
    private bool _disposed;

    internal StorageTransferQueue(StorageLibrary library, StorageTransferQueueOptions options)
    {
        _library = library;
        _options = options;
        _paused = options.StartPaused;
    }

    /// <summary>Raised when a job is added or changes state. Handlers run on transfer threads and must be quick.</summary>
    public event Action<StorageTransferJob>? JobChanged;

    /// <summary>Raised with throttled progress of running jobs. Handlers run on transfer threads and must be quick.</summary>
    public event Action<StorageTransferJob>? ProgressChanged;

    /// <summary>Gets whether the queue is paused; running jobs finish, but no new job starts.</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    /// <summary>Gets a snapshot of every job, oldest first.</summary>
    public IReadOnlyList<StorageTransferJob> Jobs
    {
        get { lock (_gate) return [.. _jobs.Values.OrderBy(entry => entry.Sequence).Select(entry => entry.Snapshot())]; }
    }

    /// <summary>Gets a snapshot of failed jobs, which <see cref="RetryFailed"/> re-queues.</summary>
    public IReadOnlyList<StorageTransferJob> FailedJobs => [.. Jobs.Where(job => job.State == StorageTransferState.Failed)];

    /// <summary>Queues a copy between (or within) connections.</summary>
    public StorageTransferJob EnqueueCopy(string sourceConnectionId, string sourcePath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, StorageTransferPriority priority = StorageTransferPriority.Normal) =>
        Add(StorageTransferKind.Copy, priority, $"{sourceConnectionId}:{sourcePath}", $"{destinationConnectionId}:{destinationPath}", [sourceConnectionId, destinationConnectionId],
            (progress, token) => _library.CopyAsync(sourceConnectionId, sourcePath, destinationConnectionId, destinationPath, (options ?? new()) with { Progress = progress }, token));

    /// <summary>Queues a move between (or within) connections.</summary>
    public StorageTransferJob EnqueueMove(string sourceConnectionId, string sourcePath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, StorageTransferPriority priority = StorageTransferPriority.Normal) =>
        Add(StorageTransferKind.Move, priority, $"{sourceConnectionId}:{sourcePath}", $"{destinationConnectionId}:{destinationPath}", [sourceConnectionId, destinationConnectionId],
            (progress, token) => _library.MoveAsync(sourceConnectionId, sourcePath, destinationConnectionId, destinationPath, (options ?? new()) with { Progress = progress }, token));

    /// <summary>Queues an upload of a local file.</summary>
    public StorageTransferJob EnqueueUpload(string localFilePath, string destinationConnectionId, string destinationPath, StorageUploadOptions? options = null, StorageTransferPriority priority = StorageTransferPriority.Normal) =>
        Add(StorageTransferKind.UploadFile, priority, localFilePath, $"{destinationConnectionId}:{destinationPath}", [destinationConnectionId],
            async (progress, token) =>
            {
                var result = await _library.GetStorage(destinationConnectionId)
                    .UploadFileAsync(destinationPath, localFilePath, (options ?? new()) with { Progress = progress }, token).ConfigureAwait(false);
                return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
            });

    /// <summary>Queues a download to a local file.</summary>
    public StorageTransferJob EnqueueDownload(string sourceConnectionId, string sourcePath, string localFilePath, StorageConflictPolicy? conflictPolicy = null, StorageTransferPriority priority = StorageTransferPriority.Normal) =>
        Add(StorageTransferKind.DownloadFile, priority, $"{sourceConnectionId}:{sourcePath}", localFilePath, [sourceConnectionId],
            async (progress, token) =>
            {
                var result = await _library.GetStorage(sourceConnectionId)
                    .DownloadToFileAsync(sourcePath, localFilePath, new StorageDownloadOptions { Progress = progress }, conflictPolicy: conflictPolicy, cancellationToken: token).ConfigureAwait(false);
                return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
            });

    /// <summary>Queues an upload of a local directory tree.</summary>
    public StorageTransferJob EnqueueUploadDirectory(string localDirectoryPath, string destinationConnectionId, string destinationPath, StorageTransferOptions? options = null, StorageTransferPriority priority = StorageTransferPriority.Normal) =>
        Add(StorageTransferKind.UploadDirectory, priority, localDirectoryPath, $"{destinationConnectionId}:{destinationPath}", [destinationConnectionId],
            async (progress, token) =>
            {
                var result = await _library.UploadDirectoryAsync(localDirectoryPath, destinationConnectionId, destinationPath, (options ?? new()) with { Progress = progress }, token).ConfigureAwait(false);
                return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
            });

    /// <summary>Queues a download of a directory tree to a local directory.</summary>
    public StorageTransferJob EnqueueDownloadDirectory(string sourceConnectionId, string sourcePath, string localDirectoryPath, StorageTransferOptions? options = null, StorageTransferPriority priority = StorageTransferPriority.Normal) =>
        Add(StorageTransferKind.DownloadDirectory, priority, $"{sourceConnectionId}:{sourcePath}", localDirectoryPath, [sourceConnectionId],
            async (progress, token) =>
            {
                var result = await _library.DownloadDirectoryAsync(sourceConnectionId, sourcePath, localDirectoryPath, (options ?? new()) with { Progress = progress }, token).ConfigureAwait(false);
                return result.IsSuccess ? Result.Success() : Result.Failure(result.Error!);
            });

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

    /// <summary>Cancels a queued or running job.</summary>
    /// <returns><see langword="true"/> when the job existed and had not finished.</returns>
    public bool Cancel(Guid jobId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry) || entry.State is StorageTransferState.Completed or StorageTransferState.Failed or StorageTransferState.Cancelled)
                return false;
            if (entry.State == StorageTransferState.Queued)
                Finish(entry, StorageTransferState.Cancelled, null);
        }
        entry.Cancellation.Cancel();
        Raise(JobChanged, entry);
        UpdateIdle();
        return true;
    }

    /// <summary>Re-queues one failed or cancelled job.</summary>
    /// <returns><see langword="true"/> when the job was re-queued.</returns>
    public bool Retry(Guid jobId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out entry) || entry.State is not (StorageTransferState.Failed or StorageTransferState.Cancelled))
                return false;
            entry.Requeue(automaticRetriesLeft: _options.AutomaticRetries);
            MarkBusy();
        }
        Raise(JobChanged, entry);
        UpdateIdle();
        Pump();
        return true;
    }

    /// <summary>Re-queues every failed job.</summary>
    /// <returns>The number of jobs re-queued.</returns>
    public int RetryFailed() => FailedJobs.Count(job => Retry(job.Id));

    /// <summary>Removes finished jobs (completed, failed, or cancelled) from the list.</summary>
    /// <returns>The number of jobs removed.</returns>
    public int ClearFinished()
    {
        lock (_gate)
        {
            var finished = _jobs.Values.Where(entry => entry.IsFinished).Select(entry => entry.Id).ToArray();
            foreach (var id in finished) _jobs.Remove(id);
            return finished.Length;
        }
    }

    /// <summary>Waits until no job is queued or running. A paused queue with waiting jobs is not idle.</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate) idle = _idle.Task;
        return idle.WaitAsync(cancellationToken);
    }

    /// <summary>Cancels every job and waits for running jobs to stop.</summary>
    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = [.. _jobs.Values];
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var entry in entries.Where(entry => entry.State == StorageTransferState.Queued))
        {
            lock (_gate) Finish(entry, StorageTransferState.Cancelled, null);
        }
        UpdateIdle();
        try { await WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _shutdown.Dispose();
    }

    private StorageTransferJob Add(
        StorageTransferKind kind,
        StorageTransferPriority priority,
        string source,
        string destination,
        string[] connections,
        Func<IProgress<StorageTransferProgress>, CancellationToken, Task<Result>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entry = new Entry(Guid.NewGuid(), ++_sequence, kind, priority, source, destination,
                [.. connections.Distinct(StringComparer.OrdinalIgnoreCase)], work, _options.AutomaticRetries);
            _jobs.Add(entry.Id, entry);
            MarkBusy();
        }
        Raise(JobChanged, entry);
        Pump();
        return entry.Snapshot();
    }

    /// <summary>Starts as many eligible jobs as the limits allow: highest priority first, then oldest.</summary>
    private void Pump()
    {
        var started = new List<Entry>();
        TimeSpan? wakeIn = null;
        lock (_gate)
        {
            if (_paused || _disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var entry in _jobs.Values
                .Where(entry => entry.State == StorageTransferState.Queued && entry.NotBefore <= now)
                .OrderByDescending(entry => entry.Priority)
                .ThenBy(entry => entry.Sequence))
            {
                if (_running >= _options.MaxConcurrentTransfers) break;
                if (entry.Connections.Any(id => _activeByConnection.GetValueOrDefault(id) >= _options.MaxTransfersPerConnection))
                    continue;
                _running++;
                foreach (var id in entry.Connections)
                    _activeByConnection[id] = _activeByConnection.GetValueOrDefault(id) + 1;
                entry.Start();
                started.Add(entry);
            }
            // Jobs waiting out a retry delay need a later pass; timers can fire slightly early, so the
            // wake-up is derived from the earliest due time rather than from a fixed delay.
            var nextDue = _jobs.Values
                .Where(entry => entry.State == StorageTransferState.Queued && entry.NotBefore > now)
                .Select(entry => (DateTimeOffset?)entry.NotBefore)
                .Min();
            if (nextDue is { } due)
                wakeIn = due - now + TimeSpan.FromMilliseconds(5);
        }
        if (wakeIn is { } delay)
            _ = RetryLaterAsync(delay);
        foreach (var entry in started)
        {
            Raise(JobChanged, entry);
            _ = RunAsync(entry);
        }
    }

    private async Task RunAsync(Entry entry)
    {
        await PublishAsync(new StorageTransferStartedEvent(entry.Id, entry.Kind, entry.Source, entry.Destination, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        Result result;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, _shutdown.Token);
        try
        {
            var progress = new Throttled(entry, snapshot => Raise(ProgressChanged, snapshot));
            result = await entry.Work(progress, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            result = Result.Failure(StorageErrors.Unavailable("The transfer was cancelled."));
        }
        catch (Exception error)
        {
            result = Result.Failure(StorageErrors.FromException(error, "Queued transfer"));
        }

        lock (_gate)
        {
            _running--;
            foreach (var id in entry.Connections)
                _activeByConnection[id] = Math.Max(0, _activeByConnection.GetValueOrDefault(id) - 1);
            if (linked.IsCancellationRequested)
                Finish(entry, StorageTransferState.Cancelled, null);
            else if (result.IsSuccess)
                Finish(entry, StorageTransferState.Completed, null);
            else if (StorageErrorInfo.IsTransient(result.Error) && entry.AutomaticRetriesLeft > 0 && !_disposed)
            {
                entry.ScheduleRetry(result.Error!, DateTimeOffset.UtcNow + _options.RetryDelay);
            }
            else
                Finish(entry, StorageTransferState.Failed, result.Error);
        }

        Raise(JobChanged, entry);
        if (entry.State == StorageTransferState.Completed)
            await PublishAsync(new StorageTransferCompletedEvent(entry.Id, entry.Kind, entry.Source, entry.Destination, entry.Attempts, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        else if (entry.State == StorageTransferState.Failed)
            await PublishAsync(new StorageTransferFailedEvent(entry.Id, entry.Kind, entry.Source, entry.Destination, entry.Attempts, result.Error!.Code, DateTimeOffset.UtcNow)).ConfigureAwait(false);

        UpdateIdle();
        Pump();
    }

    private async Task RetryLaterAsync(TimeSpan delay)
    {
        try { await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }
        Pump();
    }

    private static void Finish(Entry entry, StorageTransferState state, Error? error) => entry.Finish(state, error);

    /// <summary>Re-arms the idle signal when work is added or re-queued; call under the gate.</summary>
    private void MarkBusy()
    {
        if (_idle.Task.IsCompleted) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void UpdateIdle()
    {
        lock (_gate)
        {
            if (_jobs.Values.All(entry => entry.IsFinished))
                _idle.TrySetResult();
        }
    }

    private void Raise(Action<StorageTransferJob>? handler, Entry entry) => Raise(handler, entry.Snapshot());

    private static void Raise(Action<StorageTransferJob>? handler, StorageTransferJob snapshot)
    {
        try { handler?.Invoke(snapshot); }
        catch { /* A faulty subscriber must not break the queue. */ }
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

    /// <summary>Stores the latest report on the job and forwards it to <see cref="ProgressChanged"/>.</summary>
    private sealed class Throttled(Entry entry, Action<StorageTransferJob> forward) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value)
        {
            entry.Progress = value;
            forward(entry.Snapshot());
        }
    }

    private sealed class Entry(
        Guid id,
        long sequence,
        StorageTransferKind kind,
        StorageTransferPriority priority,
        string source,
        string destination,
        string[] connections,
        Func<IProgress<StorageTransferProgress>, CancellationToken, Task<Result>> work,
        int automaticRetries)
    {
        public Guid Id { get; } = id;
        public long Sequence { get; } = sequence;
        public StorageTransferKind Kind { get; } = kind;
        public StorageTransferPriority Priority { get; } = priority;
        public string Source { get; } = source;
        public string Destination { get; } = destination;
        public string[] Connections { get; } = connections;
        public Func<IProgress<StorageTransferProgress>, CancellationToken, Task<Result>> Work { get; } = work;
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
        public StorageTransferState State { get; private set; } = StorageTransferState.Queued;
        public int Attempts { get; private set; }
        public int AutomaticRetriesLeft { get; private set; } = automaticRetries;
        public DateTimeOffset NotBefore { get; private set; } = DateTimeOffset.MinValue;
        public StorageTransferProgress? Progress { get; set; }
        public Error? Error { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public CancellationTokenSource Cancellation { get; private set; } = new();
        public bool IsFinished => State is StorageTransferState.Completed or StorageTransferState.Failed or StorageTransferState.Cancelled;

        public void Start()
        {
            State = StorageTransferState.Running;
            Attempts++;
            Progress = null;
        }

        public void ScheduleRetry(Error error, DateTimeOffset notBefore)
        {
            State = StorageTransferState.Queued;
            AutomaticRetriesLeft--;
            Error = error;
            NotBefore = notBefore;
        }

        public void Finish(StorageTransferState state, Error? error)
        {
            State = state;
            Error = error;
            FinishedAt = DateTimeOffset.UtcNow;
        }

        public void Requeue(int automaticRetriesLeft)
        {
            State = StorageTransferState.Queued;
            AutomaticRetriesLeft = automaticRetriesLeft;
            NotBefore = DateTimeOffset.MinValue;
            Error = null;
            FinishedAt = null;
            if (Cancellation.IsCancellationRequested)
            {
                Cancellation.Dispose();
                Cancellation = new CancellationTokenSource();
            }
        }

        public StorageTransferJob Snapshot() =>
            new(Id, Kind, Priority, State, Source, Destination, Attempts, Progress, Error, EnqueuedAt, FinishedAt);
    }
}
