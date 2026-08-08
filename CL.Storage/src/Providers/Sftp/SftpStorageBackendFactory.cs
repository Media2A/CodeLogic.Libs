using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using Renci.SshNet;

namespace CL.Storage.Providers.Sftp;

internal sealed class SftpStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(SftpConnectionConfig);
    public StorageProvider Provider => StorageProvider.Sftp;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (SftpConnectionConfig)configuration;
        return new SftpStorageBackend(
            connectionId,
            () => CreateClient(value),
            value.Root,
            maxBufferedDownloadBytes);
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
        var connection = new ConnectionInfo(value.Host, value.Port, value.Username, method)
        {
            Timeout = timeout
        };
        var client = new SftpClient(connection) { OperationTimeout = timeout };
        var fingerprints = value.HostKeyFingerprints
            .Select(fingerprint => CertificateFingerprint.TryNormalizeSha256(fingerprint, out var normalized) ? normalized : null)
            .Where(fingerprint => fingerprint is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        client.HostKeyReceived += (_, eventArgs) =>
        {
            eventArgs.CanTrust = value.AutoAcceptHostKey ||
                (CertificateFingerprint.TryNormalizeSha256(eventArgs.FingerPrintSHA256, out var normalized) &&
                 fingerprints.Contains(normalized));
        };
        return client;
    }
}
