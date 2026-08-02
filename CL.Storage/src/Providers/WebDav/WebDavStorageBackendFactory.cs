using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using WebDAVClient;

namespace CL.Storage.Providers.WebDav;

internal sealed class WebDavStorageBackendFactory : IStorageBackendFactory
{
    public Type ConfigurationType => typeof(WebDavConnectionConfig);
    public StorageProvider Provider => StorageProvider.WebDav;

    public IStorageBackend Create(string connectionId, object configuration, long maxBufferedDownloadBytes)
    {
        var value = (WebDavConnectionConfig)configuration;
        var endpoint = new Uri(value.Endpoint, UriKind.Absolute);
        var timeout = TimeSpan.FromSeconds(value.TimeoutSeconds);
        var client = value.AuthenticationMode switch
        {
            WebDavAuthenticationMode.BearerToken => new Client(value.BearerToken!, timeout, proxy: null),
            WebDavAuthenticationMode.Windows => new Client(
                CredentialCache.DefaultNetworkCredentials, timeout, proxy: null),
            WebDavAuthenticationMode.Basic => new Client(
                new NetworkCredential(value.Username, value.Password), timeout, proxy: null),
            _ => new Client(new NetworkCredential(), timeout, proxy: null)
        };
        client.Server = endpoint.GetLeftPart(UriPartial.Authority) + "/";
        client.BasePath = NormalizeBasePath(endpoint.AbsolutePath);
        client.Port = endpoint.IsDefaultPort ? null : endpoint.Port;
        client.CustomHeaders = value.Headers.ToArray();
        var pins = value.TrustedCertificateSha256
            .Select(fingerprint => CertificateFingerprint.TryNormalizeSha256(fingerprint, out var normalized) ? normalized : null)
            .Where(fingerprint => fingerprint is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pins.Count > 0)
        {
            client.ServerCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                    return false;
                var hash = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                return pins.Contains(hash);
            };
        }
        return new WebDavStorageBackend(
            connectionId,
            client,
            value.Root,
            client.BasePath,
            ownsClient: true,
            maxBufferedDownloadBytes);
    }

    private static string NormalizeBasePath(string path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/" : path;
        if (!value.StartsWith('/')) value = "/" + value;
        if (!value.EndsWith('/')) value += "/";
        return value;
    }
}
