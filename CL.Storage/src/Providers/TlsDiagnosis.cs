using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
    /// <summary>
    /// The hint on a <c>storage.connection_lost</c> whose TLS record failed after the handshake (a decryption error,
    /// an unexpected end of stream): an ordinary drop, still worth retrying.
    /// </summary>
    public const string ConnectionInterrupted = "connection_interrupted";

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
    /// Whether an exception chain carries a failure from the platform's TLS stack: an SChannel status (Windows)
    /// raised by a TLS stream, or an OpenSSL error (Linux). An SSPI status outside a TLS stream — Negotiate or NTLM
    /// authentication failing — is not a TLS failure.
    /// </summary>
    public static bool IsPlatformTlsFailure(Exception exception)
    {
        Exception? parent = null;
        for (var current = exception; current is not null; parent = current, current = current.InnerException)
        {
            if (current is System.ComponentModel.Win32Exception { NativeErrorCode: var code } &&
                (code & unchecked((int)0xFFFFFF00)) == unchecked((int)0x80090300) &&
                parent is IOException or AuthenticationException && parent is not InvalidCredentialException)
                return true;
            if (current is System.Security.Cryptography.CryptographicException && current.Message.Contains("SSL routines", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a TLS failure belongs to the handshake: raised while it ran (an <see cref="AuthenticationException"/>),
    /// or an alert the server sent about it (our certificate, the protocol, its certificate) — which TLS 1.3 sends only
    /// after the handshake completed. Any other TLS error on an established connection (a record that fails to decrypt,
    /// an unexpected end of stream) is a lost connection, not a credential or trust problem.
    /// </summary>
    public static bool IsHandshakeFailure(Exception exception)
    {
        if (ProviderErrorMapper.Find<AuthenticationException>(exception) is { } authentication && authentication is not InvalidCredentialException)
            return true;
        return Reason(exception) is ClientCertificateRejected or ProtocolMismatch or ServerCertificateRejected;
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
    public static Error Enrich(Error error, ServerIdentityRecorder? identity, DateTimeOffset attemptStarted) =>
        Enrich(error, identity, attemptStarted,
            identity?.ClientCertificateRefusedAt is { } refused && refused >= attemptStarted - TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Explains a failed attempt. A client-certificate refusal counts only when it was recorded on a connection this
    /// attempt itself sent a request on (<see cref="ProviderAttempt.ClientCertificateRefused"/>); a refusal on any other
    /// connection of the backend — a spare connection, a concurrent transfer — leaves this attempt's drop a transient
    /// <c>storage.connection_lost</c>. Where the watch cannot see the connection (behind an HTTP proxy tunnel, the TLS
    /// stream sits on the tunnel rather than on the watched socket) nothing is recorded, and a refusal stays a plain
    /// lost connection with at most a <c>tlsReason</c> hint: never a false refusal.
    /// </summary>
    public static Error Enrich(Error error, ServerIdentityRecorder? identity, ProviderAttempt attempt) =>
        Enrich(error, identity, attempt.Started, attempt.ClientCertificateRefused);

    private static Error Enrich(Error error, ServerIdentityRecorder? identity, DateTimeOffset attemptStarted, bool clientCertificateRefused)
    {
        // TLS 1.3 servers refuse a missing or untrusted client certificate only after the handshake, and SChannel
        // reports the refusal as a dropped connection. It is taken for a refusal only when a connection the server
        // asked for a certificate failed right after its alert (recorded per connection by TlsConnectionWatch). A drop
        // on another connection, or on one whose certificate the server accepted (a server that requests but does not
        // require one), stays a transient lost connection.
        var unexplained = error.Code == StorageErrors.ConnectionLostCode ||
            (error.Code == StorageErrors.TlsFailureCode && StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out var why) && why == HandshakeFailed);
        var serverRefused = identity?.Last is { Kind: "tls-certificate", Trusted: false } && identity.LastRecordedAt >= attemptStarted - TimeSpan.FromMilliseconds(50);
        if (unexplained && !serverRefused && clientCertificateRefused)
        {
            return StorageErrors.TlsFailure(
                $"{error.Message} The server asked for a client certificate and closed the connection: the certificate was missing or refused.",
                Merge(error.Details, [$"{StorageErrorInfo.TlsReasonKey}={ClientCertificateRejected}", $"transportError={error.Code}"]));
        }
        if (error.Code == StorageErrors.ConnectionLostCode && !serverRefused && identity?.ClientCertificateRequestedAt is { } requested &&
            requested >= attemptStarted - TimeSpan.FromMilliseconds(50) &&
            !StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.TlsReasonKey, out _))
        {
            // Asked for a certificate, but nothing ties the drop to it: a hint only, and still transient.
            return StorageErrors.ConnectionLost(error.Message, Merge(error.Details, [$"{StorageErrorInfo.TlsReasonKey}={ClientCertificateRejected}"]));
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

/// <summary>
/// Watches the transport under one TLS connection, so a server refusing our client certificate can be told from an
/// ordinary drop. TLS 1.3 servers refuse a missing or untrusted certificate only after the handshake, with an alert on
/// the first read (SChannel then reports a dropped connection or a decryption error). A refusal is recorded only for a
/// connection the server actually asked for a certificate, and only when, after our reply, the connection failed or was
/// closed with evidence of a refusal: the server sent an alert's worth of bytes (at least one, fewer than
/// <see cref="AcceptedAfterBytes"/>), or a request sent after the reply got a read error or the end of the stream
/// instead of an answer. A connection merely closed without having read anything after the reply (a spare connection
/// the pool opened and later scavenged) records nothing, nor does one that carried a response afterwards (the server
/// accepted the certificate, or only requested one).
/// <para>
/// The refusal is attributed to the <see cref="ProviderAttempt"/> that sent a request on this connection after the
/// certificate reply, so only that attempt's failure is explained by it. A backend-wide time is kept as well, for
/// diagnostics only.
/// </para>
/// </summary>
internal sealed class TlsConnectionWatch(Stream inner, ServerIdentityRecorder recorder) : Stream
{
    /// <summary>Bytes the server sends after our reply that prove it accepted: an alert record is about 24 bytes; session tickets or a response are more.</summary>
    internal const int AcceptedAfterBytes = 64;
    private static readonly PropertyInfo? InnerStreamProperty = typeof(AuthenticatedStream).GetProperty("InnerStream", BindingFlags.Instance | BindingFlags.NonPublic);

    private const int NotAsked = 0;
    private const int Asked = 1;
    private const int Replied = 2;
    private const int Settled = 3;
    private int _state;
    private long _readAfterReply;
    private int _requestSent;
    private ProviderAttempt? _requestAttempt;

    /// <summary>
    /// Called from a TLS stream's client-certificate selection: records a request only when the server asked (it sent its
    /// certificate or the issuers it accepts), and only on the connection it asked on. A TLS stream that does not sit
    /// directly on a watched connection (an HTTP proxy tunnel) is not found, and nothing is recorded.
    /// </summary>
    public static void OnCertificateSelection(object sender, X509Certificate? remoteCertificate, string[]? acceptableIssuers)
    {
        if (remoteCertificate is null && acceptableIssuers is not { Length: > 0 }) return;
        if (sender is AuthenticatedStream stream && InnerStreamProperty?.GetValue(stream) is TlsConnectionWatch watch)
            watch.CertificateRequested();
    }

    /// <summary>The server asked this connection for a client certificate.</summary>
    public void CertificateRequested() => Interlocked.CompareExchange(ref _state, Asked, NotAsked);

    private void Wrote()
    {
        // A write after the certificate reply carries a request: remember that one was sent, and whose it was, so a
        // refusal explains only that attempt's failure.
        if (Volatile.Read(ref _state) == Replied)
        {
            Volatile.Write(ref _requestSent, 1);
            if (ProviderAttempt.Current is { } attempt) Volatile.Write(ref _requestAttempt, attempt);
        }
        Interlocked.CompareExchange(ref _state, Replied, Asked);
    }

    private void Read(int count, int requested)
    {
        // A zero-byte read of an empty buffer (SslStream waits for data that way) is not the end of the stream.
        if (count == 0) { if (requested > 0) Failed(disposing: false); return; }
        if (Volatile.Read(ref _state) == Replied && Interlocked.Add(ref _readAfterReply, count) >= AcceptedAfterBytes)
            Interlocked.CompareExchange(ref _state, Settled, Replied);
    }

    /// <summary>
    /// The connection failed (a read error or the end of the stream) or is being closed. It is taken for a refusal
    /// only after our certificate reply and before the server sent more than an alert, and only with evidence: an
    /// alert's worth of bytes was read, or a request sent after the reply failed to read its answer. A connection that
    /// is merely closed without having read anything after the reply — a spare the pool opened and later scavenged —
    /// records nothing.
    /// </summary>
    private void Failed(bool disposing)
    {
        var alertRead = Interlocked.Read(ref _readAfterReply) > 0;
        if (!alertRead && (disposing || Volatile.Read(ref _requestSent) == 0)) return;
        if (Interlocked.CompareExchange(ref _state, Settled, Replied) != Replied) return;
        recorder.RecordClientCertificateRefusal();
        Volatile.Read(ref _requestAttempt)?.RecordClientCertificateRefusal();
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count;
        try { count = inner.Read(buffer); }
        catch { Failed(disposing: false); throw; }
        Read(count, buffer.Length);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int count;
        try { count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { Failed(disposing: false); throw; }
        Read(count, buffer.Length);
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Wrote();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Wrote();
    }

    protected override void Dispose(bool disposing)
    {
        // Closed after our reply with only an alert's worth read: SChannel read the alert and failed, and the
        // connection is being discarded.
        if (disposing) { Failed(disposing: true); inner.Dispose(); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Failed(disposing: true);
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
