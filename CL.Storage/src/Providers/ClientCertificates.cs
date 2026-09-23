using System.Security.Cryptography.X509Certificates;

namespace CL.Storage.Providers;

/// <summary>Loads a mutual-TLS client certificate from a file or from bytes held in memory.</summary>
internal static class ClientCertificates
{
    /// <summary>Returns the configured certificate, or null when neither a path nor content is set.</summary>
    public static X509Certificate2? Load(string? path, byte[]? content, string? password)
    {
        if (content is { Length: > 0 })
            return X509CertificateLoader.LoadPkcs12(content, password);
        return string.IsNullOrWhiteSpace(path) ? null : X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }
}
