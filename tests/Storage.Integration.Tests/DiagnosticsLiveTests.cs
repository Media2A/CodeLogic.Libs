using CL.Storage;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>
/// Live coverage of connection tests and diagnostics, including the "the server presented this
/// fingerprint; trust it?" flow: a rejected certificate or host key is reported, then pinned.
/// </summary>
public sealed class DiagnosticsLiveTests
{
    private static readonly StorageLibrary Library = new();

    [FtpsFact]
    public async Task Ftps_rejected_certificate_is_reported_and_can_be_pinned()
    {
        FtpConnectionConfig Ftps(IReadOnlyList<string>? pins = null) => new()
        {
            Host = LiveServers.Env("CL_STORAGE_TEST_FTPS_HOST")!,
            Port = int.Parse(LiveServers.Env("CL_STORAGE_TEST_FTPS_PORT") ?? "21"),
            Username = LiveServers.Env("CL_STORAGE_TEST_FTPS_USER")!,
            Password = LiveServers.Env("CL_STORAGE_TEST_FTPS_PASS")!,
            EncryptionMode = StorageFtpEncryptionMode.Explicit,
            TrustedPublicKeySha256 = pins?.ToList() ?? [],
            TimeoutSeconds = 15,
            Retry = new StorageRetryConfig { RetryCount = 0 }
        };

        var rejected = await Library.TestConnectionAsync(Ftps());
        Assert.False(rejected.Succeeded);
        Assert.Equal(StorageErrors.TlsFailureCode, rejected.Error!.Code);
        var presented = Assert.IsType<StorageServerIdentity>(rejected.ServerIdentity);
        Assert.Equal(("tls-certificate", false), (presented.Kind, presented.Trusted));

        var pinned = await Library.TestConnectionAsync(Ftps([presented.PublicKeyFingerprint!]));
        Assert.True(pinned.Succeeded, pinned.Error?.ToString());
        var diagnostics = pinned.Diagnostics!;
        Assert.Equal(StorageTransportSecurity.Tls, diagnostics.Security);
        Assert.True(diagnostics.ServerIdentity!.Trusted);
        Assert.Equal(presented.Fingerprint, diagnostics.ServerIdentity.Fingerprint);
        Assert.StartsWith("Tls", diagnostics.Negotiated["tls"]);
        Assert.NotEmpty(diagnostics.ServerFeatures);
        Assert.NotNull(diagnostics.Pool);
    }

    [FtpFact]
    public async Task Ftp_server_system_and_software_are_reported()
    {
        var report = await Library.TestConnectionAsync(LiveServers.Ftp());

        Assert.True(report.Succeeded, report.Error?.ToString());
        Assert.False(string.IsNullOrWhiteSpace(report.Diagnostics!.ServerSystem));
        Assert.Contains(report.Diagnostics.ServerFeatures, feature => feature is "MDTM" or "SIZE" or "UTF8");
        Assert.Equal(StorageTransportSecurity.None, report.Diagnostics.Security);
    }

    [SftpFact]
    public async Task Sftp_rejected_host_key_is_reported_and_can_be_pinned()
    {
        var rejected = await Library.TestConnectionAsync(LiveServers.Sftp(c =>
        {
            c.AutoAcceptHostKey = false;
            c.HostKeyFingerprints = ["SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=')];
            c.Retry = new StorageRetryConfig { RetryCount = 0 };
        }));
        Assert.False(rejected.Succeeded);
        Assert.Equal(StorageErrors.HostKeyRejectedCode, rejected.Error!.Code);
        var presented = Assert.IsType<StorageServerIdentity>(rejected.ServerIdentity);
        Assert.Equal(("ssh-host-key", false), (presented.Kind, presented.Trusted));
        Assert.StartsWith("SHA256:", presented.Fingerprint);

        var pinned = await Library.TestConnectionAsync(LiveServers.Sftp(c =>
        {
            c.AutoAcceptHostKey = false;
            c.HostKeyFingerprints = [presented.Fingerprint];
        }));
        Assert.True(pinned.Succeeded, pinned.Error?.ToString());
        var diagnostics = pinned.Diagnostics!;
        Assert.StartsWith("SSH-2.0-", diagnostics.ServerSystem);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.ServerSoftware));
        Assert.Equal(StorageTransportSecurity.Ssh, diagnostics.Security);
        Assert.True(diagnostics.Negotiated.ContainsKey("kex"));
        Assert.Equal(presented.Algorithm, diagnostics.Negotiated["hostKey"]);
        Assert.True(diagnostics.ServerIdentity!.Trusted);
    }

    [WebDavFact]
    public async Task WebDav_reports_dav_compliance_and_methods()
    {
        var report = await Library.TestConnectionAsync(LiveServers.WebDav());

        Assert.True(report.Succeeded, report.Error?.ToString());
        Assert.Contains("DAV 1", report.Diagnostics!.ServerFeatures);
        Assert.Contains("PROPFIND", report.Diagnostics.ServerFeatures);
        Assert.Equal(StorageTransportSecurity.None, report.Diagnostics.Security);
    }

    [WebDavOptionsFact]
    public async Task WebDav_rejected_certificate_is_reported_and_can_be_pinned()
    {
        WebDavConnectionConfig Tls(IReadOnlyList<string>? pins = null) => LiveServers.WebDav(c =>
        {
            c.Endpoint = LiveServers.Env("CL_STORAGE_TEST_WEBDAV_TLS_URL")!;
            c.AllowInsecureHttp = false;
            c.TrustedPublicKeySha256 = pins?.ToList() ?? [];
            c.Retry = new StorageRetryConfig { RetryCount = 0 };
        });

        var rejected = await Library.TestConnectionAsync(Tls());
        Assert.False(rejected.Succeeded);
        Assert.Equal(StorageErrors.TlsFailureCode, rejected.Error!.Code);
        var presented = Assert.IsType<StorageServerIdentity>(rejected.ServerIdentity);

        var pinned = await Library.TestConnectionAsync(Tls([presented.PublicKeyFingerprint!]));
        Assert.True(pinned.Succeeded, pinned.Error?.ToString());
        Assert.Equal(StorageTransportSecurity.Tls, pinned.Diagnostics!.Security);
        Assert.True(pinned.Diagnostics.ServerIdentity!.Trusted);
    }
}
