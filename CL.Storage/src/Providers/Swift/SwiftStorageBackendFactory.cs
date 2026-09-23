using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;

namespace CL.Storage.Providers.Swift;

internal sealed class SwiftStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(SwiftConnectionConfig);
    public StorageProvider Provider => StorageProvider.OpenStackSwift;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null)
    {
        var value = (SwiftConnectionConfig)configuration;
        var proxy = value.Proxy?.ToWebProxy();
        var handler = new SocketsHttpHandler { Proxy = proxy, UseProxy = proxy is not null };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(value.TimeoutSeconds) };
        return new SwiftStorageBackend(connectionId, client, value, ownsClient: true, maxBufferedDownloadBytes);
    }
}
