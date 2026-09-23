namespace CL.Storage.Queue;

/// <summary>
/// Where a <see cref="StorageTransferQueue"/> keeps its jobs, so they survive a restart and can be shared
/// between processes. Implement it over a database to make the queue durable; the default keeps jobs in memory.
/// </summary>
/// <remarks>
/// <para>Implementations must make each method atomic for its job:</para>
/// <list type="bullet">
/// <item><see cref="TryClaimAsync"/> succeeds only when the job has no unexpired lease, and then records the
/// new owner, expiry, and a fencing token greater than any issued before for that job.</item>
/// <item><see cref="SaveAsync"/> with a lease succeeds only while that lease is the job's current one (same
/// owner and fencing token, not expired); it releases the lease when the saved record has no
/// <see cref="StorageTransferJobRecord.LeaseOwner"/>. Without a lease it succeeds only when no other worker
/// holds an unexpired lease. A refused save means another worker owns the job.</item>
/// </list>
/// </remarks>
public interface IStorageTransferJobStore
{
    /// <summary>Returns every stored job.</summary>
    Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Adds a job.</summary>
    /// <returns><see langword="false"/> when a job with the same id already exists.</returns>
    Task<bool> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken);

    /// <summary>Returns one job, or null.</summary>
    Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Claims a job for a worker.</summary>
    /// <returns>The lease, or null when another worker holds an unexpired lease or the job is gone.</returns>
    Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Extends a lease.</summary>
    /// <returns>The renewed lease, or null when it is no longer current.</returns>
    Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Replaces a job's record, fenced by <paramref name="lease"/> when given.</summary>
    /// <returns><see langword="false"/> when the save was refused (see the remarks on the interface).</returns>
    Task<bool> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, CancellationToken cancellationToken);

    /// <summary>Removes a job; does nothing when it is gone.</summary>
    Task RemoveAsync(string jobId, CancellationToken cancellationToken);
}

/// <summary>Keeps jobs in memory; they end with the process. The default store.</summary>
public sealed class InMemoryStorageTransferJobStore : IStorageTransferJobStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StorageTransferJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Creates an empty store.</summary>
    public InMemoryStorageTransferJobStore() : this(TimeProvider.System) { }

    /// <summary>Creates an empty store with a clock, for tests.</summary>
    /// <param name="time">The clock used for lease expiry.</param>
    public InMemoryStorageTransferJobStore(TimeProvider time) => _time = time;

    /// <inheritdoc />
    public Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<StorageTransferJobRecord>>([.. _jobs.Values]);
    }

    /// <inheritdoc />
    public Task<bool> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_jobs.TryAdd(record.Id, record));
    }

    /// <inheritdoc />
    public Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_jobs.GetValueOrDefault(jobId));
    }

    /// <inheritdoc />
    public Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var record)) return Task.FromResult<StorageTransferLease?>(null);
            var now = _time.GetUtcNow();
            if (record.LeaseOwner is not null && record.LeaseExpiresAt > now) return Task.FromResult<StorageTransferLease?>(null);
            var lease = new StorageTransferLease(jobId, workerId, record.FencingToken + 1, now + duration);
            _jobs[jobId] = record with { LeaseOwner = workerId, LeaseExpiresAt = lease.ExpiresAt, FencingToken = lease.FencingToken };
            return Task.FromResult<StorageTransferLease?>(lease);
        }
    }

    /// <inheritdoc />
    public Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!IsCurrent(lease, out var record)) return Task.FromResult<StorageTransferLease?>(null);
            var renewed = lease with { ExpiresAt = _time.GetUtcNow() + duration };
            _jobs[lease.JobId] = record! with { LeaseExpiresAt = renewed.ExpiresAt };
            return Task.FromResult<StorageTransferLease?>(renewed);
        }
    }

    /// <inheritdoc />
    public Task<bool> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(record.Id, out var current)) return Task.FromResult(false);
            if (lease is not null)
            {
                if (!IsCurrent(lease, out _)) return Task.FromResult(false);
                _jobs[record.Id] = record.LeaseOwner is null
                    ? record with { LeaseExpiresAt = null, FencingToken = current.FencingToken }
                    : record with { LeaseOwner = lease.WorkerId, LeaseExpiresAt = current.LeaseExpiresAt, FencingToken = current.FencingToken };
                return Task.FromResult(true);
            }
            if (current.LeaseOwner is not null && current.LeaseExpiresAt > _time.GetUtcNow()) return Task.FromResult(false);
            _jobs[record.Id] = record with { LeaseOwner = null, LeaseExpiresAt = null, FencingToken = current.FencingToken };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(string jobId, CancellationToken cancellationToken)
    {
        lock (_gate) _jobs.Remove(jobId);
        return Task.CompletedTask;
    }

    private bool IsCurrent(StorageTransferLease lease, out StorageTransferJobRecord? record) =>
        _jobs.TryGetValue(lease.JobId, out record) &&
        record.LeaseOwner == lease.WorkerId &&
        record.FencingToken == lease.FencingToken &&
        record.LeaseExpiresAt > _time.GetUtcNow();
}
