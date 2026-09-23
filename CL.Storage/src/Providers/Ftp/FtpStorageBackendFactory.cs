using System.Net;
using System.Security.Authentication;
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
        var identity = new ServerIdentityRecorder();
        return new FtpStorageBackend(
            connectionId,
            () => CreateClient(value, identity),
            value.Root,
            maxBufferedDownloadBytes,
            value.Session,
            value.Retry,
            observer,
            AfterConnect(value))
        {
            AllowRawCommands = value.AllowRawCommands,
            Identity = identity
        };
    }

    private static AsyncFtpClient CreateClient(FtpConnectionConfig value, ServerIdentityRecorder? identity = null)
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
            ConnectTimeout = Milliseconds(value.ConnectTimeoutSeconds ?? value.TimeoutSeconds),
            ReadTimeout = Milliseconds(value.ReadTimeoutSeconds ?? value.TimeoutSeconds),
            DataConnectionConnectTimeout = Milliseconds(value.DataConnectionTimeoutSeconds ?? value.TimeoutSeconds),
            DataConnectionReadTimeout = Milliseconds(value.DataConnectionTimeoutSeconds ?? value.TimeoutSeconds),
            // Listing times and MFMT/MDTM values are converted to and from UTC, using ServerTimeZone
            // when the server reports local time.
            TimeConversion = FtpDate.UTC,
            DataConnectionEncryption = value.EncryptDataChannel,
            ValidateCertificateRevocation = value.CheckCertificateRevocation,
            SocketKeepAlive = value.SocketKeepAlive,
            UploadDataType = DataType(value.TransferType),
            DownloadDataType = DataType(value.TransferType),
            ListingParser = value.ListingParser switch
            {
                StorageFtpListingParser.Machine => FtpParser.Machine,
                StorageFtpListingParser.Unix => FtpParser.Unix,
                StorageFtpListingParser.UnixAlternative => FtpParser.UnixAlt,
                StorageFtpListingParser.Windows => FtpParser.Windows,
                StorageFtpListingParser.Vms => FtpParser.VMS,
                StorageFtpListingParser.IbmZos => FtpParser.IBMzOS,
                StorageFtpListingParser.NonStop => FtpParser.NonStop,
                _ => FtpParser.Auto
            }
        };
        if (value.TlsProtocols is { Count: > 0 } protocols)
            config.SslProtocols = protocols.Aggregate(SslProtocols.None, (all, protocol) => all | protocol);
        if (value.ActivePortMin is { } min && value.ActivePortMax is { } max)
            config.ActivePorts = Enumerable.Range(min, max - min + 1);
        if (!string.IsNullOrWhiteSpace(value.ActiveExternalIp))
        {
            var externalIp = value.ActiveExternalIp;
            config.AddressResolver = () => externalIp;
        }
        if (!string.IsNullOrWhiteSpace(value.ServerTimeZone))
        {
            config.ServerTimeZone = TimeZoneInfo.FindSystemTimeZoneById(value.ServerTimeZone);
        }
        if (value.Session is { KeepAliveSeconds: > 0 } session)
        {
            config.Noop = true;
            config.NoopInterval = checked(session.KeepAliveSeconds * 1000);
        }

        if (ClientCertificates.Load(value.ClientCertificatePath, value.ClientCertificateContent, value.ClientCertificatePassword) is { } certificate)
            config.ClientCertificates.Add(certificate);

        var client = CreateProxiedClient(value, config);
        client.Encoding = StorageEncodings.Get(value.Encoding);

        var pins = new TlsPins(value.TrustedCertificateSha256, value.TrustedPublicKeySha256);
        client.ValidateCertificate += (_, eventArgs) =>
        {
            eventArgs.Accept = pins.Accepts(eventArgs.Certificate, eventArgs.PolicyErrors, value.RequireValidCertificateChain);
            identity?.RecordCertificate(eventArgs.Certificate, eventArgs.Accept);
        };
        return client;
    }

    private static int Milliseconds(int seconds) => checked(seconds * 1000);

    private static FtpDataType DataType(StorageFtpTransferType type) =>
        type == StorageFtpTransferType.Ascii ? FtpDataType.ASCII : FtpDataType.Binary;

    /// <summary>Sends the configured post-login commands; a rejected command fails the connection.</summary>
    internal static Func<AsyncFtpClient, CancellationToken, Task>? AfterConnect(FtpConnectionConfig value)
    {
        if (value.LoginCommands is not { Count: > 0 } commands)
            return null;
        return async (client, token) =>
        {
            foreach (var command in commands)
            {
                var reply = await client.Execute(command, token).ConfigureAwait(false);
                if (!reply.Success)
                    throw new FluentFTP.Exceptions.FtpCommandException(reply);
            }
        };
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
