using CodeLogic.Core.Configuration;

namespace CL.SQLite.Models;

[ConfigSection("sqlite")]
public class SQLiteConfig : ConfigModelBase
{
    public Dictionary<string, SQLiteDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new SQLiteDatabaseConfig()
    };

    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Databases.Count == 0)
            errors.Add("At least one database configuration is required");

        foreach (var kvp in Databases)
        {
            var result = kvp.Value.Validate();
            if (!result.IsValid)
                errors.Add($"Database '{kvp.Key}': {string.Join(", ", result.Errors)}");
        }

        return errors.Any() ? ConfigValidationResult.Invalid(errors) : ConfigValidationResult.Valid();
    }
}

public class SQLiteDatabaseConfig
{
    [ConfigField(Label = "Enabled", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    [ConfigField(Label = "Database Path", Required = true,
        Description = "Absolute path or relative to the library data directory.",
        Placeholder = "database.db", RequiresRestart = true, Group = "Connection", Order = 10)]
    public string DatabasePath { get; set; } = "database.db";

    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 3600,
        Group = "Timeouts", Order = 20, Collapsed = true)]
    public uint ConnectionTimeoutSeconds { get; set; } = 30;

    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 7200,
        Group = "Timeouts", Order = 21, Collapsed = true)]
    public uint CommandTimeoutSeconds { get; set; } = 120;

    [ConfigField(Label = "Skip Table Sync",
        Description = "Turn off automatic schema sync for this database.",
        Group = "Schema", Order = 30)]
    public bool SkipTableSync { get; set; } = false;

    [ConfigField(Label = "Cache Mode",
        Description = "Default / Private / Shared — see SQLite docs.",
        RequiresRestart = true, Group = "Advanced", Order = 40, Collapsed = true)]
    public CacheMode CacheMode { get; set; } = CacheMode.Default;

    [ConfigField(Label = "Use WAL Journal",
        Description = "Write-Ahead Logging — better concurrency, recommended.",
        RequiresRestart = true, Group = "Advanced", Order = 41, Collapsed = true)]
    public bool UseWAL { get; set; } = true;

    [ConfigField(Label = "Enable Foreign Keys",
        RequiresRestart = true, Group = "Advanced", Order = 42, Collapsed = true)]
    public bool EnableForeignKeys { get; set; } = true;

    [ConfigField(Label = "Max Pool Size", Min = 1,
        RequiresRestart = true, Group = "Advanced", Order = 43, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 10;

    [ConfigField(Label = "Slow Query Threshold (ms)", Min = 0,
        Group = "Advanced", Order = 44, Collapsed = true)]
    public int SlowQueryThresholdMs { get; set; } = 500;

    public ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DatabasePath)) errors.Add("DatabasePath is required");
        if (MaxPoolSize < 1) errors.Add("MaxPoolSize must be >= 1");
        return errors.Any() ? ConfigValidationResult.Invalid(errors) : ConfigValidationResult.Valid();
    }
}

public enum CacheMode
{
    Default,
    Private,
    Shared
}
