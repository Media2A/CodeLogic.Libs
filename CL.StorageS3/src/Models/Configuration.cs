using Amazon;
using Amazon.S3;
using CodeLogic.Core.Configuration;

namespace CL.StorageS3.Models;

/// <summary>
/// Main configuration model for the CL.StorageS3 library.
/// Serialized as <c>config.storages3.json</c> in the library's config directory.
/// </summary>
[ConfigSection("storages3")]
public class StorageS3Config : ConfigModelBase
{
    /// <summary>Whether the StorageS3 library is enabled.</summary>
    [ConfigField(Label = "Enabled", Description = "Master switch for S3/MinIO storage.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>One or more S3/MinIO connection configurations.</summary>
    public List<S3ConnectionConfig> Connections { get; set; } = [];

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Enabled && Connections.Count == 0)
            errors.Add("At least one connection must be configured when StorageS3 is enabled");

        var ids = Connections.Select(c => c.ConnectionId).ToList();
        var duplicates = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            errors.Add($"Duplicate connection IDs found: {string.Join(", ", duplicates)}");

        foreach (var conn in Connections)
        {
            if (!conn.IsValid())
                errors.Add($"Connection '{conn.ConnectionId}' is missing required fields (AccessKey, SecretKey, ServiceUrl)");

            if (conn.TimeoutSeconds <= 0)
                errors.Add($"Connection '{conn.ConnectionId}': TimeoutSeconds must be > 0");

            if (conn.MaxRetries < 0)
                errors.Add($"Connection '{conn.ConnectionId}': MaxRetries must be >= 0");
        }

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Configuration for a single S3 or S3-compatible (MinIO, etc.) connection.
/// </summary>
public class S3ConnectionConfig
{
    /// <summary>Unique identifier for this connection. Used to look it up via the connection manager.</summary>
    [ConfigField(Label = "Connection ID", Required = true, Description = "Unique identifier used to look up this connection.",
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string ConnectionId { get; set; } = "Default";

    /// <summary>AWS / MinIO access key ID.</summary>
    [ConfigField(Label = "Access Key", Required = true, Secret = true,
        Description = "AWS / MinIO / R2 access key ID.",
        RequiresRestart = true, Group = "Credentials", Order = 11)]
    public string AccessKey { get; set; } = "";

    /// <summary>AWS / MinIO secret access key.</summary>
    [ConfigField(Label = "Secret Key", InputType = ConfigInputType.Password, Secret = true,
        Required = true, Description = "AWS / MinIO / R2 secret access key.",
        RequiresRestart = true, Group = "Credentials", Order = 12)]
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// S3 service endpoint URL.
    /// Use <c>https://s3.amazonaws.com</c> for AWS or a MinIO URL such as <c>http://localhost:9000</c>.
    /// </summary>
    [ConfigField(Label = "Service URL", InputType = ConfigInputType.Url, Required = true,
        Description = "AWS: https://s3.amazonaws.com — MinIO: http://host:9000 — R2: https://<account>.r2.cloudflarestorage.com",
        Placeholder = "https://s3.amazonaws.com", RequiresRestart = true, Group = "Connection", Order = 13)]
    public string ServiceUrl { get; set; } = "";

    /// <summary>
    /// Public-facing base URL used to build object URLs, e.g. <c>https://cdn.example.com</c>.
    /// When empty, object public URLs are not generated.
    /// </summary>
    [ConfigField(Label = "Public URL", InputType = ConfigInputType.Url,
        Description = "Base URL used when generating public object links. Leave blank if private.",
        Placeholder = "https://cdn.example.com", Group = "Connection", Order = 14)]
    public string PublicUrl { get; set; } = "";

    /// <summary>AWS region name (e.g. <c>us-east-1</c>). Defaults to <c>us-east-1</c>.</summary>
    [ConfigField(Label = "Region", Description = "AWS region name (ignored when Service URL is set).",
        RequiresRestart = true, Group = "Connection", Order = 15)]
    public string Region { get; set; } = "us-east-1";

    /// <summary>Default bucket name used when no bucket is specified in an operation.</summary>
    [ConfigField(Label = "Default Bucket", Placeholder = "my-app-assets",
        Description = "Used when code doesn't specify a bucket explicitly.",
        Group = "Connection", Order = 16)]
    public string DefaultBucket { get; set; } = "";

    /// <summary>
    /// Whether to use path-style addressing (<c>http://host/bucket/key</c> instead of <c>http://bucket.host/key</c>).
    /// Required for MinIO and most non-AWS S3-compatible services.
    /// </summary>
    [ConfigField(Label = "Force Path Style", Description = "Required for MinIO and most non-AWS services.",
        RequiresRestart = true, Group = "Advanced", Order = 30, Collapsed = true)]
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Whether to use HTTPS for the connection.</summary>
    [ConfigField(Label = "Use HTTPS", RequiresRestart = true, Group = "Advanced", Order = 31, Collapsed = true)]
    public bool UseHttps { get; set; } = true;

    /// <summary>HTTP request timeout in seconds.</summary>
    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 600, Group = "Advanced", Order = 32, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum number of retry attempts on transient failures.</summary>
    [ConfigField(Label = "Max Retries", Min = 0, Max = 20, Group = "Advanced", Order = 33, Collapsed = true)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Disables chunked payload signing (STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER).
    /// Required for Cloudflare R2 and some other S3-compatible services that don't
    /// support the streaming signature format. Default: false (AWS/MinIO compatible).
    /// </summary>
    [ConfigField(Label = "Disable Payload Signing", Description = "Enable for Cloudflare R2 — disables STREAMING-AWS4 payload signing.",
        RequiresRestart = true, Group = "Advanced", Order = 34, Collapsed = true)]
    public bool DisablePayloadSigning { get; set; }

    /// <summary>
    /// Builds and returns a configured <see cref="AmazonS3Client"/> for this connection.
    /// </summary>
    public AmazonS3Client BuildClient()
    {
        var s3Config = new AmazonS3Config
        {
            ForcePathStyle = ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds),
            MaxErrorRetry = MaxRetries
        };

        if (!string.IsNullOrWhiteSpace(ServiceUrl))
        {
            s3Config.ServiceURL = ServiceUrl;
        }
        else
        {
            var region = RegionEndpoint.GetBySystemName(Region);
            s3Config.RegionEndpoint = region;
        }

        return new AmazonS3Client(AccessKey, SecretKey, s3Config);
    }

    /// <summary>
    /// Returns <see langword="true"/> when all required fields have values.
    /// </summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey) &&
        !string.IsNullOrWhiteSpace(ServiceUrl);
}
