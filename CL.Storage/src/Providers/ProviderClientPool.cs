namespace CL.Storage.Providers;

/// <summary>
/// Keeps connected provider clients alive between operations for the session-oriented backends
/// (FTP, SFTP), which would otherwise pay a full connect and authentication handshake on every call.
/// </summary>
/// <remarks>
/// <para>
/// Clients are rented exclusively: a rented client is owned by one caller until it is returned, so
/// this preserves the single-threaded usage that <c>SftpClient</c> and <c>AsyncFtpClient</c> both
/// require while still reusing the underlying session.
/// </para>
/// <para>
/// Idle clients are checked before they are handed out, but the check is the client's own connection
/// flag (<c>SftpClient.IsConnected</c>, <c>AsyncFtpClient.IsConnected</c>), which reports local
/// socket state rather than probing the server. A session the server has already closed, or a
/// half-open socket, can therefore pass the check and fail the caller's next operation — a failure
/// mode connect-per-call did not have. The real defence is <c>idleLifetime</c>, kept well below the
/// idle timeouts SSH and FTP servers typically enforce, so a pooled session is retired before a
/// server is likely to have dropped it. A cheap liveness probe supplied by the backend would close
/// the remaining gap at the cost of a round trip per rent.
/// </para>
/// </remarks>
/// <typeparam name="TClient">Native client type owned by the pool.</typeparam>
internal sealed class ProviderClientPool<TClient> : IAsyncDisposable where TClient : class
{
    private readonly Func<TClient> _create;
    private readonly Func<TClient, CancellationToken, Task> _connectAsync;
    private readonly Func<TClient, bool> _isUsable;
    private readonly Func<TClient, ValueTask> _destroyAsync;
    private readonly int _maxIdle;
    private readonly TimeSpan _idleLifetime;
    private readonly Stack<Idle> _idle = new();
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private bool _disposed;

    /// <summary>Initializes a pool over one provider's native client lifecycle.</summary>
    /// <param name="create">Creates a new, unconnected client.</param>
    /// <param name="connectAsync">Connects and authenticates a freshly created client.</param>
    /// <param name="isUsable">Reports the client's own connection state; see the type remarks for what this does and does not detect.</param>
    /// <param name="destroyAsync">Disconnects and disposes a client the pool is giving up.</param>
    /// <param name="maxIdle">Maximum number of connected clients kept idle.</param>
    /// <param name="idleLifetime">Idle time after which a pooled client is discarded.</param>
    /// <param name="time">Clock used for idle expiry; defaults to the system clock.</param>
    public ProviderClientPool(
        Func<TClient> create,
        Func<TClient, CancellationToken, Task> connectAsync,
        Func<TClient, bool> isUsable,
        Func<TClient, ValueTask> destroyAsync,
        int maxIdle = 4,
        TimeSpan? idleLifetime = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(connectAsync);
        ArgumentNullException.ThrowIfNull(isUsable);
        ArgumentNullException.ThrowIfNull(destroyAsync);
        if (maxIdle <= 0) throw new ArgumentOutOfRangeException(nameof(maxIdle));
        _create = create;
        _connectAsync = connectAsync;
        _isUsable = isUsable;
        _destroyAsync = destroyAsync;
        _maxIdle = maxIdle;
        _idleLifetime = idleLifetime ?? TimeSpan.FromMinutes(2);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets the number of clients the pool has connected, reused or not.</summary>
    /// <remarks>Reused for assertions that connection count stays flat across paged listings.</remarks>
    public int ConnectionsOpened => Volatile.Read(ref _connectionsOpened);
    private int _connectionsOpened;

    /// <summary>Takes a connected client, reusing an idle session when one is live.</summary>
    /// <param name="cancellationToken">Token observed while connecting a new client.</param>
    /// <returns>A connected client owned by the caller until it is returned.</returns>
    public async Task<TClient> RentAsync(CancellationToken cancellationToken)
    {
        while (TryTakeIdle(out var pooled, out var expired))
        {
            if (!expired && _isUsable(pooled)) return pooled;
            await SafeDestroyAsync(pooled).ConfigureAwait(false);
        }

        var client = _create() ?? throw new InvalidOperationException($"The {typeof(TClient).Name} factory returned null.");
        try
        {
            await _connectAsync(client, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _connectionsOpened);
            return client;
        }
        catch
        {
            await SafeDestroyAsync(client).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Returns a client so the next operation can reuse its session.</summary>
    /// <param name="client">Client previously handed out by <see cref="RentAsync"/>.</param>
    /// <returns>A task representing the return or the disconnect that replaced it.</returns>
    public async ValueTask ReturnAsync(TClient client)
    {
        if (client is null) return;
        if (_isUsable(client))
        {
            lock (_gate)
            {
                if (!_disposed && _idle.Count < _maxIdle)
                {
                    _idle.Push(new Idle(client, _time.GetUtcNow()));
                    return;
                }
            }
        }

        await SafeDestroyAsync(client).ConfigureAwait(false);
    }

    /// <summary>Disconnects and disposes a client instead of returning it to the pool.</summary>
    /// <param name="client">Client whose session must not be reused.</param>
    /// <returns>A task representing the disconnect.</returns>
    /// <remarks>
    /// Used for clients handed to callers as native leases: external code may have changed the
    /// working directory or transfer mode, and the pool cannot know whether the session is still in
    /// the state the backend expects.
    /// </remarks>
    public ValueTask DiscardAsync(TClient client) => SafeDestroyAsync(client);

    /// <summary>Disconnects every idle client held by the pool.</summary>
    /// <returns>A task representing asynchronous shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        Idle[] pooled;
        lock (_gate)
        {
            _disposed = true;
            pooled = [.. _idle];
            _idle.Clear();
        }

        foreach (var entry in pooled) await SafeDestroyAsync(entry.Client).ConfigureAwait(false);
    }

    private bool TryTakeIdle(out TClient client, out bool expired)
    {
        lock (_gate)
        {
            if (_idle.Count > 0)
            {
                var entry = _idle.Pop();
                client = entry.Client;
                // Destroying inside the lock would block other renters, so an expired client is
                // handed back flagged and torn down by the caller.
                expired = _time.GetUtcNow() - entry.PooledAt > _idleLifetime;
                return true;
            }
        }

        client = null!;
        expired = false;
        return false;
    }

    private async ValueTask SafeDestroyAsync(TClient client)
    {
        try { await _destroyAsync(client).ConfigureAwait(false); }
        catch { /* A client being torn down must never fail the caller's operation. */ }
    }

    private readonly record struct Idle(TClient Client, DateTimeOffset PooledAt);
}
