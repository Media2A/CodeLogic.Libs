using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies FTP transport encryption negotiation.</summary>
public enum StorageFtpEncryptionMode
{
    /// <summary>Uses unencrypted FTP.</summary>
    None,
    /// <summary>Upgrades an FTP connection with explicit TLS.</summary>
    Explicit,
    /// <summary>Starts the connection using implicit TLS.</summary>
    Implicit
}

/// <summary>Specifies how FTP data connections are established.</summary>
public enum StorageFtpDataConnectionMode
{
    /// <summary>Automatically chooses a passive strategy.</summary>
    AutoPassive,
    /// <summary>Uses extended passive mode.</summary>
    Epsv,
    /// <summary>Uses passive mode.</summary>
    Pasv,
    /// <summary>Automatically chooses an active strategy.</summary>
    AutoActive,
    /// <summary>Uses extended active mode.</summary>
    Eprt,
    /// <summary>Uses active port mode.</summary>
    Port
}

/// <summary>Specifies the FTP transfer type.</summary>
public enum StorageFtpTransferType
{
    /// <summary>Transfers bytes unchanged (TYPE I). Correct for every file type.</summary>
    Binary,
    /// <summary>Converts line endings between client and server (TYPE A). Only for text files.</summary>
    Ascii
}

/// <summary>Specifies how FTP directory listings are parsed.</summary>
public enum StorageFtpListingParser
{
    /// <summary>Detects the format from the server.</summary>
    Auto,
    /// <summary>Machine-readable MLSD listings.</summary>
    Machine,
    /// <summary>Unix <c>ls -l</c> style listings.</summary>
    Unix,
    /// <summary>Alternative Unix listing parser for unusual servers.</summary>
    UnixAlternative,
    /// <summary>Windows/IIS style listings.</summary>
    Windows,
    /// <summary>OpenVMS listings.</summary>
    Vms,
    /// <summary>IBM z/OS listings.</summary>
    IbmZos,
    /// <summary>HP NonStop/Tandem listings.</summary>
    NonStop
}

/// <summary>Defines named FTP and FTPS connections.</summary>
[ConfigSection("storage.ftp")]
public sealed class FtpStorageConfig : ProviderStorageConfigBase<FtpConnectionConfig> { }

