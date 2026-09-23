using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>
/// Explains a <c>storage.tls_failure</c> with a <c>tlsReason</c> detail, so a caller can tell a server
/// certificate it may choose to trust from its own client certificate being refused or a protocol mismatch.
/// </summary>
internal static class TlsDiagnosis
{
    public const string ServerCertificateRejected = "server_certificate_rejected";
    public const string ClientCertificateRejected = "client_certificate_rejected";
    public const string ProtocolMismatch = "protocol_mismatch";
    public const string HandshakeFailed = "handshake_failed";

    private static readonly string[] ServerCertificateSignals =
    [
        "remote certificate", "certificate was rejected", "certificate is invalid", "RemoteCertificateValidationCallback",
        "certificate chain", "untrusted root", "certificate verify failed"
    ];

    // TLS alerts a server sends when it refuses the client's certificate (or demands one): bad_certificate
    // (42), unsupported_certificate (43), certificate_revoked (44), certificate_expired (45),
    // certificate_unknown (46), unknown_ca (48), certificate_required (116). OpenSSL (Linux) spells them
    // "alert bad certificate ... alert number 42"; SChannel (Windows) and macOS report the alert by its .NET
    // name, "the remote party sent a TLS alert: 'BadCertificate'".
    private static readonly string[] ClientCertificateSignals =
    [
        "bad certificate", "certificate required", "unknown ca", "certificate unknown", "certificate revoked",
        "certificate expired", "unsupported certificate", "alert number 42", "alert number 43", "alert number 44",
        "alert number 45", "alert number 46", "alert number 48", "alert number 116",
        "'BadCertificate'", "'UnsupportedCert'", "'CertificateRevoked'", "'CertificateExpired'", "'CertificateUnknown'",
        "'UnknownCA'", "'CertificateRequired'", "'NoCertificate'"
    ];

    // protocol_version (70), insufficient_security (71), handshake_failure (40) from cipher negotiation, in the
    // OpenSSL and SChannel spellings.
    private static readonly string[] ProtocolSignals =
    [
        "protocol version", "unsupported protocol", "wrong version number", "no protocols available",
        "no shared cipher", "insufficient security", "alert number 70", "alert number 71", "cipher suite",
        "client and server cannot communicate, because they do not possess a common algorithm",
        "'ProtocolVersion'", "'InsufficientSecurity'"
    ];

    // SChannel status codes, which (unlike its messages) are not localized. .NET validates the server's
    // certificate itself, so certificate statuses from SChannel carry the server's alert about ours.
    private const int SecEUnsupportedFunction = unchecked((int)0x80090302);
    private const int SecEAlgorithmMismatch = unchecked((int)0x80090331);
    private const int SecEUnknownCredentials = unchecked((int)0x8009030D);
    private const int SecENoCredentials = unchecked((int)0x8009030E);
    private const int SecEUntrustedRoot = unchecked((int)0x80090325);
    private const int SecECertUnknown = unchecked((int)0x80090327);
    private const int SecECertExpired = unchecked((int)0x80090328);
    private const int SecECertWrongUsage = unchecked((int)0x80090349);

    /// <summary>
    /// Whether an exception chain carries a TLS failure from the platform's TLS stack: an SChannel status
    /// (Windows) or an OpenSSL error (Linux). Both also arrive after the handshake — TLS 1.3 refuses a client
    /// certificate then — inside an <see cref="IOException"/> rather than an authentication exception.
    /// </summary>
    public static bool IsPlatformTlsFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.ComponentModel.Win32Exception { NativeErrorCode: var code } && (code & unchecked((int)0xFFFFFF00)) == unchecked((int)0x80090300))
                return true;
            if (current is System.Security.Cryptography.CryptographicException && current.Message.Contains("SSL routines", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Classifies a TLS failure by its SChannel status or its message chain.</summary>
    public static string Reason(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is not System.ComponentModel.Win32Exception win32) continue;
            switch (win32.NativeErrorCode)
            {
                case SecEUnsupportedFunction or SecEAlgorithmMismatch:
                    return ProtocolMismatch;
                case SecEUnknownCredentials or SecENoCredentials or SecEUntrustedRoot or SecECertUnknown or SecECertExpired or SecECertWrongUsage:
                    return ClientCertificateRejected;
            }
        }
        var text = Messages(exception);
        if (Contains(text, ServerCertificateSignals)) return ServerCertificateRejected;
        if (Contains(text, ClientCertificateSignals)) return ClientCertificateRejected;
        if (Contains(text, ProtocolSignals)) return ProtocolMismatch;
        return HandshakeFailed;
    }

    /// <summary>The <c>tlsReason</c> detail for a classified exception.</summary>
    public static string Details(Exception exception) => $"{StorageErrorInfo.TlsReasonKey}={Reason(exception)}";

    /// <summary>
    /// When our trust settings refused the server's certificate during the failed attempt (recorded at or after
    /// <paramref name="attemptStarted"/>), marks the failure as <see cref="ServerCertificateRejected"/> and adds
    /// the certificate's fingerprints. The error's other details are kept.
    /// </summary>
    public static Error Enrich(Error error, ServerIdentityRecorder? identity, DateTimeOffset attemptStarted)
    {
        // A connection dropped right after a handshake in which the server asked for a client certificate is the
        // server refusing that certificate (TLS 1.3 refuses it after the handshake, and SChannel reports the drop).
        var unexplained = error.Code == StorageErrors.ConnectionLostCode ||
            (error.Code == StorageErrors.TlsFailureCode && StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out var why) && why == HandshakeFailed);
        var serverRefused = identity?.Last is { Kind: "tls-certificate", Trusted: false } && identity.LastRecordedAt >= attemptStarted - TimeSpan.FromMilliseconds(50);
        if (unexplained && !serverRefused && identity?.ClientCertificateRequestedAt is { } requested &&
            requested >= attemptStarted - TimeSpan.FromMilliseconds(50))
        {
            return StorageErrors.TlsFailure(
                $"{error.Message} The server asked for a client certificate and closed the connection: the certificate was missing or refused.",
                Merge(error.Details, [$"{StorageErrorInfo.TlsReasonKey}={ClientCertificateRejected}", $"transportError={error.Code}"]));
        }
        if (error.Code != StorageErrors.TlsFailureCode || identity?.Last is not { Kind: "tls-certificate", Trusted: false } presented)
            return error;
        // Clock granularity: a rejection recorded in the same tick as the start still belongs to this attempt.
        if (identity.LastRecordedAt is not { } at || at < attemptStarted - TimeSpan.FromMilliseconds(50))
            return error;
        var added = new List<string>
        {
            $"{StorageErrorInfo.TlsReasonKey}={ServerCertificateRejected}",
            $"{StorageErrorInfo.PresentedCertificateKey}={presented.Fingerprint}"
        };
        if (presented.PublicKeyFingerprint is { } spki) added.Add($"{StorageErrorInfo.PresentedPublicKeyKey}={spki}");
        return StorageErrors.TlsFailure(error.Message, Merge(error.Details, added));
    }

    /// <summary>Keeps an error's details, replacing the keys that <paramref name="added"/> sets.</summary>
    private static string Merge(string? details, IReadOnlyList<string> added)
    {
        var replaced = added.Select(part => part.Split('=', 2)[0]).ToHashSet(StringComparer.Ordinal);
        var kept = (details ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !replaced.Contains(part.Split('=', 2)[0]));
        return string.Join(';', kept.Concat(added));
    }

    private static string Messages(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            parts.Add(current.Message);
        return string.Join(" | ", parts);
    }

    private static bool Contains(string text, string[] signals) =>
        signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
