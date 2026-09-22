using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Skips unless the Digest and HTTPS WebDAV servers are configured.</summary>
public sealed class WebDavOptionsFactAttribute : FactAttribute
{
    public WebDavOptionsFactAttribute()
    {
        if (LiveServers.Env("CL_STORAGE_TEST_WEBDAV_DIGEST_URL") is null || LiveServers.Env("CL_STORAGE_TEST_WEBDAV_TLS_URL") is null)
            Skip = "Set CL_STORAGE_TEST_WEBDAV_DIGEST_URL and CL_STORAGE_TEST_WEBDAV_TLS_URL to run WebDAV option tests.";
    }
}

/// <summary>Live coverage of WebDAV Digest authentication, TLS pinning, and connection limits.</summary>
public sealed class WebDavOptionsLiveTests
{
    private static WebDavConnectionConfig Digest(Action<WebDavConnectionConfig>? configure = null) => LiveServers.WebDav(c =>
    {
        c.Endpoint = LiveServers.Env("CL_STORAGE_TEST_WEBDAV_DIGEST_URL")!;
        c.AuthenticationMode = WebDavAuthenticationMode.Digest;
        c.Retry.RetryCount = 0;
        configure?.Invoke(c);
    });

    private static WebDavConnectionConfig Tls(Action<WebDavConnectionConfig>? configure = null) => LiveServers.WebDav(c =>
    {
        c.Endpoint = LiveServers.Env("CL_STORAGE_TEST_WEBDAV_TLS_URL")!;
        c.AllowInsecureHttp = false;
        c.Retry.RetryCount = 0;
        configure?.Invoke(c);
    });

    [WebDavOptionsFact]
    public async Task Digest_authentication_round_trips()
    {
        await using var storage = LiveServers.Create(Digest());
        await StorageContract.RoundTripAsync(storage);
    }

    [WebDavOptionsFact]
    public async Task Wrong_digest_password_is_an_authentication_failure()
    {
        await using var storage = LiveServers.Create(Digest(c => c.Password = "wrong"));
        await StorageContract.WrongCredentialsAreAuthenticationFailuresAsync(storage);
    }

    [WebDavOptionsFact]
    public async Task Untrusted_certificate_is_a_tls_failure()
    {
        await using var storage = LiveServers.Create(Tls());
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
    }

    [WebDavOptionsFact]
    public async Task Pinned_certificate_round_trips_over_https()
    {
        var certificate = await CaptureCertificateAsync();
        await using var storage = LiveServers.Create(Tls(c => c.TrustedCertificateSha256 = [Convert.ToHexString(SHA256.HashData(certificate.RawData))]));
        await StorageContract.RoundTripAsync(storage);
    }

    [WebDavOptionsFact]
    public async Task Pinned_public_key_is_trusted()
    {
        var certificate = await CaptureCertificateAsync();
        var spki = Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        await using var storage = LiveServers.Create(Tls(c => c.TrustedPublicKeySha256 = [spki]));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [WebDavOptionsFact]
    public async Task Wrong_pin_is_a_tls_failure()
    {
        await using var storage = LiveServers.Create(Tls(c => c.TrustedPublicKeySha256 = [new string('B', 64)]));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
    }

    [WebDavOptionsFact]
    public async Task Pin_with_required_chain_rejects_an_untrusted_issuer()
    {
        var certificate = await CaptureCertificateAsync();
        await using var storage = LiveServers.Create(Tls(c =>
        {
            c.TrustedCertificateSha256 = [Convert.ToHexString(SHA256.HashData(certificate.RawData))];
            c.RequireValidCertificateChain = true;
        }));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
    }

    [WebDavOptionsFact]
    public async Task Single_connection_limit_still_completes_a_round_trip()
    {
        await using var storage = LiveServers.Create(Digest(c => c.MaxConnectionsPerServer = 1));
        await StorageContract.RoundTripAsync(storage);
    }

    private static async Task<X509Certificate2> CaptureCertificateAsync()
    {
        var endpoint = new Uri(LiveServers.Env("CL_STORAGE_TEST_WEBDAV_TLS_URL")!);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint.Host, endpoint.Port);
        X509Certificate2? captured = null;
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            captured = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
            return true;
        });
        await tls.AuthenticateAsClientAsync(endpoint.Host);
        return captured!;
    }
}
