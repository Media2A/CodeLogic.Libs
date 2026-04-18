using CodeLogic.Core.Configuration;

namespace CL.PostgreSQL.Models;

[ConfigSection("postgresql")]
public class PostgreSQLConfig : ConfigModelBase
{
    public Dictionary<string, DatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new DatabaseConfig()
    };

    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Databases.Count == 0)
            errors.Add("At least one database configuration is required");

        foreach (var kvp in Databases)
        {
            var r = kvp.Value.Validate();
            if (!r.IsValid)
                errors.Add($"Database '{kvp.Key}': {string.Join(", ", r.Errors)}");
        }

        return errors.Any() ? ConfigValidationResult.Invalid(errors) : ConfigValidationResult.Valid();
    }
}

public class DatabaseConfig
{
    [ConfigField(Label = "Enabled", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    [ConfigField(Label = "Host", Required = true, Placeholder = "localhost",
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = "localhost";

    [ConfigField(Label = "Port", Min = 1, Max = 65535, RequiresRestart = true, Group = "Connection", Order = 11)]
    public int Port { get; set; } = 5432;

    [ConfigField(Label = "Database", Required = true, RequiresRestart = true, Group = "Connection", Order = 12)]
    public string Database { get; set; } = string.Empty;

    [ConfigField(Label = "Username", Required = true, RequiresRestart = true, Group = "Connection", Order = 13)]
    public string Username { get; set; } = string.Empty;

    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 14)]
    public string Password { get; set; } = string.Empty;

    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 600, Group = "Timeouts", Order = 20, Collapsed = true)]
    public int ConnectionTimeout { get; set; } = 30;

    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 3600, Group = "Timeouts", Order = 21, Collapsed = true)]
    public int CommandTimeout { get; set; } = 30;

    [ConfigField(Label = "Min Pool Size", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 30, Collapsed = true)]
    public int MinPoolSize { get; set; } = 5;

    [ConfigField(Label = "Max Pool Size", Min = 1, RequiresRestart = true, Group = "Pooling", Order = 31, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 100;

    [ConfigField(Label = "Max Idle Time (s)", Min = 0, Group = "Pooling", Order = 32, Collapsed = true)]
    public int MaxIdleTime { get; set; } = 60;

    [ConfigField(Label = "SSL Mode",
        Description = "Disable / Allow / Prefer / Require / VerifyCA / VerifyFull.",
        RequiresRestart = true, Group = "Security", Order = 40)]
    public SslMode SslMode { get; set; } = SslMode.Prefer;

    [ConfigField(Label = "Allow Destructive Sync",
        Description = "Dev-only: allow DROP operations during schema sync.",
        Group = "Schema Sync", Order = 50, Collapsed = true)]
    public bool AllowDestructiveSync { get; set; } = false;

    [ConfigField(Label = "Slow Query Threshold (ms)", Min = 0,
        Group = "Advanced", Order = 60, Collapsed = true)]
    public int SlowQueryThresholdMs { get; set; } = 1000;

    public ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
            errors.Add("Host is required");

        if (string.IsNullOrWhiteSpace(Database))
            errors.Add("Database name is required");

        if (string.IsNullOrWhiteSpace(Username))
            errors.Add("Username is required");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }

    public string BuildConnectionString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Host={Host};Port={Port};Database={Database};");
        sb.Append($"Username={Username};Password={Password};");
        sb.Append($"Connection Timeout={ConnectionTimeout};Command Timeout={CommandTimeout};");
        sb.Append($"Minimum Pool Size={MinPoolSize};Maximum Pool Size={MaxPoolSize};");
        sb.Append($"SSL Mode={SslMode};Pooling=true;");
        return sb.ToString();
    }
}

public enum SslMode
{
    Disable,
    Allow,
    Prefer,
    Require,
    VerifyCA,
    VerifyFull
}
