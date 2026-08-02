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

    /// <summary>Gets or sets the optional client-certificate path.</summary>
    [ConfigField(Label = "Client certificate", Group = "TLS", Order = 30)]
    public string? ClientCertificatePath { get; set; }

    /// <summary>Gets or sets the optional client-certificate password.</summary>
    [ConfigField(Label = "Client certificate password", Secret = true, InputType = ConfigInputType.Password, Group = "TLS", Order = 31)]
    public string? ClientCertificatePassword { get; set; }

    /// <summary>Gets or sets the operation timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <inheritdoc />
    public override string MountRoot => Root;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (string.IsNullOrWhiteSpace(Host))
            yield return "Host is required";
        if (Port is < 1 or > 65535)
            yield return "Port must be between 1 and 65535";
        if (string.IsNullOrWhiteSpace(Username))
            yield return "Username is required";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (StoragePath.Normalize(Root ?? string.Empty).IsFailure)
            yield return "Root is invalid";
        if (!string.IsNullOrWhiteSpace(ClientCertificatePath) && !Path.IsPathFullyQualified(ClientCertificatePath))
            yield return "ClientCertificatePath must be an absolute path";
        if (EncryptionMode == StorageFtpEncryptionMode.None && TrustedCertificateSha256?.Count > 0)
            yield return "TrustedCertificateSha256 requires an encrypted FTP connection";
        foreach (var fingerprint in TrustedCertificateSha256 ?? [])
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Trusted certificate fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
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
        {
            try
            {
                var bytes = Convert.FromBase64String(text[7..]);
                if (bytes.Length != 32) return false;
                normalized = Convert.ToHexString(bytes);
                return true;
            }
            catch (FormatException) { return false; }
        }

        text = text.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (text.Length != 64 || text.Any(character => !Uri.IsHexDigit(character)))
            return false;
        normalized = text.ToUpperInvariant();
        return true;
    }
}
