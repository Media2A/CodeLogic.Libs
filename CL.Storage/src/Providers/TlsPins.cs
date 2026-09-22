using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CL.Storage.Configuration;

namespace CL.Storage.Providers;

/// <summary>
/// Server-certificate trust from SHA-256 pins. A certificate pin hashes the whole leaf certificate and
/// breaks on renewal; a public-key (SPKI) pin hashes only the key and survives renewals that keep it.
/// </summary>
internal sealed class TlsPins
{
    private readonly HashSet<string> _certificates;
    private readonly HashSet<string> _publicKeys;

    public TlsPins(IEnumerable<string>? certificatePins, IEnumerable<string>? publicKeyPins)
    {
        _certificates = Normalize(certificatePins);
        _publicKeys = Normalize(publicKeyPins);
    }

    public bool Any => _certificates.Count > 0 || _publicKeys.Count > 0;

    /// <summary>
    /// Accepts a pinned certificate, even self-signed, unless <paramref name="requireValidChain"/> is set.
    /// Without pins, only a certificate with no policy errors is accepted.
    /// </summary>
    public bool Accepts(X509Certificate? certificate, SslPolicyErrors errors, bool requireValidChain)
    {
        if (!Any)
            return errors == SslPolicyErrors.None;
        if (certificate is null)
            return false;
        if (requireValidChain && errors != SslPolicyErrors.None)
            return false;
        return Matches(certificate);
    }

    public bool Matches(X509Certificate certificate)
    {
        if (_certificates.Contains(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))))
            return true;
        if (_publicKeys.Count == 0)
            return false;
        // Only a certificate created here is disposed; the caller's is still used by the TLS stack.
        if (certificate is X509Certificate2 full)
            return _publicKeys.Contains(PublicKeyPin(full));
        using var parsed = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return _publicKeys.Contains(PublicKeyPin(parsed));
    }

    /// <summary>Uppercase hex SHA-256 of the SubjectPublicKeyInfo, the value an SPKI pin must match.</summary>
    internal static string PublicKeyPin(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static HashSet<string> Normalize(IEnumerable<string>? pins) => (pins ?? [])
        .Select(pin => CertificateFingerprint.TryNormalizeSha256(pin, out var normalized) ? normalized : null)
        .Where(pin => pin is not null)
        .Select(pin => pin!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
