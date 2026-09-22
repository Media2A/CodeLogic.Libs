using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using Renci.SshNet;

namespace CL.Storage.Providers.Sftp;

internal sealed class SftpStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(SftpConnectionConfig);
    public StorageProvider Provider => StorageProvider.Sftp;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null)
    {
        var value = (SftpConnectionConfig)configuration;
        return new SftpStorageBackend(
            connectionId,
            () => CreateClient(value),
            value.Root,
            maxBufferedDownloadBytes,
            value.Session,
            value.Retry,
            observer);
    }

    private static SftpClient CreateClient(SftpConnectionConfig value)
    {
        AuthenticationMethod method = value.AuthenticationMode switch
        {
            SftpAuthenticationMode.PrivateKey => new PrivateKeyAuthenticationMethod(
                value.Username,
                string.IsNullOrEmpty(value.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(value.PrivateKeyPath!)
                    : new PrivateKeyFile(value.PrivateKeyPath!, value.PrivateKeyPassphrase)),
            _ => new PasswordAuthenticationMethod(value.Username, value.Password ?? string.Empty)
        };
        var timeout = TimeSpan.FromSeconds(value.TimeoutSeconds);
        var proxy = value.Proxy ?? new StorageProxyConfig();
        var connection = new ConnectionInfo(
            value.Host,
            value.Port,
            value.Username,
            proxy.Type switch
            {
                StorageProxyType.Http => ProxyTypes.Http,
                StorageProxyType.Socks4 => ProxyTypes.Socks4,
                StorageProxyType.Socks5 => ProxyTypes.Socks5,
                _ => ProxyTypes.None
            },
            proxy.Enabled ? proxy.Host : null,
            proxy.Enabled ? proxy.Port : 0,
            proxy.Username,
            proxy.Password,
            method)
        {
            Timeout = timeout
        };
        var client = new SftpClient(connection) { OperationTimeout = timeout };
        if (value.Session is { KeepAliveSeconds: > 0 } session)
            client.KeepAliveInterval = TimeSpan.FromSeconds(session.KeepAliveSeconds);
        var fingerprints = value.HostKeyFingerprints
            .Select(fingerprint => CertificateFingerprint.TryNormalizeSha256(fingerprint, out var normalized) ? normalized : null)
            .Where(fingerprint => fingerprint is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        client.HostKeyReceived += (_, eventArgs) =>
        {
            eventArgs.CanTrust = value.AutoAcceptHostKey ||
                (CertificateFingerprint.TryNormalizeSha256(eventArgs.FingerPrintSHA256, out var normalized) &&
                 fingerprints.Contains(normalized));
            if (!eventArgs.CanTrust)
                SftpHostKeyTracker.MarkRejected(client, $"SHA256:{eventArgs.FingerPrintSHA256}");
        };
        return client;
    }
}
