using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how a WebDAV connection authenticates HTTP requests.</summary>
public enum WebDavAuthenticationMode
{
    /// <summary>Sends no authentication credentials.</summary>
    None,
    /// <summary>Uses HTTP Basic authentication.</summary>
    Basic,
    /// <summary>Uses an HTTP bearer token.</summary>
    BearerToken,
    /// <summary>Uses the current Windows credentials.</summary>
    Windows
}

/// <summary>Defines named WebDAV connections.</summary>
[ConfigSection("storage.webdav")]
public sealed class WebDavStorageConfig : ProviderStorageConfigBase<WebDavConnectionConfig> { }

/// <summary>Defines one WebDAV endpoint connection.</summary>
public sealed class WebDavConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the absolute WebDAV endpoint URL.</summary>
    [ConfigField(Label = "Endpoint", Required = true, InputType = ConfigInputType.Url, Group = "Connection", Order = 10)]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote path mounted as the connection root.</summary>
    [ConfigField(Label = "Root", Group = "Connection", Order = 11)]
    public string Root { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTP authentication mode.</summary>
    public WebDavAuthenticationMode AuthenticationMode { get; set; } = WebDavAuthenticationMode.Basic;

    /// <summary>Gets or sets the Basic authentication username.</summary>
    [ConfigField(Label = "Username", Group = "Credentials", Order = 20)]
    public string? Username { get; set; }

    /// <summary>Gets or sets the Basic authentication password.</summary>
    [ConfigField(Label = "Password", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? Password { get; set; }

    /// <summary>Gets or sets the bearer token.</summary>
    [ConfigField(Label = "Bearer token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? BearerToken { get; set; }

    /// <summary>Gets or sets additional safe HTTP request headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Allows clear-text HTTP only when explicitly enabled for a deliberate endpoint.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Optional SHA-256 fingerprints of trusted TLS leaf certificates.</summary>
    public List<string> TrustedCertificateSha256 { get; set; } = [];

    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <inheritdoc />
    public override string MountRoot => Root;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            yield return "Endpoint must be an absolute HTTP(S) URL";
        else
        {
            if (endpoint.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
                yield return "HTTP endpoints require AllowInsecureHttp=true";
            if (!string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                yield return "Endpoint cannot contain user info, a query string, or a fragment";
        }
        if (StoragePath.Normalize(Root ?? string.Empty).IsFailure)
            yield return "Root is invalid";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (AuthenticationMode == WebDavAuthenticationMode.Basic && string.IsNullOrWhiteSpace(Username))
            yield return "Basic authentication requires Username";
        if (AuthenticationMode == WebDavAuthenticationMode.BearerToken && string.IsNullOrWhiteSpace(BearerToken))
            yield return "BearerToken authentication requires BearerToken";
        foreach (var fingerprint in TrustedCertificateSha256 ?? [])
        {
            if (!CertificateFingerprint.IsValidSha256(fingerprint))
                yield return $"Trusted certificate fingerprint '{fingerprint}' is not a SHA-256 fingerprint";
        }
        foreach (var (name, value) in Headers ?? [])
            if (string.IsNullOrWhiteSpace(name) || value is null || name.Contains('\r') || name.Contains('\n') || value.Contains('\r') || value.Contains('\n'))
                yield return "Custom HTTP headers cannot be blank or contain line breaks";
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                yield return $"Custom HTTP header '{name}' is managed by the transport or authentication mode";
    }
}
