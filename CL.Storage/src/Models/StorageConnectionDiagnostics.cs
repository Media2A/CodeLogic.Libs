using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>How the connection to the server is protected.</summary>
public enum StorageTransportSecurity
{
    /// <summary>Not a network connection, or not known from the configuration.</summary>
    Unknown = 0,
    /// <summary>Plain text: FTP without TLS, or HTTP.</summary>
    None = 1,
    /// <summary>TLS: FTPS or HTTPS.</summary>
    Tls = 2,
    /// <summary>SSH.</summary>
    Ssh = 3
}

/// <summary>The certificate or host key a server presented, in the form a pin setting expects.</summary>
/// <param name="Kind">Either <c>tls-certificate</c> or <c>ssh-host-key</c>.</param>
/// <param name="Fingerprint">
/// For TLS, the uppercase hex SHA-256 of the certificate (for <c>TrustedCertificateSha256</c>); for SSH,
/// <c>SHA256:&lt;base64&gt;</c> (for <c>HostKeyFingerprints</c>).
/// </param>
/// <param name="PublicKeyFingerprint">For TLS, the SHA-256 of the public key (for <c>TrustedPublicKeySha256</c>).</param>
/// <param name="Algorithm">For SSH, the host key algorithm, such as <c>ssh-ed25519</c>.</param>
/// <param name="Subject">For TLS, the certificate subject.</param>
/// <param name="Issuer">For TLS, the certificate issuer.</param>
/// <param name="NotAfter">For TLS, when the certificate expires.</param>
/// <param name="Trusted">Whether the configured trust settings accepted it.</param>
public sealed record StorageServerIdentity(
    string Kind,
    string Fingerprint,
    string? PublicKeyFingerprint,
    string? Algorithm,
    string? Subject,
    string? Issuer,
    DateTimeOffset? NotAfter,
    bool Trusted);

/// <summary>Counters for a session pool (FTP, SFTP).</summary>
/// <param name="Idle">Connected sessions waiting to be reused.</param>
/// <param name="InUse">Sessions currently rented by operations.</param>
/// <param name="Opened">Sessions opened since the connection was created.</param>
/// <param name="Destroyed">Sessions closed, because they failed, expired, or the pool was full.</param>
/// <param name="ProbeFailures">Idle sessions found dead when checked before reuse.</param>
public sealed record StorageSessionPoolStats(int Idle, int InUse, int Opened, int Destroyed, int ProbeFailures);

/// <summary>The outcome of the most recent health check of a connection.</summary>
/// <param name="Healthy">Whether the check passed.</param>
/// <param name="ErrorCode">The <c>storage.*</c> code when it failed.</param>
/// <param name="Latency">How long the check took.</param>
/// <param name="CheckedAt">When the check finished (UTC).</param>
public sealed record StorageConnectionHealth(bool Healthy, string? ErrorCode, TimeSpan Latency, DateTimeOffset CheckedAt);

/// <summary>A live description of a connection and the server behind it. Never contains secrets.</summary>
public sealed record StorageConnectionDiagnostics
{
    /// <summary>Gets the connection ID.</summary>
    public required string ConnectionId { get; init; }
    /// <summary>Gets the provider.</summary>
    public required StorageProvider Provider { get; init; }
    /// <summary>Gets the server host name, when the connection has one.</summary>
    public string? Host { get; init; }
    /// <summary>Gets the server port, when the connection has one.</summary>
    public int? Port { get; init; }
    /// <summary>Gets how the connection is protected.</summary>
    public StorageTransportSecurity Security { get; init; }
    /// <summary>Gets the server software or system: the FTP <c>SYST</c> reply, or the SSH version string.</summary>
    public string? ServerSystem { get; init; }
    /// <summary>Gets the server type recognised by the client library, such as <c>VsFTPd</c>.</summary>
    public string? ServerSoftware { get; init; }
    /// <summary>Gets optional features the server advertised, such as the FTP <c>FEAT</c> list.</summary>
    public IReadOnlyList<string> ServerFeatures { get; init; } = [];
    /// <summary>
    /// Gets what was negotiated for the session: <c>tls</c> and <c>cipher</c> for FTPS, and <c>kex</c>,
    /// <c>hostKey</c>, <c>cipher</c> and <c>mac</c> for SSH.
    /// </summary>
    public IReadOnlyDictionary<string, string> Negotiated { get; init; } = new Dictionary<string, string>();
    /// <summary>Gets the certificate or host key the server presented most recently.</summary>
    public StorageServerIdentity? ServerIdentity { get; init; }
    /// <summary>Gets session pool counters, for FTP and SFTP.</summary>
    public StorageSessionPoolStats? Pool { get; init; }
    /// <summary>Gets the most recent health check, if one has run.</summary>
    public StorageConnectionHealth? LastHealth { get; init; }
}

/// <summary>One step of <see cref="StorageLibrary.TestConnectionAsync(Configuration.StorageConnectionConfigBase, CancellationToken)"/>.</summary>
/// <param name="Name">What the step checked: <c>validate</c>, <c>connect</c>, <c>list</c>, or <c>details</c>.</param>
/// <param name="Succeeded">Whether it passed.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Error">Why it failed.</param>
public sealed record StorageConnectionTestStep(string Name, bool Succeeded, TimeSpan Duration, Error? Error);

/// <summary>The result of testing a connection configuration that has not been saved.</summary>
/// <param name="Succeeded">Whether the connection validated, connected, and listed its root.</param>
/// <param name="Steps">Each step that ran, in order; later steps are skipped after a failure.</param>
/// <param name="Diagnostics">Server details, when the connection succeeded.</param>
/// <param name="ServerIdentity">
/// The certificate or host key the server presented, even when it was rejected, so a caller can offer to pin it.
/// </param>
/// <param name="Error">The first failure.</param>
public sealed record StorageConnectionTestReport(
    bool Succeeded,
    IReadOnlyList<StorageConnectionTestStep> Steps,
    StorageConnectionDiagnostics? Diagnostics,
    StorageServerIdentity? ServerIdentity,
    Error? Error);
