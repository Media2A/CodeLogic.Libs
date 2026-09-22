using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using FluentFTP;
using FluentFTP.Proxy.AsyncProxy;

namespace CL.Storage.Providers.Ftp;

internal sealed class FtpStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(FtpConnectionConfig);
    public StorageProvider Provider => StorageProvider.Ftp;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null)
    {
        var value = (FtpConnectionConfig)configuration;
        return new FtpStorageBackend(
            connectionId,
            () => CreateClient(value),
            value.Root,
            maxBufferedDownloadBytes,
            value.Session,
            value.Retry,
            observer);
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
        if (value.Session is { KeepAliveSeconds: > 0 } session)
        {
            config.Noop = true;
            config.NoopInterval = checked(session.KeepAliveSeconds * 1000);
        }

        if (!string.IsNullOrWhiteSpace(value.ClientCertificatePath))
        {
#pragma warning disable SYSLIB0057
            config.ClientCertificates.Add(new X509Certificate2(value.ClientCertificatePath, value.ClientCertificatePassword));
#pragma warning restore SYSLIB0057
        }

        var client = CreateProxiedClient(value, config);

        var pins = value.TrustedCertificateSha256
            .Select(fingerprint => CertificateFingerprint.TryNormalizeSha256(fingerprint, out var normalized) ? normalized : null)
            .Where(fingerprint => fingerprint is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        client.ValidateCertificate += (_, eventArgs) =>
        {
            var hash = eventArgs.Certificate is null
                ? null
                : Convert.ToHexString(SHA256.HashData(eventArgs.Certificate.GetRawCertData()));
            eventArgs.Accept = (hash is not null && pins.Contains(hash)) ||
                (pins.Count == 0 && eventArgs.PolicyErrors == SslPolicyErrors.None);
        };
        return client;
    }

    private static AsyncFtpClient CreateProxiedClient(FtpConnectionConfig value, FtpConfig config)
    {
        var credentials = new NetworkCredential(value.Username, value.Password);
        var proxy = value.Proxy ?? new StorageProxyConfig();
        if (!proxy.Enabled)
            return new AsyncFtpClient(value.Host, credentials, value.Port, config);
        var profile = new FtpProxyProfile
        {
            ProxyHost = proxy.Host,
            ProxyPort = proxy.Port,
            ProxyCredentials = string.IsNullOrEmpty(proxy.Username) ? null : new NetworkCredential(proxy.Username, proxy.Password),
            FtpHost = value.Host,
            FtpPort = value.Port,
            FtpCredentials = credentials
        };
        AsyncFtpClient client = proxy.Type switch
        {
            StorageProxyType.Http => new AsyncFtpClientHttp11Proxy(profile),
            StorageProxyType.Socks4 => new AsyncFtpClientSocks4Proxy(profile),
            _ => new AsyncFtpClientSocks5Proxy(profile)
        };
        client.Config = config;
        return client;
    }
}
