using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;

namespace CL.Storage.Providers.Swift;

internal sealed class SwiftStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(SwiftConnectionConfig);
    public StorageProvider Provider => StorageProvider.OpenStackSwift;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (SwiftConnectionConfig)configuration;
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(value.TimeoutSeconds) };
        return new SwiftStorageBackend(connectionId, client, value, ownsClient: true, maxBufferedDownloadBytes);
    }
}
