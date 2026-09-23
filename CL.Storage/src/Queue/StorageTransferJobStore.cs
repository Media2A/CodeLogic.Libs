namespace CL.Storage.Queue;

/// <summary>
/// Where a <see cref="StorageTransferQueue"/> keeps its jobs, so they survive a restart and can be shared
/// between processes. Implement it over a database to make the queue durable; the default keeps jobs in memory.
/// </summary>
/// <remarks>
/// <para>Each method must be atomic for its job. Two rules keep concurrent queues from overwriting each other:</para>
/// <list type="bullet">
/// <item><b>Revisions.</b> Every stored record has a <see cref="StorageTransferJobRecord.Revision"/>.
/// <see cref="AddAsync"/> stores revision 1, and every successful <see cref="SaveAsync"/> stores the next one.
/// A save whose record does not carry the current revision is refused: the caller worked from a stale copy.
/// Claims and renewals do not change the revision.</item>
/// <item><b>Leases.</b> The store owns <see cref="StorageTransferJobRecord.LeaseOwner"/>,
/// <see cref="StorageTransferJobRecord.LeaseExpiresAt"/>, and <see cref="StorageTransferJobRecord.FencingToken"/>;
/// the values in a record passed to <see cref="SaveAsync"/> are ignored. <see cref="TryClaimAsync"/> succeeds
/// only for the expected revision and when no other worker holds an unexpired lease (the same worker may claim
/// again, which a restarted process needs), and issues a fencing token greater than any before. A save with a
/// lease succeeds only while that lease is current (same owner and token, unexpired); a save without one only
/// while no worker holds an unexpired lease.</item>
/// </list>
/// <para>Records carry a <see cref="StorageTransferJobRecord.SchemaVersion"/>; a store that serializes them
/// should keep it, so a later version of the library can read old rows.</para>
/// </remarks>
public interface IStorageTransferJobStore
{
    /// <summary>Returns every stored job.</summary>
    Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Adds a job at revision 1.</summary>
    /// <returns>The stored record, or null when a job with the same id already exists.</returns>
    Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken);

    /// <summary>Returns one job, or null.</summary>
    Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Claims a job for a worker, provided it is still at <paramref name="expectedRevision"/>.</summary>
    /// <returns>The lease, or null when the job changed, is gone, or another worker holds an unexpired lease.</returns>
    Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>Extends a lease.</summary>
    /// <returns>The renewed lease, or null when it is no longer current.</returns>
    Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a job's record if it still has the record's <see cref="StorageTransferJobRecord.Revision"/>,
    /// fenced by <paramref name="lease"/> when given (see the remarks on the interface).
    /// </summary>
    /// <param name="record">The new content; its lease fields are ignored.</param>
    /// <param name="lease">The caller's lease, or null.</param>
    /// <param name="releaseLease">Whether to end <paramref name="lease"/> with this save.</param>
    /// <param name="cancellationToken">Token used to cancel the save.</param>
    /// <returns>The stored record at its new revision, or null when the save was refused.</returns>
    Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken);

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
    public Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken)
    {
        var stored = record with { Revision = 1, LeaseOwner = null, LeaseExpiresAt = null, FencingToken = 0 };
        lock (_gate) return Task.FromResult(_jobs.TryAdd(record.Id, stored) ? stored : null);
    }

    /// <inheritdoc />
    public Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_jobs.GetValueOrDefault(jobId));
    }

    /// <inheritdoc />
    public Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var record) || record.Revision != expectedRevision) return Task.FromResult<StorageTransferLease?>(null);
            var now = _time.GetUtcNow();
            if (record.LeaseOwner is not null && record.LeaseOwner != workerId && record.LeaseExpiresAt > now) return Task.FromResult<StorageTransferLease?>(null);
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
    public Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(record.Id, out var current) || current.Revision != record.Revision)
                return Task.FromResult<StorageTransferJobRecord?>(null);
            var leased = current.LeaseOwner is not null && current.LeaseExpiresAt > _time.GetUtcNow();
            StorageTransferJobRecord stored;
            if (lease is not null)
            {
                if (!IsCurrent(lease, out _)) return Task.FromResult<StorageTransferJobRecord?>(null);
                stored = releaseLease
                    ? record with { LeaseOwner = null, LeaseExpiresAt = null }
                    : record with { LeaseOwner = current.LeaseOwner, LeaseExpiresAt = current.LeaseExpiresAt };
            }
            else
            {
                if (leased) return Task.FromResult<StorageTransferJobRecord?>(null);
                stored = record with { LeaseOwner = null, LeaseExpiresAt = null };
            }
            stored = stored with { FencingToken = current.FencingToken, Revision = current.Revision + 1 };
            _jobs[record.Id] = stored;
            return Task.FromResult<StorageTransferJobRecord?>(stored);
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
