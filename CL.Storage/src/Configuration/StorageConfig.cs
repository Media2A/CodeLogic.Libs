using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Defines library-wide storage defaults and transfer safety limits.</summary>
[ConfigSection("storage")]
public sealed class StorageConfig : ConfigModelBase
{
    /// <summary>Gets or sets whether the storage library is enabled.</summary>
    [ConfigField(Label = "Enabled", Description = "Master switch for provider-neutral storage.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the connection ID used when callers omit one.</summary>
    [ConfigField(Label = "Default Connection", Required = true, Description = "Connection used when no ID is specified.", Group = "General", Order = 1)]
    public string DefaultConnection { get; set; } = "Default";

    /// <summary>Gets or sets the maximum duration of a connection health check, in seconds.</summary>
    [ConfigField(Label = "Health Check Timeout (s)", Min = 1, Max = 600, Group = "Health", Order = 10)]
    public int HealthCheckTimeoutSeconds { get; set; } = 10;

    /// <summary>Gets or sets the maximum size accepted by buffered download helpers.</summary>
    [ConfigField(Label = "Maximum Buffered Download (bytes)", Min = 1, Group = "Transfers", Order = 20)]
    public long MaxBufferedDownloadBytes { get; set; } = 67_108_864;

    /// <summary>Gets or sets the combined upload speed limit across all connections, in bytes per second; null or zero means unlimited.</summary>
    [ConfigField(Label = "Total upload limit (bytes/s)", Min = 0, Group = "Transfers", Order = 21)]
    public long? MaxTotalUploadBytesPerSecond { get; set; }

    /// <summary>Gets or sets the combined download speed limit across all connections, in bytes per second; null or zero means unlimited.</summary>
    [ConfigField(Label = "Total download limit (bytes/s)", Min = 0, Group = "Transfers", Order = 22)]
    public long? MaxTotalDownloadBytesPerSecond { get; set; }

    /// <inheritdoc />
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Enabled && string.IsNullOrWhiteSpace(DefaultConnection))
            errors.Add("DefaultConnection is required when storage is enabled");
        if (HealthCheckTimeoutSeconds <= 0)
            errors.Add("HealthCheckTimeoutSeconds must be greater than zero");
        if (MaxBufferedDownloadBytes <= 0)
            errors.Add("MaxBufferedDownloadBytes must be greater than zero");
        if (MaxTotalUploadBytesPerSecond < 0 || MaxTotalDownloadBytesPerSecond < 0)
            errors.Add("Total transfer limits cannot be negative");
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
