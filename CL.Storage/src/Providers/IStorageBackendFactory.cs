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
