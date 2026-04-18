using CL.MySQL2.Models;
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
    [ConfigField(Label = "Enabled", Description = "Turn this connection on or off without removing it.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>MySQL server hostname or IP address.</summary>
    [ConfigField(Label = "Host", Description = "MySQL server hostname or IP address.", Required = true, Placeholder = "localhost",
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = "localhost";

    /// <summary>MySQL server port. Default: 3306.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, RequiresRestart = true, Group = "Connection", Order = 11)]
    public int Port { get; set; } = 3306;

    /// <summary>Database (schema) name.</summary>
    [ConfigField(Label = "Database", Description = "Name of the MySQL schema to use.", Required = true,
        RequiresRestart = true, Group = "Connection", Order = 12)]
    public string Database { get; set; } = string.Empty;

    /// <summary>MySQL username.</summary>
    [ConfigField(Label = "Username", Required = true, RequiresRestart = true, Group = "Connection", Order = 13)]
    public string Username { get; set; } = string.Empty;

    /// <summary>MySQL password.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 14)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether connection pooling is enabled. Default: true.</summary>
    [ConfigField(Label = "Enable Pooling", Description = "Use the MySqlConnector connection pool.",
        RequiresRestart = true, Group = "Pooling", Order = 20, Collapsed = true)]
    public bool EnablePooling { get; set; } = true;

    /// <summary>Minimum number of pooled connections. Default: 1.</summary>
    [ConfigField(Label = "Min Pool Size", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 21, Collapsed = true)]
    public int MinPoolSize { get; set; } = 1;

    /// <summary>Maximum number of pooled connections. Default: 100.</summary>
    [ConfigField(Label = "Max Pool Size", Min = 1, RequiresRestart = true, Group = "Pooling", Order = 22, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Maximum connection lifetime in seconds. Default: 300.</summary>
    [ConfigField(Label = "Connection Lifetime (s)", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 23, Collapsed = true)]
    public int ConnectionLifetime { get; set; } = 300;

    /// <summary>Connection timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 600, Group = "Timeouts", Order = 30, Collapsed = true)]
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>Command timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 3600, Group = "Timeouts", Order = 31, Collapsed = true)]
    public int CommandTimeout { get; set; } = 30;

    /// <summary>Whether to require SSL/TLS for connections. Default: false.</summary>
    [ConfigField(Label = "Enable SSL/TLS", RequiresRestart = true, Group = "Security", Order = 40)]
    public bool EnableSsl { get; set; } = false;

    /// <summary>Path to a client SSL certificate file. Optional.</summary>
    [ConfigField(Label = "SSL Certificate Path", Description = "Optional path to a client certificate file.",
        RequiresRestart = true, Group = "Security", Order = 41)]
    public string? SslCertificatePath { get; set; }

    /// <summary>Whether to allow servers without RSA public key. Default: false.</summary>
    [ConfigField(Label = "Allow Public Key Retrieval", Description = "Needed for some MySQL 8+ configurations.",
        RequiresRestart = true, Group = "Security", Order = 42)]
    public bool AllowPublicKeyRetrieval { get; set; } = false;

    /// <summary>Connection character set. Default: "utf8mb4".</summary>
    [ConfigField(Label = "Character Set", RequiresRestart = true, Group = "Advanced", Order = 50, Collapsed = true)]
    public string CharacterSet { get; set; } = "utf8mb4";

    /// <summary>Default collation. Default: "utf8mb4_unicode_ci".</summary>
    [ConfigField(Label = "Collation", RequiresRestart = true, Group = "Advanced", Order = 51, Collapsed = true)]
    public string Collation { get; set; } = "utf8mb4_unicode_ci";

    /// <summary>
    /// Controls how aggressively the table sync service reconciles the database
    /// schema with entity definitions. See <see cref="Models.SchemaSyncLevel"/>.
    /// Default: <see cref="Models.SchemaSyncLevel.Safe"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>None</b> — no sync at all.</item>
    ///   <item><b>Safe</b> — add missing columns/indexes/FKs + modify existing columns (grow VARCHAR, change default, toggle NULL). Never drops.</item>
    ///   <item><b>Additive</b> — Safe + drop indexes and foreign keys no longer in the model.</item>
    ///   <item><b>Full</b> — Additive + drop columns no longer in the model, and allow DROP TABLE rebuild. Development only.</item>
    /// </list>
    /// </remarks>
    [ConfigField(Label = "Schema Sync Level",
        Description = "How aggressively the table sync aligns DB schema with entity models. Safe = additive only (default). Additive = also drops removed indexes/FKs. Full = also drops removed columns (dev only).",
        Group = "Schema Sync", Order = 60)]
    public SchemaSyncLevel SchemaSyncLevel { get; set; } = SchemaSyncLevel.Safe;

    /// <summary>
    /// Legacy flag. When true, sync operates at <see cref="Models.SchemaSyncLevel.Full"/>
    /// regardless of <see cref="SchemaSyncLevel"/>. Prefer setting SchemaSyncLevel directly.
    /// </summary>
    [ConfigField(Label = "Allow Destructive Sync (legacy)",
        Description = "Deprecated — use Schema Sync Level = Full instead.",
        Group = "Schema Sync", Order = 61, Collapsed = true)]
    public bool AllowDestructiveSync { get; set; } = false;

    /// <summary>
    /// Effective sync level, resolving the legacy <see cref="AllowDestructiveSync"/> flag.
    /// </summary>
    public SchemaSyncLevel EffectiveSyncLevel =>
        AllowDestructiveSync ? SchemaSyncLevel.Full : SchemaSyncLevel;

    /// <summary>
    /// Directory for schema backup files.
    /// Null = <c>DataDirectory/backups</c>.
    /// </summary>
    [ConfigField(Label = "Backup Directory", Description = "Override where schema backups are stored. Blank = default data/backups folder.",
        Group = "Schema Sync", Order = 62, Collapsed = true)]
    public string? BackupDirectory { get; set; }

    /// <summary>Queries exceeding this threshold (ms) are logged as slow queries. Default: 1000.</summary>
    [ConfigField(Label = "Slow Query Threshold (ms)", Min = 0,
        Description = "Queries taking longer than this are logged as slow.",
        Group = "Advanced", Order = 52, Collapsed = true)]
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
