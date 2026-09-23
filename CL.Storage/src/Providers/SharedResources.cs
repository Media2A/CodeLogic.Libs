using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CL.Storage.Providers;

/// <summary>A stable, secret-free identity for a connection's settings: equal settings give equal keys.</summary>
internal static class ProviderSettingsKey
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>SHA-256 over the settings' type and JSON, so credentials never appear in the key.</summary>
    public static string For(object settings)
    {
        var json = JsonSerializer.Serialize(settings, settings.GetType(), Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.GetType().FullName}\n{json}")));
    }
}

/// <summary>A pool a backend uses, either its own or one shared by every backend with the same settings.</summary>
internal interface IPoolHandle<TClient> where TClient : class
{
    ProviderClientPool<TClient> Pool { get; }

    /// <summary>Gives the pool back: an owned pool is disposed, a shared one when its last user has lingered out.</summary>
    ValueTask ReleaseAsync();
}

/// <summary>A pool owned by one backend.</summary>
internal sealed class OwnedPool<TClient>(ProviderClientPool<TClient> pool) : IPoolHandle<TClient> where TClient : class
{
    public ProviderClientPool<TClient> Pool { get; } = pool;
    public ValueTask ReleaseAsync() => Pool.DisposeAsync();
}

/// <summary>
/// Resources shared by backends with identical settings — session pools and what their clients report —
/// so replacing a registration with the same settings keeps its warm sessions. A resource lingers for a
/// while after its last user releases it, then is disposed unless a new user arrives first.
/// </summary>
internal static class SharedResources
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Holder> Resources = new(StringComparer.Ordinal);

    private sealed class Holder(object value, Func<object, ValueTask> dispose)
    {
        public object Value { get; } = value;
        public Func<object, ValueTask> Dispose { get; } = dispose;
        public int Users { get; set; }
        public long Generation { get; set; }
        /// <summary>Cancels the linger of a resource nobody uses, so it is disposed at once.</summary>
        public CancellationTokenSource? Linger { get; set; }
    }

    /// <summary>Returns the shared resource for a key, creating it for the first user.</summary>
    public static T Acquire<T>(string key, Func<T> create, Func<T, ValueTask> dispose) where T : class
    {
        lock (Gate)
        {
            if (!Resources.TryGetValue(key, out var holder) || holder.Value is not T)
            {
                holder = new Holder(create(), value => dispose((T)value));
                Resources[key] = holder;
            }
            holder.Users++;
            holder.Generation++;
            holder.Linger?.Cancel();
            holder.Linger = null;
            return (T)holder.Value;
        }
    }

    /// <summary>Releases one use; the resource is disposed after <paramref name="linger"/> if nobody took it again.</summary>
    public static async ValueTask ReleaseAsync(string key, TimeSpan linger)
    {
        long generation;
        Holder released;
        CancellationTokenSource? lingering = null;
        lock (Gate)
        {
            if (!Resources.TryGetValue(key, out released!)) return;
            released.Users--;
            if (released.Users > 0) return;
            generation = released.Generation;
            if (linger > TimeSpan.Zero)
                released.Linger = lingering = new CancellationTokenSource();
        }
        if (lingering is not null)
        {
            // Cut short by a new user (which keeps the resource) or by FlushIdleAsync (which disposes it).
            try { await Task.Delay(linger, lingering.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { lingering.Dispose(); }
        }
        Holder? retired = null;
        lock (Gate)
        {
            // The same holder, still unused since this release: a newer one under the key is left alone.
            if (Resources.TryGetValue(key, out var holder) && ReferenceEquals(holder, released) && holder.Users == 0 && holder.Generation == generation)
            {
                Resources.Remove(key);
                retired = holder;
            }
        }
        if (retired is not null) await retired.Dispose(retired.Value).ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes every resource no one is using, instead of letting it linger; called when a storage library
    /// stops, so its idle sessions do not outlive it. Resources other libraries still use are kept.
    /// </summary>
    public static async ValueTask FlushIdleAsync()
    {
        Holder[] idle;
        lock (Gate)
        {
            idle = [.. Resources.Values.Where(holder => holder.Users == 0)];
            foreach (var key in Resources.Where(pair => pair.Value.Users == 0).Select(pair => pair.Key).ToList())
                Resources.Remove(key);
            foreach (var holder in idle) holder.Linger?.Cancel();
        }
        foreach (var holder in idle)
        {
            try { await holder.Dispose(holder.Value).ConfigureAwait(false); }
            catch (Exception) { /* Closing an idle session is best effort. */ }
        }
    }

    /// <summary>How many resources are alive, for tests.</summary>
    internal static int Count { get { lock (Gate) return Resources.Count; } }
}

/// <summary>A session pool and the identity recorder its clients report to, shared per settings key.</summary>
internal sealed class SharedPool<TClient>(ProviderClientPool<TClient> pool, ServerIdentityRecorder identity) where TClient : class
{
    public ProviderClientPool<TClient> Pool { get; } = pool;
    public ServerIdentityRecorder Identity { get; } = identity;
    /// <summary>A resource the pool's sessions use (a client certificate), disposed with the pool.</summary>
    public IDisposable? Owned { get; init; }
}

/// <summary>A backend's use of a <see cref="SharedPool{TClient}"/>.</summary>
internal sealed class SharedPoolHandle<TClient>(string key, SharedPool<TClient> shared, TimeSpan linger) : IPoolHandle<TClient> where TClient : class
{
    private int _released;

    public ProviderClientPool<TClient> Pool => shared.Pool;

    public ValueTask ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return ValueTask.CompletedTask;
        // Without lingering the last user closes the pool before returning; lingering runs in the background
        // so disposing a backend never waits for it.
        if (linger <= TimeSpan.Zero) return SharedResources.ReleaseAsync(key, TimeSpan.Zero);
        _ = SharedResources.ReleaseAsync(key, linger).AsTask();
        return ValueTask.CompletedTask;
    }
}
