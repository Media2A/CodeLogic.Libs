using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using FluentFTP;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Skips unless a live FTPS server (TLS required) is configured.</summary>
public sealed class FtpsFactAttribute : FactAttribute
{
    public FtpsFactAttribute()
    {
        if (LiveServers.Env("CL_STORAGE_TEST_FTPS_HOST") is null)
            Skip = "Set CL_STORAGE_TEST_FTPS_* to run live FTPS tests.";
    }
}

/// <summary>Live FTPS coverage: certificate and public-key pins, TLS versions, and TLS failures.</summary>
public sealed class FtpsLiveTests
{
    private static FtpConnectionConfig Ftps(Action<FtpConnectionConfig>? configure = null)
    {
        var config = new FtpConnectionConfig
        {
            Host = LiveServers.Env("CL_STORAGE_TEST_FTPS_HOST")!,
            Port = int.Parse(LiveServers.Env("CL_STORAGE_TEST_FTPS_PORT") ?? "21"),
            Username = LiveServers.Env("CL_STORAGE_TEST_FTPS_USER")!,
            Password = LiveServers.Env("CL_STORAGE_TEST_FTPS_PASS")!,
            EncryptionMode = StorageFtpEncryptionMode.Explicit,
            TimeoutSeconds = 15,
            Retry = new StorageRetryConfig { RetryCount = 0 }
        };
        configure?.Invoke(config);
        return config;
    }

    [FtpsFact]
    public async Task Pinned_certificate_round_trips_over_tls()
    {
        var certificate = await CaptureCertificateAsync();
        await using var storage = LiveServers.Create(Ftps(c => c.TrustedCertificateSha256 = [Convert.ToHexString(SHA256.HashData(certificate.RawData))]));
        await StorageContract.RoundTripAsync(storage);
    }

    [FtpsFact]
    public async Task Pinned_public_key_is_trusted()
    {
        var certificate = await CaptureCertificateAsync();
        var spki = "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
        await using var storage = LiveServers.Create(Ftps(c => c.TrustedPublicKeySha256 = [spki]));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [FtpsFact]
    public async Task Self_signed_certificate_without_a_pin_is_a_tls_failure()
    {
        await using var storage = LiveServers.Create(Ftps());
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
    }

    [FtpsFact]
    public async Task Wrong_pin_is_a_tls_failure()
    {
        await using var storage = LiveServers.Create(Ftps(c => c.TrustedCertificateSha256 = [new string('A', 64)]));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
    }

    [FtpsFact]
    public async Task Pin_with_required_chain_rejects_a_self_signed_certificate()
    {
        var certificate = await CaptureCertificateAsync();
        await using var storage = LiveServers.Create(Ftps(c =>
        {
            c.TrustedCertificateSha256 = [Convert.ToHexString(SHA256.HashData(certificate.RawData))];
            c.RequireValidCertificateChain = true;
        }));
        var health = await storage.CheckHealthAsync();
        Assert.Equal(StorageErrors.TlsFailureCode, health.Error?.Code);
    }

    [FtpsFact]
    public async Task Tls12_only_connects()
    {
        var certificate = await CaptureCertificateAsync();
        await using var storage = LiveServers.Create(Ftps(c =>
        {
            c.TrustedCertificateSha256 = [Convert.ToHexString(SHA256.HashData(certificate.RawData))];
            c.TlsProtocols = [SslProtocols.Tls12];
        }));
        var health = await storage.CheckHealthAsync();
        Assert.True(health.IsSuccess, health.Error?.ToString());
    }

    [FtpsFact]
    public async Task Plain_ftp_against_a_tls_only_server_is_refused()
    {
        await using var storage = LiveServers.Create(Ftps(c => c.EncryptionMode = StorageFtpEncryptionMode.None));
        var health = await storage.CheckHealthAsync();
        Assert.NotNull(health.Error);
        Assert.False(StorageErrorInfo.IsTransient(health.Error));
    }

    [FtpsFact]
    public async Task Login_commands_run_after_connect_and_a_rejected_one_fails_the_connection()
    {
        var certificate = await CaptureCertificateAsync();
        var pin = Convert.ToHexString(SHA256.HashData(certificate.RawData));

        await using (var accepted = LiveServers.Create(Ftps(c => { c.TrustedCertificateSha256 = [pin]; c.LoginCommands = ["NOOP", "TYPE I"]; })))
            Assert.True((await accepted.CheckHealthAsync()).IsSuccess);

        await using var rejected = LiveServers.Create(Ftps(c => { c.TrustedCertificateSha256 = [pin]; c.LoginCommands = ["SITE NOSUCHCOMMAND"]; }));
        var health = await rejected.CheckHealthAsync();
        Assert.NotNull(health.Error);
        Assert.True(StorageErrorInfo.TryGetDetail(health.Error, StorageErrorInfo.FtpReplyKey, out var reply), health.Error!.ToString());
        Assert.StartsWith("5", reply);
    }

    private static async Task<X509Certificate2> CaptureCertificateAsync()
    {
        var config = Ftps();
        await using var client = new AsyncFtpClient(config.Host, new NetworkCredential(config.Username, config.Password), config.Port,
            new FtpConfig { EncryptionMode = FtpEncryptionMode.Explicit });
        X509Certificate2? captured = null;
        client.ValidateCertificate += (_, e) =>
        {
            captured = X509CertificateLoader.LoadCertificate(e.Certificate.GetRawCertData());
            e.Accept = true;
        };
        await client.Connect();
        await client.Disconnect();
        return captured!;
    }
}
