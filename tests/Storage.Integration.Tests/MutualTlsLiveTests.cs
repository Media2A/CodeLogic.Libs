using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Providers;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Skips unless the mutual-TLS WebDAV server (TLS 1.3 only, client certificate required) is configured.</summary>
public sealed class MutualTlsFactAttribute : FactAttribute
{
    public MutualTlsFactAttribute()
    {
        if (LiveServers.Env("CL_STORAGE_TEST_WEBDAV_MTLS_URL") is null)
            Skip = "Set CL_STORAGE_TEST_WEBDAV_MTLS_URL to run mutual-TLS tests.";
    }
}

/// <summary>
/// Live mutual TLS: a client certificate held only in memory, and the reasons a handshake fails, as the
/// platform's own TLS stack reports them (SChannel on Windows, OpenSSL on Linux).
/// </summary>
public sealed class MutualTlsLiveTests
{
    private static readonly Uri Endpoint = new(LiveServers.Env("CL_STORAGE_TEST_WEBDAV_MTLS_URL") ?? "https://localhost:8444/");

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "mtls", name));

    private static WebDavConnectionConfig Config(string pin, byte[]? clientCertificate) => LiveServers.WebDav(c =>
    {
        c.Endpoint = Endpoint.ToString();
        c.AllowInsecureHttp = false;
        c.TrustedPublicKeySha256 = [pin];
        c.ClientCertificateContent = clientCertificate;
        c.ClientCertificatePassword = clientCertificate is null ? null : "cltest";
        c.Retry.RetryCount = 0;
    });

    /// <summary>
    /// The server's public-key pin. It is read without presenting a client certificate: a handshake that did
    /// present one would leave a TLS session the platform could resume, and a later test would then pass without
    /// presenting its own certificate at all.
    /// </summary>
    private static async Task<string> ServerPinAsync()
    {
        string? pin = null;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            using var parsed = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
            pin = Convert.ToHexString(SHA256.HashData(parsed.PublicKey.ExportSubjectPublicKeyInfo()));
            return true;
        });
        try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Endpoint.Host }); }
        catch (Exception) when (pin is not null) { /* Refused for lack of a client certificate; the pin was read. */ }
        return pin!;
    }

    [MutualTlsFact]
    public async Task A_client_certificate_held_only_in_memory_authenticates()
    {
        await using var storage = LiveServers.Create(Config(await ServerPinAsync(), Fixture("client.pfx")));

        await StorageContract.RoundTripAsync(storage);
    }

    [MutualTlsFact]
    public async Task A_missing_client_certificate_is_a_credential_problem()
    {
        await using var storage = LiveServers.Create(Config(await ServerPinAsync(), clientCertificate: null));

        var health = await storage.CheckHealthAsync();

        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(health.Error!, StorageErrorInfo.TlsReasonKey, out var reason), health.Error!.ToString());
        Assert.Equal(TlsDiagnosis.ClientCertificateRejected, reason);
    }

    [MutualTlsFact]
    public async Task A_client_certificate_from_an_untrusted_issuer_is_a_credential_problem()
    {
        await using var storage = LiveServers.Create(Config(await ServerPinAsync(), Fixture("stranger.pfx")));

        var health = await storage.CheckHealthAsync();

        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(health.Error!, StorageErrorInfo.TlsReasonKey, out var reason), health.Error!.ToString());
        Assert.Equal(TlsDiagnosis.ClientCertificateRejected, reason);
    }

    [MutualTlsFact]
    public async Task A_protocol_the_server_does_not_offer_is_a_protocol_mismatch()
    {
        // The server accepts TLS 1.3 only; the platform's own error for a TLS 1.2 client is classified.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = Endpoint.Host,
            EnabledSslProtocols = SslProtocols.Tls12
        }));

        Assert.True(error is AuthenticationException or IOException, error.ToString());
        Assert.Equal(TlsDiagnosis.ProtocolMismatch, TlsDiagnosis.Reason(error));
    }
}
