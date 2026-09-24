using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Providers.Local;

namespace CL.Storage.Providers;

internal interface IStorageBackendFactory
{
    Type ConfigurationType { get; }
    StorageProvider Provider { get; }
    IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null);
}

/// <summary>
/// A factory whose backends share a session pool with every registration of the same settings; it can also build a
/// backend with a pool of its own, used by a connection test so it never uses or disturbs a live connection's sessions.
/// </summary>
internal interface IIsolatedStorageBackendFactory
{
    /// <summary>Creates a backend whose pool is its own, disposed with the backend.</summary>
    IStorageBackend CreateIsolated(string connectionId, object configuration, long maxBufferedDownloadBytes);
}

internal sealed class LocalStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(LocalConnectionConfig);
    public StorageProvider Provider => StorageProvider.Local;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null) =>
        new LocalStorageBackend(connectionId, (LocalConnectionConfig)configuration, maxBufferedDownloadBytes)
        {
            ListingScope = ProviderSettingsKey.For(configuration)
        };
}
