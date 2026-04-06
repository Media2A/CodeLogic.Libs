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
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int ConnectionTimeout { get; set; } = 30;
    public int CommandTimeout { get; set; } = 30;
    public int MinPoolSize { get; set; } = 5;
    public int MaxPoolSize { get; set; } = 100;
    public int MaxIdleTime { get; set; } = 60;
    public SslMode SslMode { get; set; } = SslMode.Prefer;
    public bool AllowDestructiveSync { get; set; } = false;
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
