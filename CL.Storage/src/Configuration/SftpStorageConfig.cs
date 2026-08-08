using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how an SFTP connection authenticates the client.</summary>
public enum SftpAuthenticationMode
{
    /// <summary>Authenticates with a password.</summary>
    Password,
    /// <summary>Authenticates with a private key and optional passphrase.</summary>
    PrivateKey
}

/// <summary>Defines named SFTP connections.</summary>
[ConfigSection("storage.sftp")]
public sealed class SftpStorageConfig : ProviderStorageConfigBase<SftpConnectionConfig> { }

/// <summary>Defines one SSH File Transfer Protocol connection.</summary>
public sealed class SftpConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the SSH server host.</summary>
    [ConfigField(Label = "Host", Required = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = string.Empty;

    /// <summary>Gets or sets the SSH server port.</summary>
    [ConfigField(Label = "Port", Group = "Connection", Order = 11)]
    public int Port { get; set; } = 22;

    /// <summary>Gets or sets the remote directory mounted as the connection root.</summary>
    [ConfigField(Label = "Root", Group = "Connection", Order = 12)]
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the login username.</summary>
    [ConfigField(Label = "Username", Required = true, Group = "Credentials", Order = 20)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the client authentication mode.</summary>
    public SftpAuthenticationMode AuthenticationMode { get; set; } = SftpAuthenticationMode.Password;

    /// <summary>Gets or sets the login password.</summary>
    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    /// <summary>Gets or sets the absolute private-key file path.</summary>
    [ConfigField(Label = "Private key", Group = "Credentials", Order = 22)]
    public string? PrivateKeyPath { get; set; }

    /// <summary>Gets or sets the private-key passphrase.</summary>
    [ConfigField(Label = "Private key passphrase", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>
    /// Gets or sets whether to trust any SSH server host key. This disables host-key verification and
    /// should only be used for trusted development environments or servers whose key cannot be pinned.
    /// </summary>
    [ConfigField(Label = "Auto-accept host key", Description = "Trust any SSH server host key. This disables host-key verification.", Group = "Connection", Order = 13)]
    public bool AutoAcceptHostKey { get; set; }

    /// <summary>Trusted server host-key fingerprints, in SHA256:base64 or hexadecimal form.</summary>
    public List<string> HostKeyFingerprints { get; set; } = [];

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
        if (AuthenticationMode == SftpAuthenticationMode.Password && Password is null)
            yield return "Password authentication requires Password";
        if (AuthenticationMode == SftpAuthenticationMode.PrivateKey)
        {
            if (string.IsNullOrWhiteSpace(PrivateKeyPath))
                yield return "Private-key authentication requires PrivateKeyPath";
            else if (!Path.IsPathFullyQualified(PrivateKeyPath))
                yield return "PrivateKeyPath must be an absolute path";
        }
        if (!AutoAcceptHostKey && (HostKeyFingerprints is null || HostKeyFingerprints.Count == 0))
            yield return "At least one HostKeyFingerprint is required";
        foreach (var fingerprint in HostKeyFingerprints ?? [])
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Host-key fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
    }
}
