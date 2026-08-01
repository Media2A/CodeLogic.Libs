using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum S3AuthenticationMode
{
    DefaultCredentialChain,
    StaticCredentials
}

[ConfigSection("storage.s3")]
public sealed class S3StorageConfig : ProviderStorageConfigBase<S3ConnectionConfig> { }

public sealed class S3ConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Bucket", Required = true, Group = "Connection", Order = 10)]
    public string Bucket { get; set; } = string.Empty;

    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    [ConfigField(Label = "Service URL", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? ServiceUrl { get; set; }

    [ConfigField(Label = "Region", Group = "Connection", Order = 13)]
    public string Region { get; set; } = "us-east-1";

    public S3AuthenticationMode AuthenticationMode { get; set; } = S3AuthenticationMode.DefaultCredentialChain;

    [ConfigField(Label = "Access Key", Secret = true, Group = "Credentials", Order = 20)]
    public string? AccessKey { get; set; }

    [ConfigField(Label = "Secret Key", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? SecretKey { get; set; }

    [ConfigField(Label = "Session Token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? SessionToken { get; set; }

    public bool ForcePathStyle { get; set; }
    public bool DisablePayloadSigning { get; set; }
    public bool DisableDefaultChecksumValidation { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;

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
            else if (uri.Host.EndsWith(".r2.cloudflarestorage.com", StringComparison.OrdinalIgnoreCase) &&
                     uri.Scheme != Uri.UriSchemeHttps)
            {
                yield return "Cloudflare R2 endpoints require HTTPS";
            }
        }

        if (AuthenticationMode == S3AuthenticationMode.StaticCredentials &&
            (string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)))
        {
            yield return "StaticCredentials requires AccessKey and SecretKey";
        }
    }
}
