using Azure.Core;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;

namespace CL.Storage.Providers.Azure;

internal sealed class AzureBlobStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(AzureBlobConnectionConfig);
    public StorageProvider Provider => StorageProvider.AzureBlob;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (AzureBlobConnectionConfig)configuration;
        var options = new BlobClientOptions
        {
            Retry =
            {
                MaxRetries = value.MaxRetries,
                NetworkTimeout = TimeSpan.FromSeconds(value.TimeoutSeconds)
            }
        };
        BlobContainerClient client = value.AuthenticationMode switch
        {
            AzureBlobAuthenticationMode.ConnectionString => new BlobContainerClient(value.ConnectionString!, value.Container, options),
            AzureBlobAuthenticationMode.SharedKey => new BlobContainerClient(
                ContainerUri(value), new StorageSharedKeyCredential(value.AccountName!, value.AccountKey!), options),
            AzureBlobAuthenticationMode.SasToken => new BlobContainerClient(
                AppendSas(ContainerUri(value), value.SasToken!), options),
            _ => new BlobContainerClient(ContainerUri(value), (TokenCredential)new DefaultAzureCredential(), options)
        };
        return new AzureBlobStorageBackend(connectionId, client, value.Prefix, maxBufferedDownloadBytes);
    }

    private static Uri ContainerUri(AzureBlobConnectionConfig value) =>
        new($"{value.ServiceUri!.TrimEnd('/')}/{Uri.EscapeDataString(value.Container)}", UriKind.Absolute);

    private static Uri AppendSas(Uri containerUri, string sasToken)
    {
        var builder = new UriBuilder(containerUri) { Query = sasToken.TrimStart('?') };
        return builder.Uri;
    }
}
