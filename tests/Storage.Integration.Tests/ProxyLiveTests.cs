using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Skips unless the compose proxy and its internal servers are available.</summary>
public sealed class ProxyTheoryAttribute : TheoryAttribute
{
    public ProxyTheoryAttribute()
    {
        if (LiveServers.Env("CL_STORAGE_TEST_PROXY_HOST") is null)
            Skip = "Set CL_STORAGE_TEST_PROXY_HOST (and start the compose proxy) to run proxy tests.";
    }
}

/// <summary>
/// Routes every provider through HTTP and SOCKS5 proxies. Servers are addressed by compose
/// service names that only resolve inside the Docker network, so success proves the proxy was used.
/// </summary>
public sealed class ProxyLiveTests
{
    // SOCKS4 cannot resolve names on the proxy side: the client must resolve the destination itself,
    // and compose service names do not resolve from the host. SOCKS5 and HTTP resolve remotely.
    public static TheoryData<StorageProxyType> ProxyTypes => new()
    {
        StorageProxyType.Http,
        StorageProxyType.Socks5
    };

    private static StorageProxyConfig Proxy(StorageProxyType type) => new()
    {
        Type = type,
        Host = LiveServers.Env("CL_STORAGE_TEST_PROXY_HOST")!,
        Port = type switch
        {
            StorageProxyType.Http => int.Parse(LiveServers.Env("CL_STORAGE_TEST_PROXY_HTTP_PORT") ?? "3128"),
            StorageProxyType.Socks4 => int.Parse(LiveServers.Env("CL_STORAGE_TEST_PROXY_SOCKS4_PORT") ?? "1081"),
            _ => int.Parse(LiveServers.Env("CL_STORAGE_TEST_PROXY_SOCKS5_PORT") ?? "1080")
        }
    };

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task Sftp_through_proxy(StorageProxyType type)
    {
        await using var storage = LiveServers.Create(new SftpConnectionConfig
        {
            Host = "sftp",
            Port = 22,
            Username = "cltest",
            Password = "cltest-pw",
            Root = "upload",
            AutoAcceptHostKey = true,
            Proxy = Proxy(type)
        });
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task Ftp_through_proxy(StorageProxyType type)
    {
        await using var storage = LiveServers.Create(new FtpConnectionConfig
        {
            Host = "ftp-internal",
            Port = 21,
            Username = "cltest",
            Password = "cltest-pw",
            Root = "home/cltest",
            EncryptionMode = StorageFtpEncryptionMode.None,
            Proxy = Proxy(type)
        });
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task WebDav_through_proxy(StorageProxyType type)
    {
        await using var storage = LiveServers.Create(new WebDavConnectionConfig
        {
            Endpoint = "http://webdav/",
            AllowInsecureHttp = true,
            AuthenticationMode = WebDavAuthenticationMode.Basic,
            Username = "cltest",
            Password = "cltest-pw",
            Proxy = Proxy(type)
        });
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task S3_through_proxy(StorageProxyType type)
    {
        await CloudEmulators.CreateAsync(CloudEmulators.S3());
        await using var storage = new CL.Storage.Providers.S3.S3StorageBackendFactory().Create("proxy-s3", Valid(CloudEmulators.S3(c =>
        {
            c.ServiceUrl = "http://minio:9000";
            c.Proxy = Proxy(type);
        })), 1 << 20);
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task Azure_through_proxy(StorageProxyType type)
    {
        await CloudEmulators.CreateAsync(CloudEmulators.Azure());
        const string internalAzurite =
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
            "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
            "BlobEndpoint=http://azurite:10000/devstoreaccount1;";
        await using var storage = new CL.Storage.Providers.Azure.AzureBlobStorageBackendFactory().Create("proxy-azure", Valid(CloudEmulators.Azure(c =>
        {
            c.ConnectionString = internalAzurite;
            c.Proxy = Proxy(type);
        })), 1 << 20);
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task Gcs_through_proxy(StorageProxyType type)
    {
        await using var storage = new CL.Storage.Providers.GoogleCloud.GoogleCloudStorageBackendFactory().Create("proxy-gcs", Valid(new GoogleCloudConnectionConfig
        {
            Bucket = "cl-test",
            ServiceUrl = "http://gcs-internal:4443",
            AllowInsecureHttp = true,
            AuthenticationMode = GoogleCloudAuthenticationMode.Anonymous,
            Proxy = Proxy(type)
        }), 1 << 20);
        var nativeBucket = await storage.OpenNativeConnectionAsync<Google.Cloud.Storage.V1.StorageClient>();
        await using (nativeBucket.Value!)
        {
            try { await nativeBucket.Value!.Client.CreateBucketAsync("cl-test-project", "cl-test"); }
            catch (Google.GoogleApiException error) when (error.HttpStatusCode == System.Net.HttpStatusCode.Conflict) { }
        }
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [MemberData(nameof(ProxyTypes))]
    public async Task Swift_through_proxy(StorageProxyType type)
    {
        await CloudEmulators.CreateAsync(CloudEmulators.Swift());
        await using var storage = new CL.Storage.Providers.Swift.SwiftStorageBackendFactory().Create("proxy-swift", Valid(CloudEmulators.Swift(c =>
        {
            c.AuthenticationUrl = "http://swift:8080/auth/v1.0";
            c.Proxy = Proxy(type);
        })), 1 << 20);
        await WriteReadDeleteAsync(storage);
    }

    [ProxyTheory]
    [InlineData(StorageProxyType.Socks5)]
    public async Task Unreachable_proxy_is_a_connection_failure(StorageProxyType type)
    {
        await using var storage = LiveServers.Create(new SftpConnectionConfig
        {
            Host = "sftp",
            Username = "cltest",
            Password = "cltest-pw",
            AutoAcceptHostKey = true,
            Retry = new StorageRetryConfig { RetryCount = 0 },
            Proxy = new StorageProxyConfig { Type = type, Host = "127.0.0.1", Port = 1 }
        });

        var health = await storage.CheckHealthAsync();

        Assert.Equal(StorageErrors.ConnectionFailedCode, health.Error?.Code);
    }

    private static T Valid<T>(T config) where T : StorageConnectionConfigBase
    {
        var validation = config.Validate();
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        return config;
    }

    private static async Task WriteReadDeleteAsync(IStorageBackend storage)
    {
        var path = $"proxy-{Guid.NewGuid():N}/file.txt";
        var content = Encoding.UTF8.GetBytes("through the proxy");
        var upload = await storage.UploadBytesAsync(path, content);
        Assert.True(upload.IsSuccess, upload.Error?.ToString());
        var download = await storage.DownloadBytesAsync(path);
        Assert.True(download.IsSuccess, download.Error?.ToString());
        Assert.Equal(content, download.Value);
        var delete = await storage.DeleteAsync(path.Split('/')[0], new CL.Storage.Models.StorageDeleteOptions { Recursive = true });
        Assert.True(delete.IsSuccess, delete.Error?.ToString());
    }
}
