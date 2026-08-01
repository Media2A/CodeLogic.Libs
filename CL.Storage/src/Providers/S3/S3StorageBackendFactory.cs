using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;

namespace CL.Storage.Providers.S3;

internal sealed class S3StorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(S3ConnectionConfig);
    public StorageProvider Provider => StorageProvider.S3;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (S3ConnectionConfig)configuration;
        var clientConfig = new AmazonS3Config
        {
            ForcePathStyle = value.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(value.TimeoutSeconds),
            MaxErrorRetry = value.MaxRetries
        };
        if (!string.IsNullOrWhiteSpace(value.ServiceUrl))
        {
            clientConfig.ServiceURL = value.ServiceUrl;
            clientConfig.AuthenticationRegion = value.Region;
        }
        else
        {
            clientConfig.RegionEndpoint = RegionEndpoint.GetBySystemName(value.Region);
        }

        AWSCredentials? credentials = value.AuthenticationMode switch
        {
            S3AuthenticationMode.StaticCredentials when !string.IsNullOrWhiteSpace(value.SessionToken) =>
                new SessionAWSCredentials(value.AccessKey!, value.SecretKey!, value.SessionToken),
            S3AuthenticationMode.StaticCredentials => new BasicAWSCredentials(value.AccessKey!, value.SecretKey!),
            _ => null
        };
        IAmazonS3 client = credentials is null ? new AmazonS3Client(clientConfig) : new AmazonS3Client(credentials, clientConfig);
        return new S3StorageBackend(
            connectionId,
            client,
            value.Bucket,
            value.Prefix,
            ownsClient: true,
            maxBufferedDownloadBytes,
            value.DisablePayloadSigning,
            value.DisableDefaultChecksumValidation);
    }
}
