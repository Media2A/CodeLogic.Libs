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
    /// key store is unavailable and the machine key store is used instead; that store is machine-wide, so while the
    /// connection holds the key, its (non-persisted) container can be read by the machine's administrators. Only that
    /// failure falls back (see <see cref="IsUserKeyStoreUnavailable"/>): a wrong password is reported as it is.
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
        catch (CryptographicException error) when (OperatingSystem.IsWindows() && IsUserKeyStoreUnavailable(error))
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

    /// <summary>
    /// Whether a Windows import failed because the user key store cannot be used — the profile of the account is
    /// not loaded (some service accounts), so the key container's folder cannot be found or opened — which is the
    /// only case the machine key store is tried for. A wrong password, a damaged file, or anything else is reported
    /// as it is, and the machine-wide store (whose containers other administrators can read) is not touched.
    /// </summary>
    internal static bool IsUserKeyStoreUnavailable(CryptographicException error) => error.HResult switch
    {
        unchecked((int)0x80070002) => true, // ERROR_FILE_NOT_FOUND: the profile's key folder is missing
        unchecked((int)0x80070003) => true, // ERROR_PATH_NOT_FOUND
        unchecked((int)0x80090016) => true, // NTE_BAD_KEYSET: the user key set cannot be opened
        _ => false
    };

    /// <summary>The key storage for a platform: ephemeral where it is supported, the default (temporary) store elsewhere.</summary>
    internal static X509KeyStorageFlags KeyStorageFlags(bool windows, bool apple) =>
        windows || apple ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    private static X509Certificate2 Load(string? path, byte[]? content, string? password, X509KeyStorageFlags flags) =>
        content is { Length: > 0 }
            ? X509CertificateLoader.LoadPkcs12(content, password, flags)
            : X509CertificateLoader.LoadPkcs12FromFile(path!, password, flags);
}
