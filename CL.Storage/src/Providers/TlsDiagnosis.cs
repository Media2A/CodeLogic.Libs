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

    /// <summary>How long a recorded rejection counts as the cause of a failure that follows it.</summary>
    private static readonly TimeSpan RecentRejection = TimeSpan.FromMinutes(2);

    private static readonly string[] ServerCertificateSignals =
    [
        "remote certificate", "certificate was rejected", "certificate is invalid", "RemoteCertificateValidationCallback",
        "certificate chain", "untrusted root", "certificate verify failed"
    ];

    // TLS alerts a server sends when it refuses the client's certificate (or demands one): bad_certificate
    // (42), unsupported_certificate (43), certificate_revoked (44), certificate_expired (45),
    // certificate_unknown (46), unknown_ca (48), certificate_required (116).
    private static readonly string[] ClientCertificateSignals =
    [
        "bad certificate", "certificate required", "unknown ca", "certificate unknown", "certificate revoked",
        "certificate expired", "unsupported certificate", "alert number 42", "alert number 43", "alert number 44",
        "alert number 45", "alert number 46", "alert number 48", "alert number 116"
    ];

    // protocol_version (70), insufficient_security (71), handshake_failure (40) from cipher negotiation.
    private static readonly string[] ProtocolSignals =
    [
        "protocol version", "unsupported protocol", "wrong version number", "no protocols available",
        "no shared cipher", "insufficient security", "alert number 70", "alert number 71", "cipher suite",
        "client and server cannot communicate, because they do not possess a common algorithm"
    ];

    /// <summary>Classifies a TLS handshake exception by its message chain.</summary>
    public static string Reason(Exception exception)
    {
        var text = Messages(exception);
        if (Contains(text, ServerCertificateSignals)) return ServerCertificateRejected;
        if (Contains(text, ClientCertificateSignals)) return ClientCertificateRejected;
        if (Contains(text, ProtocolSignals)) return ProtocolMismatch;
        return HandshakeFailed;
    }

    /// <summary>The <c>tlsReason</c> detail for a classified exception.</summary>
    public static string Details(Exception exception) => $"{StorageErrorInfo.TlsReasonKey}={Reason(exception)}";

    /// <summary>
    /// When the server's certificate was just refused by our trust settings, marks the failure as
    /// <see cref="ServerCertificateRejected"/> and attaches the certificate's fingerprints.
    /// </summary>
    public static Error Enrich(Error error, ServerIdentityRecorder? identity)
    {
        if (error.Code != StorageErrors.TlsFailureCode || identity?.Last is not { Kind: "tls-certificate", Trusted: false } presented)
            return error;
        if (identity.LastRecordedAt is not { } at || DateTimeOffset.UtcNow - at > RecentRejection)
            return error;
        var details = $"{StorageErrorInfo.TlsReasonKey}={ServerCertificateRejected}" +
            $";{StorageErrorInfo.PresentedCertificateKey}={presented.Fingerprint}" +
            (presented.PublicKeyFingerprint is { } spki ? $";{StorageErrorInfo.PresentedPublicKeyKey}={spki}" : string.Empty);
        return StorageErrors.TlsFailure(error.Message, details);
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
