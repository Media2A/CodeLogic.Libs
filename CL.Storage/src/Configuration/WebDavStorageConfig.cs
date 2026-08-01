using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum WebDavAuthenticationMode
{
    None,
    Basic,
    BearerToken
}

[ConfigSection("storage.webdav")]
public sealed class WebDavStorageConfig : ProviderStorageConfigBase<WebDavConnectionConfig> { }

public sealed class WebDavConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Endpoint", Required = true, InputType = ConfigInputType.Url, Group = "Connection", Order = 10)]
    public string Endpoint { get; set; } = string.Empty;

    [ConfigField(Label = "Root", Group = "Connection", Order = 11)]
    public string Root { get; set; } = string.Empty;

    public WebDavAuthenticationMode AuthenticationMode { get; set; } = WebDavAuthenticationMode.Basic;

    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string? Username { get; set; }

    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    [ConfigField(Label = "Bearer token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? BearerToken { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AcceptAnyCertificate { get; set; }
    public int TimeoutSeconds { get; set; } = 30;

    public override string MountRoot => Root;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            yield return "Endpoint must be an absolute HTTP(S) URL";
        if (StoragePath.Normalize(Root ?? string.Empty).IsFailure)
            yield return "Root is invalid";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (AuthenticationMode == WebDavAuthenticationMode.Basic && string.IsNullOrWhiteSpace(Username))
            yield return "Basic authentication requires Username";
        if (AuthenticationMode == WebDavAuthenticationMode.BearerToken && string.IsNullOrWhiteSpace(BearerToken))
            yield return "BearerToken authentication requires BearerToken";
        foreach (var (name, value) in Headers)
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                yield return "Custom HTTP headers cannot be blank or contain line breaks";
    }
}
