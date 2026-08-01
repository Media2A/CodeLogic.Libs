using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using FluentFTP;

namespace CL.Storage.Providers.Ftp;

internal sealed class FtpStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(FtpConnectionConfig);
    public StorageProvider Provider => StorageProvider.Ftp;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (FtpConnectionConfig)configuration;
        return new FtpStorageBackend(
            connectionId,
            () => CreateClient(value),
            value.Root,
            maxBufferedDownloadBytes);
    }

    private static AsyncFtpClient CreateClient(FtpConnectionConfig value)
    {
        var config = new FtpConfig
        {
            EncryptionMode = value.EncryptionMode switch
            {
                StorageFtpEncryptionMode.None => FtpEncryptionMode.None,
                StorageFtpEncryptionMode.Implicit => FtpEncryptionMode.Implicit,
                _ => FtpEncryptionMode.Explicit
            },
            DataConnectionType = value.DataConnectionMode switch
            {
                StorageFtpDataConnectionMode.Epsv => FtpDataConnectionType.EPSV,
                StorageFtpDataConnectionMode.Pasv => FtpDataConnectionType.PASV,
                StorageFtpDataConnectionMode.AutoActive => FtpDataConnectionType.AutoActive,
                StorageFtpDataConnectionMode.Eprt => FtpDataConnectionType.EPRT,
                StorageFtpDataConnectionMode.Port => FtpDataConnectionType.PORT,
                _ => FtpDataConnectionType.AutoPassive
            },
            ConnectTimeout = checked(value.TimeoutSeconds * 1000),
            ReadTimeout = checked(value.TimeoutSeconds * 1000),
            DataConnectionConnectTimeout = checked(value.TimeoutSeconds * 1000),
            DataConnectionReadTimeout = checked(value.TimeoutSeconds * 1000)
        };

        if (!string.IsNullOrWhiteSpace(value.ClientCertificatePath))
        {
#pragma warning disable SYSLIB0057
            config.ClientCertificates.Add(new X509Certificate2(value.ClientCertificatePath, value.ClientCertificatePassword));
#pragma warning restore SYSLIB0057
        }

        var client = new AsyncFtpClient(
            value.Host,
            new NetworkCredential(value.Username, value.Password),
            value.Port,
            config);

        var pins = value.TrustedCertificateSha256
            .Select(fingerprint => CertificateFingerprint.TryNormalizeSha256(fingerprint, out var normalized) ? normalized : null)
            .Where(fingerprint => fingerprint is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        client.ValidateCertificate += (_, eventArgs) =>
        {
            var hash = eventArgs.Certificate is null
                ? null
                : Convert.ToHexString(SHA256.HashData(eventArgs.Certificate.GetRawCertData()));
            eventArgs.Accept = value.AcceptAnyCertificate ||
                (hash is not null && pins.Contains(hash)) ||
                (pins.Count == 0 && eventArgs.PolicyErrors == SslPolicyErrors.None);
        };
        return client;
    }
}
