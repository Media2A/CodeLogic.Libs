using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum SftpAuthenticationMode
{
    Password,
    PrivateKey
}

[ConfigSection("storage.sftp")]
public sealed class SftpStorageConfig : ProviderStorageConfigBase<SftpConnectionConfig> { }

public sealed class SftpConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Host", Required = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = string.Empty;

    [ConfigField(Label = "Port", Group = "Connection", Order = 11)]
    public int Port { get; set; } = 22;

    [ConfigField(Label = "Root", Group = "Connection", Order = 12)]
    public string Root { get; set; } = string.Empty;

    [ConfigField(Label = "Username", Required = true, Group = "Credentials", Order = 20)]
    public string Username { get; set; } = string.Empty;

    public SftpAuthenticationMode AuthenticationMode { get; set; } = SftpAuthenticationMode.Password;

    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    [ConfigField(Label = "Private key", Group = "Credentials", Order = 22)]
    public string? PrivateKeyPath { get; set; }

    [ConfigField(Label = "Private key passphrase", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Trusted server host-key fingerprints, in SHA256:base64 or hexadecimal form.</summary>
    public List<string> HostKeyFingerprints { get; set; } = [];

    /// <summary>Allows any SSH host key. Keep disabled for normal use.</summary>
    public bool AcceptAnyHostKey { get; set; }

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
        if (AuthenticationMode == SftpAuthenticationMode.Password && Password is null)
            yield return "Password authentication requires Password";
        if (AuthenticationMode == SftpAuthenticationMode.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(PrivateKeyPath))
                yield return "Private-key authentication requires PrivateKeyPath";
            else if (!Path.IsPathFullyQualified(PrivateKeyPath))
                yield return "PrivateKeyPath must be an absolute path";
        }
        if (!AcceptAnyHostKey && HostKeyFingerprints.Count == 0)
            yield return "At least one HostKeyFingerprint is required unless AcceptAnyHostKey is enabled";
        foreach (var fingerprint in HostKeyFingerprints)
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Host-key fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
    }
}
