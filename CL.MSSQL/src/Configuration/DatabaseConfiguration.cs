using CL.MSSQL.Models;
using CodeLogic.Core.Configuration;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Configuration;

[ConfigSection("mssql")]
public sealed class DatabaseConfiguration : ConfigModelBase
{
    public Dictionary<string, SqlServerDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new()
    };

    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();
        if (Databases.Count == 0) errors.Add("At least one database configuration is required");
        foreach (var (name, database) in Databases)
        {
            var result = database.Validate();
            if (!result.IsValid) errors.Add($"Database '{name}': {string.Join(", ", result.Errors)}");
        }
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}

/// <summary>Authentication mode used by structured SQL Server configuration.</summary>
public enum SqlServerAuthenticationMode
{
    SqlLogin,
    IntegratedSecurity
}

/// <summary>Per-database SQL Server and Azure SQL settings.</summary>
public sealed class SqlServerDatabaseConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>An authoritative provider connection string. When set, all structured connection fields are ignored.</summary>
    [ConfigField(Label = "Connection String", InputType = ConfigInputType.Password, Secret = true, RequiresRestart = true, Group = "Connection")]
    public string? ConnectionString { get; set; }

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1433;
    public string? Instance { get; set; }
    public string Database { get; set; } = string.Empty;
    public SqlServerAuthenticationMode AuthenticationMode { get; set; } = SqlServerAuthenticationMode.SqlLogin;
    public string Username { get; set; } = string.Empty;

    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true, RequiresRestart = true, Group = "Connection")]
    public string Password { get; set; } = string.Empty;

    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    public bool EnablePooling { get; set; } = true;
    public int MinPoolSize { get; set; }
    public int MaxPoolSize { get; set; } = 100;
    public int ConnectionLifetime { get; set; } = 300;
    public int ConnectionTimeout { get; set; } = 30;
    public int CommandTimeout { get; set; } = 30;
    public string DefaultSchema { get; set; } = "dbo";
    public SyncMode SyncMode { get; set; } = SyncMode.Production;
    public SchemaSyncLevel SchemaSyncLevel { get; set; } = SchemaSyncLevel.Safe;
    public bool AllowDestructiveSync { get; set; }
    public bool IsMigrationMode => SyncMode == SyncMode.Migration;
    public SchemaSyncLevel EffectiveSyncLevel => SyncMode switch
    {
        SyncMode.Developer or SyncMode.Migration => SchemaSyncLevel.Full,
        _ => AllowDestructiveSync ? SchemaSyncLevel.Full : SchemaSyncLevel
    };
    public string? BackupDirectory { get; set; }
    public int SlowQueryThresholdMs { get; set; } = 1000;
    public bool? CacheEnabledOverride { get; set; }
    public int QueryTimeoutMs { get; set; } = 30_000;
    public int MaxBatchInsertSize { get; set; } = 500;
    public int MaxInClauseValues { get; set; } = 1_000;
    public int PreparedStatementCacheSize { get; set; } = 256;
    public int TransientRetryCount { get; set; } = 3;
    public int TransientRetryBaseDelayMs { get; set; } = 50;
    public int N1DetectorThreshold { get; set; }
    public bool CaptureExplainOnSlowQuery { get; set; } = true;
    public int DefaultStringSize { get; set; } = 255;

    public string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;

        var server = string.IsNullOrWhiteSpace(Instance)
            ? $"{Host},{Port}"
            : $"{Host}\\{Instance}";
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = Database,
            IntegratedSecurity = AuthenticationMode == SqlServerAuthenticationMode.IntegratedSecurity,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            Pooling = EnablePooling,
            MinPoolSize = MinPoolSize,
            MaxPoolSize = MaxPoolSize,
            LoadBalanceTimeout = ConnectionLifetime,
            ConnectTimeout = ConnectionTimeout,
            CommandTimeout = CommandTimeout,
            ConnectRetryCount = TransientRetryCount
        };
        if (AuthenticationMode == SqlServerAuthenticationMode.SqlLogin)
        {
            builder.UserID = Username;
            builder.Password = Password;
        }
        return builder.ConnectionString;
    }

    public ConfigValidationResult Validate()
    {
        if (!Enabled || !string.IsNullOrWhiteSpace(ConnectionString)) return ConfigValidationResult.Valid();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) errors.Add("Host is required");
        if (Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535");
        if (string.IsNullOrWhiteSpace(Database)) errors.Add("Database name is required");
        if (AuthenticationMode == SqlServerAuthenticationMode.SqlLogin && string.IsNullOrWhiteSpace(Username))
            errors.Add("Username is required for SQL login authentication");
        if (MinPoolSize < 0) errors.Add("MinPoolSize cannot be negative");
        if (MaxPoolSize < 1 || MaxPoolSize < MinPoolSize) errors.Add("MaxPoolSize must be positive and at least MinPoolSize");
        if (ConnectionTimeout < 0 || CommandTimeout < 0) errors.Add("Connection and command timeouts cannot be negative");
        if (TransientRetryCount is < 0 or > 255) errors.Add("TransientRetryCount must be between 0 and 255");
        if (TransientRetryBaseDelayMs < 0) errors.Add("TransientRetryBaseDelayMs cannot be negative");
        if (MaxBatchInsertSize < 1) errors.Add("MaxBatchInsertSize must be positive");
        if (string.IsNullOrWhiteSpace(DefaultSchema)) errors.Add("DefaultSchema is required");
        return errors.Count == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}