/// <summary>Defines one FTP or FTPS connection.</summary>
public sealed class FtpConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the FTP server host.</summary>
    [ConfigField(Label = "Host", Required = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the FTP server port.</summary>
    [ConfigField(Label = "Port", Group = "Connection", Order = 11)]
    public int Port { get; set; } = 21;

    /// <summary>Gets or sets the remote directory mounted as the connection root.</summary>
    [ConfigField(Label = "Root", Group = "Connection", Order = 12)]
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the transport encryption mode.</summary>
    public StorageFtpEncryptionMode EncryptionMode { get; set; } = StorageFtpEncryptionMode.Explicit;
    /// <summary>Gets or sets the data-channel connection strategy.</summary>
    public StorageFtpDataConnectionMode DataConnectionMode { get; set; } = StorageFtpDataConnectionMode.AutoPassive;

    /// <summary>Gets or sets the login username.</summary>
    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string Username { get; set; } = "anonymous";

    /// <summary>Gets or sets the login password.</summary>
    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string Password { get; set; } = "anonymous@";

    /// <summary>Optional SHA-256 fingerprints of trusted TLS leaf certificates.</summary>
    public List<string> TrustedCertificateSha256 { get; set; } = [];

    /// <summary>Optional SHA-256 fingerprints of trusted server public keys (SPKI); these survive certificate renewals that keep the key.</summary>
    public List<string> TrustedPublicKeySha256 { get; set; } = [];

    /// <summary>Gets or sets whether a pinned certificate must also pass normal chain validation.</summary>
    public bool RequireValidCertificateChain { get; set; }

    /// <summary>Gets or sets whether certificate revocation is checked during validation.</summary>
    public bool CheckCertificateRevocation { get; set; }

    /// <summary>Gets or sets the allowed TLS versions, such as <c>Tls12</c> and <c>Tls13</c>; empty lets the OS choose.</summary>
    public List<System.Security.Authentication.SslProtocols> TlsProtocols { get; set; } = [];

    /// <summary>Gets or sets whether data connections are encrypted too (PROT P). Disabling it leaves file contents in clear text.</summary>
    public bool EncryptDataChannel { get; set; } = true;

    /// <summary>Gets or sets the lowest local port offered for active-mode data connections.</summary>
    public int? ActivePortMin { get; set; }

    /// <summary>Gets or sets the highest local port offered for active-mode data connections.</summary>
    public int? ActivePortMax { get; set; }

    /// <summary>Gets or sets the public IP address announced in active mode, for clients behind NAT.</summary>
    public string? ActiveExternalIp { get; set; }

    /// <summary>Gets or sets the character encoding of file names, such as <c>utf-8</c> or <c>windows-1252</c>.</summary>
    public string Encoding { get; set; } = "utf-8";

    /// <summary>Gets or sets the transfer type used for uploads and downloads.</summary>
    public StorageFtpTransferType TransferType { get; set; } = StorageFtpTransferType.Binary;

    /// <summary>Gets or sets the directory-listing parser.</summary>
    public StorageFtpListingParser ListingParser { get; set; } = StorageFtpListingParser.Auto;

    /// <summary>Gets or sets the server's time zone (IANA or Windows ID) for servers that list local times.</summary>
    public string? ServerTimeZone { get; set; }

    /// <summary>Gets or sets the connect timeout in seconds; defaults to <see cref="TimeoutSeconds"/>.</summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>Gets or sets the control-connection read timeout in seconds; defaults to <see cref="TimeoutSeconds"/>.</summary>
    public int? ReadTimeoutSeconds { get; set; }

    /// <summary>Gets or sets the data-connection timeout in seconds; defaults to <see cref="TimeoutSeconds"/>.</summary>
    public int? DataConnectionTimeoutSeconds { get; set; }

    /// <summary>Gets or sets whether TCP keep-alive is enabled on the sockets.</summary>
    public bool SocketKeepAlive { get; set; }

    /// <summary>Gets or sets raw commands sent after every login, such as <c>SITE UMASK 022</c>.</summary>
    /// <remarks>A command the server rejects fails the connection, so a misconfiguration surfaces immediately.</remarks>
    public List<string> LoginCommands { get; set; } = [];

    /// <summary>Gets or sets the optional client-certificate path.</summary>
    [ConfigField(Label = "Client certificate", Group = "TLS", Order = 30)]
    public string? ClientCertificatePath { get; set; }

    /// <summary>Gets or sets the optional client-certificate password.</summary>
    [ConfigField(Label = "Client certificate password", Secret = true, InputType = ConfigInputType.Password, Group = "TLS", Order = 31)]
    public string? ClientCertificatePassword { get; set; }

    /// <summary>Gets or sets the operation timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets session pooling, keep-alive, and concurrency limits.</summary>
    public StorageSessionConfig Session { get; set; } = new();

    /// <summary>Gets or sets automatic retry of transient failures.</summary>
    public StorageRetryConfig Retry { get; set; } = new();

    /// <summary>
    /// Gets or sets whether raw commands may be sent through <see cref="Abstractions.IStorageCommandService"/>.
    /// Off by default: commands are not confined to <c>Root</c> and can do anything the account may do.
    /// </summary>
    public bool AllowRawCommands { get; set; }

    /// <summary>Gets or sets an optional HTTP or SOCKS proxy for this connection.</summary>
    public StorageProxyConfig Proxy { get; set; } = new();

    /// <inheritdoc />
    public override string MountRoot => Root;

    internal override IEnumerable<string> GetValidationErrors()
    {
        foreach (var error in (Proxy ?? new StorageProxyConfig()).GetValidationErrors("Proxy."))
            yield return error;
        if (string.IsNullOrWhiteSpace(Host))
            yield return "Host is required";
        if (Port is < 1 or > 65535)
            yield return "Port must be between 1 and 65535";
        if (string.IsNullOrWhiteSpace(Username))
            yield return "Username is required";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        foreach (var error in (Session ?? new StorageSessionConfig()).GetValidationErrors("Session."))
            yield return error;
        foreach (var error in (Retry ?? new StorageRetryConfig()).GetValidationErrors("Retry."))
            yield return error;
        if (StoragePath.Normalize(Root ?? string.Empty).IsFailure)
            yield return "Root is invalid";
        if (!string.IsNullOrWhiteSpace(ClientCertificatePath) && !Path.IsPathFullyQualified(ClientCertificatePath))
            yield return "ClientCertificatePath must be an absolute path";
        if (EncryptionMode == StorageFtpEncryptionMode.None && (TrustedCertificateSha256?.Count > 0 || TrustedPublicKeySha256?.Count > 0))
            yield return "Certificate and public-key pins require an encrypted FTP connection";
        foreach (var fingerprint in (TrustedCertificateSha256 ?? []).Concat(TrustedPublicKeySha256 ?? []))
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Trusted certificate fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
        foreach (var protocol in TlsProtocols ?? [])
        {
            if (protocol is not (System.Security.Authentication.SslProtocols.Tls12 or System.Security.Authentication.SslProtocols.Tls13))
                yield return $"TlsProtocols may only contain Tls12 and Tls13, not {protocol}";
        }
        if (ActivePortMin.HasValue != ActivePortMax.HasValue)
            yield return "ActivePortMin and ActivePortMax must be set together";
        else if (ActivePortMin is { } min && ActivePortMax is { } max && (min is < 1024 or > 65535 || max is < 1024 or > 65535 || min > max))
            yield return "The active port range must be within 1024-65535 with ActivePortMin <= ActivePortMax";
        if (!string.IsNullOrWhiteSpace(ActiveExternalIp) && !System.Net.IPAddress.TryParse(ActiveExternalIp, out _))
            yield return "ActiveExternalIp must be an IP address";
        if (!Providers.StorageEncodings.IsKnown(Encoding))
            yield return $"Encoding '{Encoding}' is not a supported character encoding";
        if (!string.IsNullOrWhiteSpace(ServerTimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(ServerTimeZone, out _))
            yield return $"ServerTimeZone '{ServerTimeZone}' is not a known time zone";
        if (ConnectTimeoutSeconds <= 0 || ReadTimeoutSeconds <= 0 || DataConnectionTimeoutSeconds <= 0)
            yield return "Timeout overrides must be greater than zero";
        foreach (var command in LoginCommands ?? [])
        {
            if (string.IsNullOrWhiteSpace(command) || command.Contains('\r') || command.Contains('\n'))
                yield return "LoginCommands cannot be blank or contain line breaks";
        }
    }

}

internal static class CertificateFingerprint
{
    internal static bool IsValidSha256(string value) => TryNormalizeSha256(value, out _);

    internal static bool TryNormalizeSha256(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var text = value.Trim();
        if (text.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            return TryNormalizeBase64(text[7..], out normalized);

        var hex = text.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (hex.Length == 64 && hex.All(Uri.IsHexDigit))
        {
            normalized = hex.ToUpperInvariant();
            return true;
        }

        // SSH.NET reports HostKeyEventArgs.FingerPrintSHA256 as canonical,
        // unpadded Base64 without the OpenSSH "SHA256:" display prefix.
        return TryNormalizeBase64(text, out normalized);
    }

    private static bool TryNormalizeBase64(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length is not (43 or 44) ||
            value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            value.Length == 44 && value[^1] != '=')
            return false;

        var padded = value.Length == 43 ? value + "=" : value;
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != 32 ||
                !string.Equals(Convert.ToBase64String(bytes), padded, StringComparison.Ordinal))
                return false;
            normalized = Convert.ToHexString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
