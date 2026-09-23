using System.Security.Cryptography.X509Certificates;

namespace CL.Storage.Providers;

/// <summary>
/// Loads a mutual-TLS client certificate from a file or from bytes held in memory. The caller owns the result
/// and disposes it with the connection; one certificate serves every session of that connection.
/// </summary>
internal static class ClientCertificates
{
    /// <summary>
    /// Returns the configured certificate, or null when neither a path nor content is set. On Linux and macOS
    /// the private key stays in memory only (<see cref="X509KeyStorageFlags.EphemeralKeySet"/>). On Windows,
    /// SChannel cannot authenticate with an in-memory key, so the key goes into a temporary key container that
    /// is not persisted and is deleted when the certificate is disposed with its connection.
    /// </summary>
    public static X509Certificate2? Load(string? path, byte[]? content, string? password)
    {
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        if (content is { Length: > 0 })
            return X509CertificateLoader.LoadPkcs12(content, password, flags);
        return string.IsNullOrWhiteSpace(path) ? null : X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
    }
}
