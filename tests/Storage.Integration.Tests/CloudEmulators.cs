using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.GoogleCloud;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.Swift;
using Google.Cloud.Storage.V1;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>
/// Settings and bucket/container bootstrap for local object-store emulators (MinIO, Azurite,
/// fake-gcs-server, Swift SAIO), so the cloud backends are exercised without any cloud account.
/// </summary>
internal static class CloudEmulators
{
    private static string? Env(string name) => LiveServers.Env(name);

    public static bool S3Configured => Env("CL_STORAGE_TEST_S3_URL") is not null;
    public static bool AzureConfigured => Env("CL_STORAGE_TEST_AZURE_CONNECTION_STRING") is not null;
    public static bool GcsConfigured => Env("CL_STORAGE_TEST_GCS_URL") is not null;
    public static bool SwiftConfigured => Env("CL_STORAGE_TEST_SWIFT_AUTH_URL") is not null;

    public static S3ConnectionConfig S3(Action<S3ConnectionConfig>? configure = null)
    {
        var url = Env("CL_STORAGE_TEST_S3_URL")!;
        var config = new S3ConnectionConfig
        {
            Bucket = Env("CL_STORAGE_TEST_S3_BUCKET") ?? "cl-test",
            ServiceUrl = url,
            Region = "us-east-1",
            ForcePathStyle = true,
            AllowInsecureHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            AuthenticationMode = S3AuthenticationMode.StaticCredentials,
            AccessKey = Env("CL_STORAGE_TEST_S3_ACCESSKEY"),
            SecretKey = Env("CL_STORAGE_TEST_S3_SECRETKEY"),
            MaxRetries = 0,
            TimeoutSeconds = 15
        };
        configure?.Invoke(config);
        return config;
    }

    public static AzureBlobConnectionConfig Azure(Action<AzureBlobConnectionConfig>? configure = null)
    {
        var config = new AzureBlobConnectionConfig
        {
            Container = Env("CL_STORAGE_TEST_AZURE_CONTAINER") ?? "cl-test",
            AuthenticationMode = AzureBlobAuthenticationMode.ConnectionString,
            ConnectionString = Env("CL_STORAGE_TEST_AZURE_CONNECTION_STRING"),
            MaxRetries = 0,
            TimeoutSeconds = 15
        };
        configure?.Invoke(config);
        return config;
    }

    public static GoogleCloudConnectionConfig Gcs(Action<GoogleCloudConnectionConfig>? configure = null)
    {
        var url = Env("CL_STORAGE_TEST_GCS_URL")!;
        var config = new GoogleCloudConnectionConfig
        {
            Bucket = Env("CL_STORAGE_TEST_GCS_BUCKET") ?? "cl-test",
            ServiceUrl = url,
            AllowInsecureHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            AuthenticationMode = GoogleCloudAuthenticationMode.Anonymous
        };
        configure?.Invoke(config);
        return config;
    }

    public static SwiftConnectionConfig Swift(Action<SwiftConnectionConfig>? configure = null)
    {
        var url = Env("CL_STORAGE_TEST_SWIFT_AUTH_URL")!;
        var config = new SwiftConnectionConfig
        {
            Container = Env("CL_STORAGE_TEST_SWIFT_CONTAINER") ?? "cl-test",
            AuthenticationMode = SwiftAuthenticationMode.TempAuthV1,
            AuthenticationUrl = url,
            Username = Env("CL_STORAGE_TEST_SWIFT_USER"),
            Password = Env("CL_STORAGE_TEST_SWIFT_KEY"),
            AllowInsecureHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            TimeoutSeconds = 15
        };
        configure?.Invoke(config);
        return config;
    }

    public static async Task<IStorageBackend> CreateAsync(StorageConnectionConfigBase config)
    {
        var validation = config.Validate();
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        switch (config)
        {
            case S3ConnectionConfig s3:
                await EnsureS3BucketAsync(s3);
                return new S3StorageBackendFactory().Create("emu-s3", s3, 64L << 20);
            case AzureBlobConnectionConfig azure:
                await new BlobContainerClient(azure.ConnectionString, azure.Container).CreateIfNotExistsAsync();
                return new AzureBlobStorageBackendFactory().Create("emu-azure", azure, 64L << 20);
            case GoogleCloudConnectionConfig gcs:
                await EnsureGcsBucketAsync(gcs);
                return new GoogleCloudStorageBackendFactory().Create("emu-gcs", gcs, 64L << 20);
            case SwiftConnectionConfig swift:
                await EnsureSwiftContainerAsync(swift);
                return new SwiftStorageBackendFactory().Create("emu-swift", swift, 64L << 20);
            default:
                throw new ArgumentOutOfRangeException(nameof(config));
        }
    }

