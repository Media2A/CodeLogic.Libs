namespace CL.Storage.Abstractions;

/// <summary>Owns a native session and invokes its release callback at most once.</summary>
public sealed class NativeConnectionLease<TClient> : IAsyncDisposable where TClient : class
{
    private Func<TClient, ValueTask>? _releaseAsync;

    public NativeConnectionLease(TClient client, Func<TClient, ValueTask> releaseAsync)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _releaseAsync = releaseAsync ?? throw new ArgumentNullException(nameof(releaseAsync));
    }

    public TClient Client { get; }

    public ValueTask DisposeAsync()
    {
        var release = Interlocked.Exchange(ref _releaseAsync, null);
        return release is null ? ValueTask.CompletedTask : release(Client);
    }
}
