using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how an S3 connection obtains AWS-compatible credentials.</summary>
public enum S3AuthenticationMode
{
    /// <summary>Uses the SDK default credential chain.</summary>
    DefaultCredentialChain,
    /// <summary>Uses the access key, secret key, and optional session token in configuration.</summary>
    StaticCredentials
}

/// <summary>Defines named Amazon S3 and S3-compatible connections.</summary>
[ConfigSection("storage.s3")]
public sealed class S3StorageConfig : ProviderStorageConfigBase<S3ConnectionConfig> { }

/// <summary>Defines one isolated Amazon S3 or S3-compatible bucket connection.</summary>
public sealed class S3ConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the bucket name.</summary>
    [ConfigField(Label = "Bucket", Required = true, Group = "Connection", Order = 10)]
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Gets or sets the key prefix mounted as the connection root.</summary>
    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional custom S3-compatible service URL.</summary>
    [ConfigField(Label = "Service URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? ServiceUrl { get; set; }

    /// <summary>Gets or sets the signing region.</summary>
    [ConfigField(Label = "Region", Group = "Connection", Order = 13)]
    public string Region { get; set; } = "us-east-1";

    /// <summary>Gets or sets the credential acquisition mode.</summary>
    public S3AuthenticationMode AuthenticationMode { get; set; } = S3AuthenticationMode.DefaultCredentialChain;

    /// <summary>Gets or sets the static access key ID.</summary>
    [ConfigField(Label = "Access Key", Secret = true, Group = "Credentials", Order = 20)]
    public string? AccessKey { get; set; }

    /// <summary>Gets or sets the static secret access key.</summary>
    [ConfigField(Label = "Secret Key", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? SecretKey { get; set; }

    /// <summary>Gets or sets an optional temporary-credential session token.</summary>
    [ConfigField(Label = "Session Token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? SessionToken { get; set; }

    /// <summary>Gets or sets whether requests use path-style bucket addressing.</summary>
    public bool ForcePathStyle { get; set; }
    /// <summary>Allows a clear-text custom S3-compatible endpoint only when deliberately enabled.</summary>
    public bool AllowInsecureHttp { get; set; }
    /// <summary>Gets or sets whether payload signing is disabled for compatible services.</summary>
    public bool DisablePayloadSigning { get; set; }
    /// <summary>Gets or sets whether SDK default response checksum validation is disabled.</summary>
    public bool DisableDefaultChecksumValidation { get; set; }
    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Gets or sets the maximum SDK retry count.</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Gets or sets the multipart upload part size in bytes.</summary>
    public int MultipartPartSizeBytes { get; set; } = 16 * 1024 * 1024;
    /// <summary>Gets or sets the size at which multipart upload begins.</summary>
    public long MultipartThresholdBytes { get; set; } = 64L * 1024 * 1024;

    /// <inheritdoc />
    public override string MountRoot => Prefix;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
            yield return "Bucket is required";
        if (string.IsNullOrWhiteSpace(Region))
            yield return "Region is required";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (MaxRetries < 0)
            yield return "MaxRetries cannot be negative";
        if (MultipartPartSizeBytes is < 5 * 1024 * 1024 or > 512 * 1024 * 1024)
            yield return "MultipartPartSizeBytes must be between 5 MiB and 512 MiB";
        if (MultipartThresholdBytes < MultipartPartSizeBytes)
            yield return "MultipartThresholdBytes must be at least MultipartPartSizeBytes";

        var prefix = StoragePath.Normalize(Prefix ?? string.Empty);
        if (prefix.IsFailure)
            yield return "Prefix is invalid";

        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                yield return "ServiceUrl must be an absolute HTTP(S) URL";
            }
            else
            {
                if (uri.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) &&
                    uri.Scheme != Uri.UriSchemeHttps)
                {
                    yield return "Cloudflare R2 endpoints require HTTPS";
                }
                else if (uri.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp)
                {
                    yield return "HTTP S3-compatible endpoints require AllowInsecureHttp=true";
                }

                if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                    yield return "ServiceUrl cannot contain user info, a query string, or a fragment";
            }
        }

        if (AuthenticationMode == S3AuthenticationMode.StaticCredentials &&
            (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)))
        {
            yield return "StaticCredentials requires AccessKey and SecretKey";
        }
    }
}
