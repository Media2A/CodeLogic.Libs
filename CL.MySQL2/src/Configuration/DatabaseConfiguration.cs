using CodeLogic.Core.Configuration;
using MySqlConnector;

namespace CL.MySQL2.Configuration;

/// <summary>
/// Root configuration for <c>CL.MySQL2</c>.
/// Serialized to / from <c>config.mysql.json</c> in the library's config directory.
/// </summary>
[ConfigSection("mysql")]
public sealed class DatabaseConfiguration : ConfigModelBase
{
    /// <summary>
    /// Named database configurations keyed by connection ID.
    /// </summary>
    public Dictionary<string, MySqlDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new MySqlDatabaseConfig()
    };

    /// <summary>
    /// Validates all configured databases.
    /// </summary>
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

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// Per-database MySQL connection settings.
/// </summary>
public sealed class MySqlDatabaseConfig
{
    /// <summary>Whether this database connection is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>MySQL server hostname or IP address.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>MySQL server port. Default: 3306.</summary>
    public int Port { get; set; } = 3306;

    /// <summary>Database (schema) name.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>MySQL username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>MySQL password.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether connection pooling is enabled. Default: true.</summary>
    public bool EnablePooling { get; set; } = true;

    /// <summary>Minimum number of pooled connections. Default: 1.</summary>
    public int MinPoolSize { get; set; } = 1;

    /// <summary>Maximum number of pooled connections. Default: 100.</summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Maximum connection lifetime in seconds. Default: 300.</summary>
    public int ConnectionLifetime { get; set; } = 300;

    /// <summary>Connection timeout in seconds. Default: 30.</summary>
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>Command timeout in seconds. Default: 30.</summary>
    public int CommandTimeout { get; set; } = 30;

    /// <summary>Whether to require SSL/TLS for connections. Default: false.</summary>
    public bool EnableSsl { get; set; } = false;

    /// <summary>Path to a client SSL certificate file. Optional.</summary>
    public string? SslCertificatePath { get; set; }

    /// <summary>Whether to allow servers without RSA public key. Default: false.</summary>
    public bool AllowPublicKeyRetrieval { get; set; } = false;

    /// <summary>Connection character set. Default: "utf8mb4".</summary>
    public string CharacterSet { get; set; } = "utf8mb4";

    /// <summary>Default collation. Default: "utf8mb4_unicode_ci".</summary>
    public string Collation { get; set; } = "utf8mb4_unicode_ci";

    /// <summary>
    /// When false (default), the table sync will never DROP or TRUNCATE existing data.
    /// Set to true only in development environments.
    /// </summary>
    public bool AllowDestructiveSync { get; set; } = false;

    /// <summary>
    /// Directory for schema backup files.
    /// Null = <c>DataDirectory/backups</c>.
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Queries exceeding this threshold (ms) are logged as slow queries. Default: 1000.</summary>
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>
    /// Builds and returns the MySqlConnector connection string from the current configuration.
    /// </summary>
    public string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = (uint)Port,
            Database = Database,
            UserID = Username,
            Password = Password,
            Pooling = EnablePooling,
            MinimumPoolSize = (uint)MinPoolSize,
            MaximumPoolSize = (uint)MaxPoolSize,
            ConnectionLifeTime = (uint)ConnectionLifetime,
            ConnectionTimeout = (uint)ConnectionTimeout,
            DefaultCommandTimeout = (uint)CommandTimeout,
            AllowPublicKeyRetrieval = AllowPublicKeyRetrieval,
            CharacterSet = CharacterSet,
            SslMode = EnableSsl ? MySqlSslMode.Required : MySqlSslMode.None
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Validates the per-database configuration.
    /// </summary>
    public ConfigValidationResult Validate()
    {
        if (!Enabled)
            return ConfigValidationResult.Valid();

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Host))
            errors.Add("Host is required");

        if (Port is < 1 or > 65535)
            errors.Add("Port must be between 1 and 65535");

        if (string.IsNullOrWhiteSpace(Database))
            errors.Add("Database name is required");

        if (string.IsNullOrWhiteSpace(Username))
            errors.Add("Username is required");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}
