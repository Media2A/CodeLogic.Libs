using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

[ConfigSection("storage.local")]
public sealed class LocalStorageConfig : ConfigModelBase
{
    [ConfigField(Label = "Connections", Description = "Named local or mounted filesystem roots.", Group = "Connections", Order = 0)]
    public Dictionary<string, LocalConnectionConfig> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        foreach (var connection in Connections)
        {
            if (string.IsNullOrWhiteSpace(connection.Key))
                errors.Add("Local connection IDs cannot be blank");
            var validation = connection.Value?.Validate() ?? ConfigValidationResult.Invalid("Connection configuration is required");
            if (!validation.IsValid)
                errors.Add($"Connection '{connection.Key}': {string.Join(", ", validation.Errors)}");
        }
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}

public sealed class LocalConnectionConfig
{
    [ConfigField(Label = "Enabled", Description = "Enable this local storage connection.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    [ConfigField(Label = "Root Path", Required = true, Description = "Local path or mounted UNC root exposed by this connection.", Placeholder = "C:\\data or \\\\server\\share", RequiresRestart = true, Group = "Connection", Order = 10)]
    public string RootPath { get; set; } = string.Empty;

    [ConfigField(Label = "Follow Links", Description = "Allow links only when their resolved target remains under the configured root.", RequiresRestart = true, Group = "Security", Order = 20)]
    public bool FollowLinks { get; set; }

    [ConfigField(Label = "Timeout (s)", Min = 1, Max = 600, Group = "Advanced", Order = 30, Collapsed = true)]
    public int TimeoutSeconds { get; set; } = 30;

    public ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Enabled && string.IsNullOrWhiteSpace(RootPath))
            errors.Add("RootPath is required for an enabled local connection");
        if (TimeoutSeconds <= 0)
            errors.Add("TimeoutSeconds must be greater than zero");
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
