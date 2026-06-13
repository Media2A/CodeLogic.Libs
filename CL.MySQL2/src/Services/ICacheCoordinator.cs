namespace CL.MySQL2.Services;

/// <summary>
/// Cross-node coordination for the query cache. The default
/// (<see cref="NullCacheCoordinator"/>) is single-node and does nothing; a distributed
/// adapter (Redis pub/sub, NATS, etc.) is supplied by the consumer via
/// <see cref="QueryCache.UseCoordinator"/> — exactly the same plug-in model as
/// <see cref="ICacheStore"/>. The library ships no transport dependency of its own.
/// <para>
/// Two responsibilities:
/// <list type="number">
///   <item><b>Invalidation fan-out</b> — <see cref="PublishInvalidationAsync"/> broadcasts
///     a local table mutation so peers drop their cached entries. Without this, each
///     process keeps its own per-table version counter and a mutation on one node never
///     invalidates the others.</item>
///   <item><b>Single-flight refresh</b> — <see cref="TryAcquireRefreshLeaseAsync"/> lets
///     exactly one node own a <see cref="SmartCachePool"/>'s refresh each tick, so N nodes
///     don't all hit the database. This assumes a <i>shared</i> <see cref="ICacheStore"/>
///     (e.g. Redis): the lease holder refreshes and writes; the others read the shared
///     entry.</item>
/// </list>
/// </para>
/// </summary>
public interface ICacheCoordinator : IAsyncDisposable
{
    /// <summary>
    /// Broadcasts that <paramref name="tableName"/> was mutated on this node so peers can
    /// invalidate their cached entries for it. Fire-and-forget from the caller's
    /// perspective; implementations should not throw on transient transport failures.
    /// </summary>
    Task PublishInvalidationAsync(string tableName, CancellationToken ct = default);

    /// <summary>
    /// Registers a handler invoked when a <i>peer</i> broadcasts an invalidation. The
    /// handler bumps the local table version and evicts matching entries (it must NOT
    /// re-broadcast — that would loop). Called once when the coordinator is installed.
    /// </summary>
    void OnInvalidation(Action<string> handler);

    /// <summary>
    /// Attempts to acquire the refresh lease for <paramref name="poolName"/> for the next
    /// <paramref name="lease"/> window. Returns <c>true</c> if this node should perform the
    /// refresh, <c>false</c> if a peer already holds it. The single-node default always
    /// returns <c>true</c>.
    /// </summary>
    Task<bool> TryAcquireRefreshLeaseAsync(string poolName, TimeSpan lease, CancellationToken ct = default);
}

/// <summary>
/// Single-node default: no fan-out, and every node (there is only one) always wins the
/// refresh lease — so behaviour is identical to the pre-coordination library.
/// </summary>
public sealed class NullCacheCoordinator : ICacheCoordinator
{
    /// <summary>Shared instance — stateless.</summary>
    public static readonly NullCacheCoordinator Instance = new();

    public Task PublishInvalidationAsync(string tableName, CancellationToken ct = default) =>
        Task.CompletedTask;

    public void OnInvalidation(Action<string> handler) { /* no peers to hear from */ }

    public Task<bool> TryAcquireRefreshLeaseAsync(string poolName, TimeSpan lease, CancellationToken ct = default) =>
        Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
