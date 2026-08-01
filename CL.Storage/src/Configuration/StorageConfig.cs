using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

[ConfigSection("storage")]
public sealed class StorageConfig : ConfigModelBase
{
    [ConfigField(Label = "Enabled", Description = "Master switch for provider-neutral storage.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    [ConfigField(Label = "Default Connection", Required = true, Description = "Connection used when no ID is specified.", Group = "General", Order = 1)]
    public string DefaultConnection { get; set; } = "Default";

    [ConfigField(Label = "Health Check Timeout (s)", Min = 1, Max = 600, Group = "Health", Order = 10)]
    public int HealthCheckTimeoutSeconds { get; set; } = 10;

    [ConfigField(Label = "Maximum Buffered Download (bytes)", Min = 1, Group = "Transfers", Order = 20)]
    public long MaxBufferedDownloadBytes { get; set; } = 67_108_864;

    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Enabled && string.IsNullOrWhiteSpace(DefaultConnection))
            errors.Add("DefaultConnection is required when storage is enabled");
        if (HealthCheckTimeoutSeconds <= 0)
            errors.Add("HealthCheckTimeoutSeconds must be greater than zero");
        if (MaxBufferedDownloadBytes <= 0)
            errors.Add("MaxBufferedDownloadBytes must be greater than zero");
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
