using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CL.Storage.Providers;

/// <summary>
/// Loads a mutual-TLS client certificate from a file or from bytes held in memory. The caller owns the result
/// and disposes it with the connection; one certificate serves every session of that connection.
/// </summary>
internal static class ClientCertificates
{
    /// <summary>
    /// Returns the configured certificate, or null when neither a path nor content is set. The PKCS#12 data must
    /// hold the private key; a certificate without one is refused here rather than failing every handshake later.
    /// </summary>
    /// <remarks>
    /// Where the private key lives depends on the platform. On Linux it stays in memory only
    /// (<see cref="X509KeyStorageFlags.EphemeralKeySet"/>). macOS does not support ephemeral keys, so .NET imports the
    /// key into a temporary keychain of its own, which it deletes when the certificate is disposed with the
    /// connection. On Windows, SChannel cannot authenticate with an in-memory key, so the key goes into a key
    /// container that is not persisted and is deleted when the certificate is disposed; a process that crashes can
    /// leave that container file behind. Where the user profile is not loaded (some service accounts) the user
    /// key store is unavailable and the machine key store is used instead.
    /// </remarks>
    public static X509Certificate2? Load(string? path, byte[]? content, string? password)
    {
        if (content is not { Length: > 0 } && string.IsNullOrWhiteSpace(path))
            return null;
        var flags = KeyStorageFlags(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst());
        X509Certificate2 certificate;
        try
        {
            certificate = Load(path, content, password, flags);
        }
        catch (CryptographicException) when (OperatingSystem.IsWindows())
        {
            certificate = Load(path, content, password, X509KeyStorageFlags.MachineKeySet);
        }
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException(
                "The client certificate has no private key: a mutual-TLS certificate must be a PKCS#12 (.pfx/.p12) file that includes its key.");
        }
        return certificate;
    }

    /// <summary>The key storage for a platform: ephemeral where it is supported, the default (temporary) store elsewhere.</summary>
    internal static X509KeyStorageFlags KeyStorageFlags(bool windows, bool apple) =>
        windows || apple ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    private static X509Certificate2 Load(string? path, byte[]? content, string? password, X509KeyStorageFlags flags) =>
        content is { Length: > 0 }
            ? X509CertificateLoader.LoadPkcs12(content, password, flags)
            : X509CertificateLoader.LoadPkcs12FromFile(path!, password, flags);
}
