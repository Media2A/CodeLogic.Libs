using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how an OpenStack Swift connection authenticates.</summary>
public enum SwiftAuthenticationMode
{
    /// <summary>Authenticates through Keystone v3 using a user and project.</summary>
    KeystoneV3Password,
    /// <summary>Uses a pre-issued token and storage URL.</summary>
    StaticToken
}

/// <summary>Defines named OpenStack Swift connections.</summary>
[ConfigSection("storage.swift")]
public sealed class SwiftStorageConfig : ProviderStorageConfigBase<SwiftConnectionConfig> { }

/// <summary>Defines one OpenStack Swift container connection.</summary>
public sealed class SwiftConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the Swift container name.</summary>
    [ConfigField(Label = "Container", Required = true, Group = "Connection", Order = 10)]
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the object prefix mounted as the connection root.</summary>
    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the authentication mode.</summary>
    public SwiftAuthenticationMode AuthenticationMode { get; set; } = SwiftAuthenticationMode.KeystoneV3Password;

    /// <summary>Gets or sets the Keystone authentication URL.</summary>
    [ConfigField(Label = "Authentication URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? AuthenticationUrl { get; set; }

    /// <summary>Gets or sets the Swift object-storage URL.</summary>
    [ConfigField(Label = "Storage URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 13)]
    public string? StorageUrl { get; set; }

    /// <summary>Gets or sets the Keystone username.</summary>
    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string? Username { get; set; }

    /// <summary>Gets or sets the Keystone password.</summary>
    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    /// <summary>Gets or sets the Keystone project name.</summary>
    [ConfigField(Label = "Project", Group = "Credentials", Order = 22)]
    public string? ProjectName { get; set; }

    /// <summary>Gets or sets the Keystone user domain name.</summary>
    public string UserDomainName { get; set; } = "Default";
    /// <summary>Gets or sets the Keystone project domain name.</summary>
    public string ProjectDomainName { get; set; } = "Default";
    /// <summary>Gets or sets an optional service-catalog region.</summary>
    public string? Region { get; set; }
    /// <summary>Gets or sets the requested service-catalog endpoint interface.</summary>
    public string EndpointInterface { get; set; } = "public";

    /// <summary>Gets or sets a pre-issued Swift authentication token.</summary>
    [ConfigField(Label = "Token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? Token { get; set; }

    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <inheritdoc />
    public override string MountRoot => Prefix;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (string.IsNullOrWhiteSpace(Container))
            yield return "Container is required";
        if (StoragePath.Normalize(Prefix ?? string.Empty).IsFailure)
            yield return "Prefix is invalid";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (AuthenticationMode == SwiftAuthenticationMode.StaticToken)
        {
            if (!IsHttps(StorageUrl)) yield return "StaticToken authentication requires an HTTPS StorageUrl";
            if (string.IsNullOrWhiteSpace(Token)) yield return "StaticToken authentication requires Token";
        }
        else
        {
            if (!IsHttps(AuthenticationUrl)) yield return "Keystone authentication requires an HTTPS AuthenticationUrl";
            if (string.IsNullOrWhiteSpace(Username)) yield return "Keystone authentication requires Username";
            if (string.IsNullOrWhiteSpace(Password)) yield return "Keystone authentication requires Password";
            if (string.IsNullOrWhiteSpace(ProjectName)) yield return "Keystone authentication requires ProjectName";
            if (string.IsNullOrWhiteSpace(UserDomainName)) yield return "UserDomainName is required";
            if (string.IsNullOrWhiteSpace(ProjectDomainName)) yield return "ProjectDomainName is required";
            if (!string.IsNullOrWhiteSpace(StorageUrl) && !IsHttps(StorageUrl)) yield return "StorageUrl must use HTTPS";
        }
    }

    private static bool IsHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
