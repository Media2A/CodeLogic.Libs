using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace CL.Storage.Providers.GoogleCloud;

internal sealed class GoogleCloudStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(GoogleCloudConnectionConfig);
    public StorageProvider Provider => StorageProvider.GoogleCloudStorage;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (GoogleCloudConnectionConfig)configuration;
        GoogleCredential? credential = value.AuthenticationMode switch
        {
            GoogleCloudAuthenticationMode.ServiceAccountFile =>
                CredentialFactory.FromFile<ServiceAccountCredential>(value.CredentialsJsonPath!).ToGoogleCredential(),
            GoogleCloudAuthenticationMode.ServiceAccountJson =>
                CredentialFactory.FromJson<ServiceAccountCredential>(value.CredentialsJson!).ToGoogleCredential(),
            _ => null
        };
        var client = credential is null ? StorageClient.Create() : StorageClient.Create(credential);
        return new GoogleCloudStorageBackend(
            connectionId,
            client,
            value.Bucket,
            value.Prefix,
            value.UploadChunkSizeBytes,
            maxBufferedDownloadBytes,
            ownsClient: true);
    }
}
