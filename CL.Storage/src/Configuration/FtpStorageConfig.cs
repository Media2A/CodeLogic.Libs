using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum StorageFtpEncryptionMode
{
    None,
    Explicit,
    Implicit
}

public enum StorageFtpDataConnectionMode
{
    AutoPassive,
    Epsv,
    Pasv,
    AutoActive,
    Eprt,
    Port
}

[ConfigSection("storage.ftp")]
public sealed class FtpStorageConfig : ProviderStorageConfigBase<FtpConnectionConfig> { }

public sealed class FtpConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Host", Required = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = string.Empty;

    [ConfigField(Label = "Port", Group = "Connection", Order = 11)]
    public int Port { get; set; } = 21;

    [ConfigField(Label = "Root", Group = "Connection", Order = 12)]
    public string Root { get; set; } = string.Empty;

    public StorageFtpEncryptionMode EncryptionMode { get; set; } = StorageFtpEncryptionMode.Explicit;
    public StorageFtpDataConnectionMode DataConnectionMode { get; set; } = StorageFtpDataConnectionMode.AutoPassive;

    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string Username { get; set; } = "anonymous";

    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string Password { get; set; } = "anonymous@";

    /// <summary>Optional SHA-256 fingerprints of trusted TLS leaf certificates.</summary>
    public List<string> TrustedCertificateSha256 { get; set; } = [];

    /// <summary>Allows an invalid TLS certificate. Keep disabled unless the endpoint is otherwise authenticated.</summary>
    public bool AcceptAnyCertificate { get; set; }

    [ConfigField(Label = "Client certificate", Group = "TLS", Order = 30)]
    public string? ClientCertificatePath { get; set; }

    [ConfigField(Label = "Client certificate password", Secret = true, InputType = ConfigInputType.Password, Group = "TLS", Order = 31)]
    public string? ClientCertificatePassword { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

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
        foreach (var fingerprint in TrustedCertificateSha256)
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
