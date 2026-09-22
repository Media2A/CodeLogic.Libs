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

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null)
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
        var client = CreateClient(value, credential);
        return new GoogleCloudStorageBackend(
            connectionId,
            client,
            value.Bucket,
            value.Prefix,
            value.UploadChunkSizeBytes,
            maxBufferedDownloadBytes,
            ownsClient: true);
    }

    private static StorageClient CreateClient(GoogleCloudConnectionConfig value, GoogleCredential? credential)
    {
        var anonymous = value.AuthenticationMode == GoogleCloudAuthenticationMode.Anonymous;
        var proxy = value.Proxy?.ToWebProxy();
        if (string.IsNullOrWhiteSpace(value.ServiceUrl) && !anonymous && proxy is null)
            return credential is null ? StorageClient.Create() : StorageClient.Create(credential);
        var builder = new StorageClientBuilder
        {
            Credential = credential,
            UnauthenticatedAccess = anonymous
        };
        if (proxy is not null)
            builder.HttpClientFactory = Google.Apis.Http.HttpClientFactory.ForProxy(proxy);
        if (!string.IsNullOrWhiteSpace(value.ServiceUrl))
            builder.BaseUri = JsonApiBase(value.ServiceUrl);
        return builder.Build();
    }

    /// <summary>Normalizes an endpoint to the JSON API root the client expects (<c>.../storage/v1/</c>).</summary>
    internal static string JsonApiBase(string serviceUrl)
    {
        var trimmed = serviceUrl.TrimEnd('/');
        return trimmed.EndsWith("/storage/v1", StringComparison.OrdinalIgnoreCase) ? trimmed + "/" : trimmed + "/storage/v1/";
    }
}
