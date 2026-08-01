using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum SwiftAuthenticationMode
{
    KeystoneV3Password,
    StaticToken
}

[ConfigSection("storage.swift")]
public sealed class SwiftStorageConfig : ProviderStorageConfigBase<SwiftConnectionConfig> { }

public sealed class SwiftConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Container", Required = true, Group = "Connection", Order = 10)]
    public string Container { get; set; } = string.Empty;

    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    public SwiftAuthenticationMode AuthenticationMode { get; set; } = SwiftAuthenticationMode.KeystoneV3Password;

    [ConfigField(Label = "Authentication URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? AuthenticationUrl { get; set; }

    [ConfigField(Label = "Storage URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 13)]
    public string? StorageUrl { get; set; }

    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string? Username { get; set; }

    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    [ConfigField(Label = "Project", Group = "Credentials", Order = 22)]
    public string? ProjectName { get; set; }

    public string UserDomainName { get; set; } = "Default";
    public string ProjectDomainName { get; set; } = "Default";
    public string? Region { get; set; }
    public string EndpointInterface { get; set; } = "public";

    [ConfigField(Label = "Token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? Token { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

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