    private static async Task EnsureS3BucketAsync(S3ConnectionConfig config)
    {
        using var client = new AmazonS3Client(
            config.AccessKey,
            config.SecretKey,
            new AmazonS3Config { ServiceURL = config.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = config.Region });
        try { await client.PutBucketAsync(new PutBucketRequest { BucketName = config.Bucket }); }
        catch (AmazonS3Exception error) when (error.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
    }

    private static async Task EnsureGcsBucketAsync(GoogleCloudConnectionConfig config)
    {
        var client = new StorageClientBuilder
        {
            BaseUri = GoogleCloudStorageBackendFactory.JsonApiBase(config.ServiceUrl!),
            UnauthenticatedAccess = true
        }.Build();
        try { await client.CreateBucketAsync("cl-test-project", config.Bucket); }
        catch (Google.GoogleApiException error) when (error.HttpStatusCode == HttpStatusCode.Conflict) { }
    }

    private static async Task EnsureSwiftContainerAsync(SwiftConnectionConfig config)
    {
        using var http = new HttpClient();
        using var auth = new HttpRequestMessage(HttpMethod.Get, config.AuthenticationUrl);
        auth.Headers.Add("X-Auth-User", config.Username);
        auth.Headers.Add("X-Auth-Key", config.Password);
        using var authResponse = await http.SendAsync(auth);
        authResponse.EnsureSuccessStatusCode();
        var storageUrl = authResponse.Headers.GetValues("X-Storage-Url").First();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{storageUrl.TrimEnd('/')}/{config.Container}");
        put.Headers.Add("X-Auth-Token", authResponse.Headers.GetValues("X-Auth-Token").First());
        using var putResponse = await http.SendAsync(put);
        putResponse.EnsureSuccessStatusCode();
    }
}

/// <summary>Skips unless an S3-compatible endpoint is configured.</summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute() { if (!CloudEmulators.S3Configured) Skip = "Set CL_STORAGE_TEST_S3_* to run S3 tests."; }
}

/// <summary>Skips unless an Azure Blob endpoint is configured.</summary>
public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute() { if (!CloudEmulators.AzureConfigured) Skip = "Set CL_STORAGE_TEST_AZURE_* to run Azure Blob tests."; }
}

/// <summary>Skips unless a GCS endpoint is configured.</summary>
public sealed class GcsFactAttribute : FactAttribute
{
    public GcsFactAttribute() { if (!CloudEmulators.GcsConfigured) Skip = "Set CL_STORAGE_TEST_GCS_* to run Google Cloud Storage tests."; }
}

/// <summary>Skips unless a Swift endpoint is configured.</summary>
public sealed class SwiftFactAttribute : FactAttribute
{
    public SwiftFactAttribute() { if (!CloudEmulators.SwiftConfigured) Skip = "Set CL_STORAGE_TEST_SWIFT_* to run Swift tests."; }
}

public sealed class S3EmulatorTests
{
    [S3Fact]
    public async Task Round_trip_through_an_s3_compatible_server()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.S3());
        await StorageContract.RoundTripAsync(storage);
    }

    [S3Fact]
    public async Task Conflict_policies_apply()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.S3());
        await StorageContract.ConflictPoliciesAsync(storage);
    }

    [S3Fact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.S3());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }

    [S3Fact]
    public async Task Wrong_secret_is_an_authentication_failure()
    {
        await CloudEmulators.CreateAsync(CloudEmulators.S3());
        await using var storage = new S3StorageBackendFactory().Create("emu-s3-bad", CloudEmulators.S3(c => c.SecretKey = "wrong-secret"), 1 << 20);
        await StorageContract.WrongCredentialsAreAuthenticationFailuresAsync(storage);
    }
}

public sealed class AzureEmulatorTests
{
    [AzureFact]
    public async Task Round_trip_through_azurite()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Azure());
        await StorageContract.RoundTripAsync(storage);
    }

    [AzureFact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Azure());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }

    [AzureFact]
    public async Task Wrong_account_key_is_an_authentication_failure()
    {
        await CloudEmulators.CreateAsync(CloudEmulators.Azure());
        var endpoint = new BlobServiceClient(CloudEmulators.Azure().ConnectionString).Uri.ToString().TrimEnd('/');
        var connection = $"DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey={Convert.ToBase64String(new byte[64])};BlobEndpoint={endpoint};";
        await using var storage = new AzureBlobStorageBackendFactory().Create("emu-azure-bad", CloudEmulators.Azure(c => c.ConnectionString = connection), 1 << 20);

        var health = await storage.CheckHealthAsync();

        // Real Azure answers a bad key with 403 AuthenticationFailed; Azurite with 403 AuthorizationFailure.
        Assert.Contains(health.Error?.Code, new[] { StorageErrors.AuthenticationFailedCode, StorageErrors.PermissionDeniedCode });
        Assert.False(StorageErrorInfo.IsTransient(health.Error));
    }
}

public sealed class GcsEmulatorTests
{
    [GcsFact]
    public async Task Round_trip_through_fake_gcs_server()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Gcs());
        await StorageContract.RoundTripAsync(storage);
    }

    [GcsFact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Gcs());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }
}

public sealed class SwiftEmulatorTests
{
    [SwiftFact]
    public async Task Round_trip_through_swift_tempauth()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Swift());
        await StorageContract.RoundTripAsync(storage);
    }

    [SwiftFact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Swift());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }

    [SwiftFact]
    public async Task Wrong_key_is_an_authentication_failure()
    {
        await using var storage = new SwiftStorageBackendFactory().Create("emu-swift-bad", CloudEmulators.Swift(c => c.Password = "wrong"), 1 << 20);
        await StorageContract.WrongCredentialsAreAuthenticationFailuresAsync(storage);
    }
}

public sealed class SwiftEtagTests
{
    [SwiftFact]
    public async Task Swift_items_carry_their_etag_so_conditional_deletes_work()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Swift());
        var path = $"etag-{Guid.NewGuid():N}.txt";
        await storage.UploadBytesAsync(path, [1, 2, 3]);

        var info = await storage.GetInfoAsync(path);
        Assert.False(string.IsNullOrEmpty(info.Value!.ETag));

        var stale = await storage.DeleteAsync(path, new CL.Storage.Models.StorageDeleteOptions { Condition = new CL.Storage.Models.StorageMutationCondition { ExpectedETag = "0123" } });
        Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
        var current = await storage.DeleteAsync(path, new CL.Storage.Models.StorageDeleteOptions { Condition = new CL.Storage.Models.StorageMutationCondition { ExpectedETag = info.Value.ETag } });
        Assert.True(current.IsSuccess, current.Error?.ToString());
    }
}
