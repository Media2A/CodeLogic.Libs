using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how a Google Cloud Storage connection obtains credentials.</summary>
public enum GoogleCloudAuthenticationMode
{
    /// <summary>Uses Google application default credentials.</summary>
    ApplicationDefaultCredentials,
    /// <summary>Loads service-account credentials from a file.</summary>
    ServiceAccountFile,
    /// <summary>Loads service-account credentials from inline JSON.</summary>
    ServiceAccountJson,
    /// <summary>Sends no credentials; for public buckets and local emulators such as fake-gcs-server.</summary>
    Anonymous
}

/// <summary>Defines named Google Cloud Storage connections.</summary>
[ConfigSection("storage.gcs")]
public sealed class GoogleCloudStorageConfig : ProviderStorageConfigBase<GoogleCloudConnectionConfig> { }

/// <summary>Defines one Google Cloud Storage bucket connection.</summary>
public sealed class GoogleCloudConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the bucket name.</summary>
    [ConfigField(Label = "Bucket", Required = true, Group = "Connection", Order = 10)]
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the object prefix mounted as the connection root.</summary>
    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the credential acquisition mode.</summary>
    public GoogleCloudAuthenticationMode AuthenticationMode { get; set; } = GoogleCloudAuthenticationMode.ApplicationDefaultCredentials;

    /// <summary>Gets or sets the absolute service-account JSON file path.</summary>
    [ConfigField(Label = "Service-account JSON file", Group = "Credentials", Order = 20)]
    public string? CredentialsJsonPath { get; set; }

    /// <summary>Gets or sets inline service-account credential JSON.</summary>
    [ConfigField(Label = "Service-account JSON", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? CredentialsJson { get; set; }

    /// <summary>Gets or sets an alternative JSON API endpoint, such as a private endpoint or an emulator.</summary>
    /// <remarks>The <c>storage/v1/</c> path is appended when missing. Leave empty for Google's public endpoint.</remarks>
    public string? ServiceUrl { get; set; }

    /// <summary>Gets or sets whether an <c>http://</c> <see cref="ServiceUrl"/> is allowed.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>Gets or sets the resumable-upload chunk size in bytes.</summary>
    public int UploadChunkSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Gets or sets an optional HTTP or SOCKS proxy for this connection.</summary>
    public StorageProxyConfig Proxy { get; set; } = new();

    /// <inheritdoc />
    public override string MountRoot => Prefix;

    internal override IEnumerable<string> GetValidationErrors()
    {
        foreach (var error in (Proxy ?? new StorageProxyConfig()).GetValidationErrors("Proxy."))
            yield return error;
        if (string.IsNullOrWhiteSpace(Bucket))
            yield return "Bucket is required";
        if (StoragePath.Normalize(Prefix ?? string.Empty).IsFailure)
            yield return "Prefix is invalid";
        if (UploadChunkSizeBytes <= 0 || UploadChunkSizeBytes % (256 * 1024) != 0)
            yield return "UploadChunkSizeBytes must be a positive multiple of 256 KiB";
        if (AuthenticationMode == GoogleCloudAuthenticationMode.ServiceAccountFile)
        {
            if (string.IsNullOrWhiteSpace(CredentialsJsonPath))
                yield return "ServiceAccountFile authentication requires CredentialsJsonPath";
            else if (!Path.IsPathFullyQualified(CredentialsJsonPath))
                yield return "CredentialsJsonPath must be an absolute path";
        }
        if (AuthenticationMode == GoogleCloudAuthenticationMode.ServiceAccountJson && string.IsNullOrWhiteSpace(CredentialsJson))
            yield return "ServiceAccountJson authentication requires CredentialsJson";
        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                yield return "ServiceUrl must be an absolute HTTP(S) URL";
            else if (uri.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
                yield return "HTTP ServiceUrl endpoints require AllowInsecureHttp=true";
            else if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                yield return "ServiceUrl cannot contain user info, a query string, or a fragment";
        }
    }
}
