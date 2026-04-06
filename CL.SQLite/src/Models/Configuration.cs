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
    public bool Enabled { get; set; } = true;
    public string DatabasePath { get; set; } = "database.db";
    public uint ConnectionTimeoutSeconds { get; set; } = 30;
    public uint CommandTimeoutSeconds { get; set; } = 120;
    public bool SkipTableSync { get; set; } = false;
    public CacheMode CacheMode { get; set; } = CacheMode.Default;
    public bool UseWAL { get; set; } = true;
    public bool EnableForeignKeys { get; set; } = true;
    public int MaxPoolSize { get; set; } = 10;
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
