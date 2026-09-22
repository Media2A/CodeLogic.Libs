using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using WebDAVClient;

namespace CL.Storage.Providers.WebDav;

internal sealed class WebDavStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(WebDavConnectionConfig);
    public StorageProvider Provider => StorageProvider.WebDav;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes, IStorageConnectionObserver? observer = null)
    {
        var value = (WebDavConnectionConfig)configuration;
        var endpoint = new Uri(value.Endpoint, UriKind.Absolute);
        var http = CreateHttpClient(value, endpoint);
        var client = new Client(http)
        {
            Server = endpoint.GetLeftPart(UriPartial.Authority) + "/",
            BasePath = NormalizeBasePath(endpoint.AbsolutePath),
            Port = endpoint.IsDefaultPort ? null : endpoint.Port,
            CustomHeaders = value.Headers.ToArray()
        };
        return new WebDavStorageBackend(
            connectionId,
            client,
            value.Root,
            client.BasePath,
            ownsClient: true,
            maxBufferedDownloadBytes,
            value.Retry,
            observer,
            http,
            new Uri(endpoint.GetLeftPart(UriPartial.Authority)));
    }

    /// <summary>
    /// Builds the HTTP stack directly so TLS pinning, client certificates, proxies, connection limits,
    /// and Digest/NTLM/Negotiate all apply; the WebDAV client's own constructors expose none of them.
    /// </summary>
    internal static HttpClient CreateHttpClient(WebDavConnectionConfig value, Uri endpoint)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
        if (value.MaxConnectionsPerServer is { } limit)
            handler.MaxConnectionsPerServer = limit;
        if (value.Proxy?.ToWebProxy() is { } proxy)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        var pins = new TlsPins(value.TrustedCertificateSha256, value.TrustedPublicKeySha256);
        if (pins.Any)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                pins.Accepts(certificate, errors, value.RequireValidCertificateChain);
        if (!string.IsNullOrWhiteSpace(value.ClientCertificatePath))
        {
            handler.SslOptions.ClientCertificates =
            [
                X509CertificateLoader.LoadPkcs12FromFile(value.ClientCertificatePath, value.ClientCertificatePassword)
            ];
        }

        switch (value.AuthenticationMode)
        {
            case WebDavAuthenticationMode.Windows:
                handler.Credentials = CredentialCache.DefaultNetworkCredentials;
                handler.PreAuthenticate = true;
                break;
            case WebDavAuthenticationMode.Digest or WebDavAuthenticationMode.Ntlm or WebDavAuthenticationMode.Negotiate:
                var scheme = value.AuthenticationMode switch
                {
                    WebDavAuthenticationMode.Digest => "Digest",
                    WebDavAuthenticationMode.Ntlm => "NTLM",
                    _ => "Negotiate"
                };
                // Scoping the credential to one scheme stops the handler from downgrading to Basic.
                handler.Credentials = new CredentialCache
                {
                    { new Uri(endpoint.GetLeftPart(UriPartial.Authority)), scheme, new NetworkCredential(value.Username, value.Password) }
                };
                handler.PreAuthenticate = true;
                break;
        }

        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(value.TimeoutSeconds) };
        switch (value.AuthenticationMode)
        {
            case WebDavAuthenticationMode.Basic:
                // Sent up front: waiting for a 401 challenge doubles every request.
                var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{value.Username}:{value.Password}"));
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
                break;
            case WebDavAuthenticationMode.BearerToken:
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value.BearerToken);
                break;
        }
        return http;
    }

    private static string NormalizeBasePath(string path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path;
        if (!value.StartsWith('/')) value = "/" + value;
        if (!value.EndsWith('/')) value += "/";
        return value;
    }
}
