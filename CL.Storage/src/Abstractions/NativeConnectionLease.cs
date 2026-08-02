namespace CL.Storage.Abstractions;

/// <summary>Owns a native session and invokes its release callback at most once.</summary>
public sealed class NativeConnectionLease<TClient> : IAsyncDisposable where TClient : class
{
    private Func<TClient, ValueTask>? _releaseAsync;

    /// <summary>Initializes a lease around one native provider client or session.</summary>
    /// <param name="client">Native client exposed for the lifetime of this lease.</param>
    /// <param name="releaseAsync">Idempotently invoked once when the lease is disposed.</param>
    public NativeConnectionLease(TClient client, Func<TClient, ValueTask> releaseAsync)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _releaseAsync = releaseAsync ?? throw new ArgumentNullException(nameof(releaseAsync));
    }

    /// <summary>Gets the native provider client or session owned by this lease.</summary>
    public TClient Client { get; }

    /// <summary>Returns or closes the native connection. Repeated calls are no-ops.</summary>
    /// <returns>A task representing asynchronous connection release.</returns>
    public ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _releaseAsync, null);
        return release is null ? ValueTask.CompletedTask : release(Client);
    }
}
