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
    public string ConnectionId { get; set; } = "Default";

    /// <summary>AWS / MinIO access key ID.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>AWS / MinIO secret access key.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>
    /// S3 service endpoint URL.
    /// Use <c>https://s3.amazonaws.com</c> for AWS or a MinIO URL such as <c>http://localhost:9000</c>.
    /// </summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>
    /// Public-facing base URL used to build object URLs, e.g. <c>https://cdn.example.com</c>.
    /// When empty, object public URLs are not generated.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>AWS region name (e.g. <c>us-east-1</c>). Defaults to <c>us-east-1</c>.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Default bucket name used when no bucket is specified in an operation.</summary>
    public string DefaultBucket { get; set; } = "";

    /// <summary>
    /// Whether to use path-style addressing (<c>http://host/bucket/key</c> instead of <c>http://bucket.host/key</c>).
    /// Required for MinIO and most non-AWS S3-compatible services.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Whether to use HTTPS for the connection.</summary>
    public bool UseHttps { get; set; } = true;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum number of retry attempts on transient failures.</summary>
    public int MaxRetries { get; set; } = 3;

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
