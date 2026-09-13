using CL.PostgreSQL.Models;
using CodeLogic.Core.Configuration;
using Npgsql;

namespace CL.PostgreSQL.Configuration;

/// <summary>
/// Root configuration for <c>CL.PostgreSQL</c>.
/// Serialized to / from <c>config.postgresql.json</c> in the library's config directory.
/// </summary>
[ConfigSection("postgresql")]
public sealed class DatabaseConfiguration : ConfigModelBase
{
    /// <summary>
    /// Named database configurations keyed by connection ID.
    /// </summary>
    public Dictionary<string, PostgreSqlDatabaseConfig> Databases { get; set; } = new()
    {
        ["Default"] = new PostgreSqlDatabaseConfig()
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
/// Per-database PostgreSQL connection settings.
/// </summary>
public sealed class PostgreSqlDatabaseConfig
{
    /// <summary>Whether this database connection is active.</summary>
    [ConfigField(Label = "Enabled", Description = "Turn this connection on or off without removing it.", Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>PostgreSQL server hostname or IP address.</summary>
    [ConfigField(Label = "Host", Description = "PostgreSQL server hostname or IP address.", Required = true, Placeholder = "localhost",
        RequiresRestart = true, Group = "Connection", Order = 10)]
    public string Host { get; set; } = "localhost";

    /// <summary>PostgreSQL server port. Default: 5432.</summary>
    [ConfigField(Label = "Port", Min = 1, Max = 65535, RequiresRestart = true, Group = "Connection", Order = 11)]
    public int Port { get; set; } = 5432;

    /// <summary>
    /// Database name. Distinct from a PostgreSQL schema: a connection targets one
    /// database, and schemas are namespaces within it (see DefaultSchema).
    /// </summary>
    [ConfigField(Label = "Database", Description = "Name of the PostgreSQL database to connect to.", Required = true,
        RequiresRestart = true, Group = "Connection", Order = 12)]
    public string Database { get; set; } = string.Empty;

    /// <summary>PostgreSQL username.</summary>
    [ConfigField(Label = "Username", Required = true, RequiresRestart = true, Group = "Connection", Order = 13)]
    public string Username { get; set; } = string.Empty;

    /// <summary>PostgreSQL password.</summary>
    [ConfigField(Label = "Password", InputType = ConfigInputType.Password, Secret = true,
        RequiresRestart = true, Group = "Connection", Order = 14)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Whether connection pooling is enabled. Default: true.</summary>
    [ConfigField(Label = "Enable Pooling", Description = "Use the Npgsql connection pool.",
        RequiresRestart = true, Group = "Pooling", Order = 20, Collapsed = true)]
    public bool EnablePooling { get; set; } = true;

    /// <summary>Minimum number of pooled connections. Default: 1.</summary>
    [ConfigField(Label = "Min Pool Size", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 21, Collapsed = true)]
    public int MinPoolSize { get; set; } = 1;

    /// <summary>Maximum number of pooled connections. Default: 100.</summary>
    [ConfigField(Label = "Max Pool Size", Min = 1, RequiresRestart = true, Group = "Pooling", Order = 22, Collapsed = true)]
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// How long a pooled connection may sit idle before it is closed, in seconds.
    /// Maps to Npgsql's <c>Connection Idle Lifetime</c>. Default: 300.
    /// </summary>
    [ConfigField(Label = "Connection Lifetime (s)", Min = 0, RequiresRestart = true, Group = "Pooling", Order = 23, Collapsed = true)]
    public int ConnectionLifetime { get; set; } = 300;

    /// <summary>Connection timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Connect Timeout (s)", Min = 1, Max = 600, Group = "Timeouts", Order = 30, Collapsed = true)]
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>Command timeout in seconds. Default: 30.</summary>
    [ConfigField(Label = "Command Timeout (s)", Min = 1, Max = 3600, Group = "Timeouts", Order = 31, Collapsed = true)]
    public int CommandTimeout { get; set; } = 30;

    /// <summary>
    /// TLS negotiation mode. Defaults to <see cref="PostgreSqlSslMode.Prefer"/>, which
    /// encrypts when the server offers it but does not verify the certificate. Use
    /// <see cref="PostgreSqlSslMode.VerifyFull"/> in production: it is the only mode that
    /// authenticates the server and so the only one that resists an active attacker.
    /// </summary>
    [ConfigField(Label = "SSL Mode",
        Description = "Disable / Allow / Prefer / Require / VerifyCA / VerifyFull. Prefer encrypts but does not verify; use VerifyFull in production.",
        RequiresRestart = true, Group = "Security", Order = 40)]
    public PostgreSqlSslMode SslMode { get; set; } = PostgreSqlSslMode.Prefer;

    /// <summary>Path to a client certificate (PEM) for certificate authentication. Optional.</summary>
    [ConfigField(Label = "Client Certificate Path", Description = "Optional path to a client certificate (PEM).",
        RequiresRestart = true, Group = "Security", Order = 41)]
    public string? SslCertificatePath { get; set; }

    /// <summary>Path to the client certificate's private key. Optional.</summary>
    [ConfigField(Label = "Client Key Path", Description = "Optional path to the client certificate private key.",
        RequiresRestart = true, Group = "Security", Order = 42)]
    public string? SslKeyPath { get; set; }

    /// <summary>
    /// Path to a CA root certificate used to verify the server under the VerifyCA and
    /// VerifyFull modes. Falls back to the system trust store when null.
    /// </summary>
    [ConfigField(Label = "Root Certificate Path", Description = "CA bundle used by VerifyCA / VerifyFull.",
        RequiresRestart = true, Group = "Security", Order = 43)]
    public string? SslRootCertificatePath { get; set; }

    /// <summary>
    /// The <c>search_path</c> applied to every connection. Default: "public".
    /// <para>
    /// This does <b>not</b> change where an entity is mapped: a type without
    /// <c>[Table(Schema = …)]</c> always resolves to the literal <c>public</c> schema, and
    /// every generated statement is schema-qualified with that.
    /// </para>
    /// </summary>
    [ConfigField(Label = "Default Schema", RequiresRestart = true, Group = "Advanced", Order = 50, Collapsed = true)]
    public string DefaultSchema { get; set; } = "public";

    /// <summary>
    /// Application name reported to the server, visible in <c>pg_stat_activity</c>.
    /// Makes it possible to attribute load to this library. Optional.
    /// </summary>
    [ConfigField(Label = "Application Name", Group = "Advanced", Order = 51, Collapsed = true)]
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Primary schema sync knob. See <see cref="Models.SyncMode"/>.
    /// Default: <see cref="Models.SyncMode.Production"/> (safe/additive, never drops).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>Developer</b> — aggressive rolling updates; drops removed columns/indexes/FKs without asking (every boot).</item>
    ///   <item><b>Production</b> — default. Additive only; never drops. A change needing a drop is deferred and flagged <c>DriftPending</c>.</item>
    ///   <item><b>Migration</b> — deliberate one-shot destructive reconcile (with backup). Idempotent; warns to switch back to Production once done.</item>
    /// </list>
    /// In all modes, models whose stored CRC in <c>__schema_state</c> matches the current model are skipped entirely.
    /// </remarks>
    [ConfigField(Label = "Sync Mode",
        Description = "How schema sync reconciles the DB with entity models. Production = additive only, never drops (default). Developer = drops removed columns/indexes without asking. Migration = one-shot destructive reconcile, then switch back to Production.",
        Group = "Schema Sync", Order = 59)]
    public SyncMode SyncMode { get; set; } = SyncMode.Production;

    /// <summary>
    /// Legacy. Lower-level sync aggressiveness. Retained for back-compat; <see cref="SyncMode"/>
    /// takes precedence and maps onto this. See <see cref="Models.SchemaSyncLevel"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><b>None</b> — no sync at all.</item>
    ///   <item><b>Safe</b> — add missing columns/indexes/FKs + modify existing columns. Never drops.</item>
    ///   <item><b>Additive</b> — Safe + drop indexes and foreign keys no longer in the model.</item>
    ///   <item><b>Full</b> — Additive + drop columns no longer in the model. Development only.</item>
    /// </list>
    /// </remarks>
    [ConfigField(Label = "Schema Sync Level (legacy)",
        Description = "Deprecated — use Sync Mode. Lower-level knob: Safe = additive only. Additive = also drops removed indexes/FKs. Full = also drops removed columns.",
        Group = "Schema Sync", Order = 60, Collapsed = true)]
    public SchemaSyncLevel SchemaSyncLevel { get; set; } = SchemaSyncLevel.Safe;

    /// <summary>
    /// Legacy flag. When true, sync operates at <see cref="Models.SchemaSyncLevel.Full"/>
    /// regardless of <see cref="SchemaSyncLevel"/>. Prefer setting <see cref="SyncMode"/> directly.
    /// </summary>
    [ConfigField(Label = "Allow Destructive Sync (legacy)",
        Description = "Deprecated — use Sync Mode = Developer/Migration instead.",
        Group = "Schema Sync", Order = 61, Collapsed = true)]
    public bool AllowDestructiveSync { get; set; } = false;

    /// <summary>
    /// True when this database is configured for the one-shot <see cref="Models.SyncMode.Migration"/> mode.
    /// </summary>
    public bool IsMigrationMode => SyncMode == SyncMode.Migration;

    /// <summary>
    /// Effective internal sync level. An explicitly chosen <see cref="SyncMode.Developer"/> or
    /// <see cref="SyncMode.Migration"/> always maps to <see cref="SchemaSyncLevel.Full"/>. Otherwise
    /// (<see cref="SyncMode.Production"/>, the default) the legacy <see cref="SchemaSyncLevel"/> /
    /// <see cref="AllowDestructiveSync"/> knobs are honored for back-compat — so an old config with
    /// <c>SchemaSyncLevel = Full</c> still behaves destructively, while a fresh Production config
    /// (default <see cref="SchemaSyncLevel.Safe"/>) never drops.
    /// </summary>
    public SchemaSyncLevel EffectiveSyncLevel =>
        SyncMode switch
        {
            SyncMode.Developer => SchemaSyncLevel.Full,
            SyncMode.Migration => SchemaSyncLevel.Full,
            _ => AllowDestructiveSync ? SchemaSyncLevel.Full : SchemaSyncLevel
        };

    /// <summary>
    /// Intended override for the schema-backup directory. <b>Not currently applied:</b>
    /// <see cref="Services.BackupManager"/> always writes to <c>DataDirectory/backups</c>.
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
    /// Intended per-database override for the global cache switch. <b>Not currently
    /// applied:</b> nothing reads this value, so <see cref="CacheConfiguration.Enabled"/>
    /// governs every database.
    /// </summary>
    [ConfigField(Label = "Cache Enabled Override",
        Description = "Override the global cache switch for this database only. Leave empty to inherit.",
        Group = "Cache", Order = 70, Collapsed = true)]
    public bool? CacheEnabledOverride { get; set; } = null;

    /// <summary>
    /// Intended default per-query timeout in milliseconds. <b>Not currently applied:</b>
    /// nothing reads this value — <see cref="CommandTimeout"/> (in seconds) is the timeout
    /// that actually reaches the connection string.
    /// </summary>
    [ConfigField(Label = "Query Timeout (ms)", Min = 0,
        Description = "Intended per-query timeout in ms. Not currently applied; use Command Timeout.",
        Group = "Timeouts", Order = 32, Collapsed = true)]
    public int QueryTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Intended chunk size for the batched <c>InsertManyAsync</c> / <c>UpsertManyAsync</c>
    /// statements. <b>Not currently applied:</b> <c>GetRepository&lt;T&gt;()</c> does not pass
    /// it through, so <see cref="Services.Repository{T}"/> uses its own default of 500 rows
    /// (further capped by PostgreSQL's 65535-parameter ceiling).
    /// </summary>
    [ConfigField(Label = "Max Batch Insert Size", Min = 1, Max = 10_000,
        Description = "Rows per batched INSERT. Not currently applied; the repository default of 500 is used.",
        Group = "Performance", Order = 80, Collapsed = true)]
    public int MaxBatchInsertSize { get; set; } = 500;

    /// <summary>
    /// Intended ceiling on the number of values in a generated <c>IN (...)</c> list.
    /// <b>Advisory only:</b> the expression translator emits every value in a single
    /// <c>IN</c> list and does not consult this setting, so a large <c>Contains</c>
    /// collection is sent as-is.
    /// </summary>
    [ConfigField(Label = "Max IN-Clause Values", Min = 1, Max = 65_000,
        Description = "Advisory ceiling on generated IN lists; not currently enforced.",
        Group = "Performance", Order = 81, Collapsed = true)]
    public int MaxInClauseValues { get; set; } = 1_000;

    /// <summary>
    /// Intended per-connection prepared-statement cache size. <b>Not currently applied:</b>
    /// nothing reads this value, so Npgsql's own default is in force.
    /// </summary>
    [ConfigField(Label = "Prepared Statement Cache Size", Min = 0,
        Description = "Prepared statements kept per connection. Not currently applied.",
        Group = "Performance", Order = 82, Collapsed = true)]
    public int PreparedStatementCacheSize { get; set; } = 256;

    /// <summary>
    /// How many times to automatically retry a single non-transactional statement that
    /// fails with a transient error — SQLSTATE <c>40001</c> (serialization failure),
    /// <c>40P01</c> (deadlock detected) or <c>55P03</c> (lock not available). 0 disables.
    /// Default: 3. Statements inside an explicit transaction scope are never auto-retried —
    /// the whole transaction must be retried by the caller.
    /// </summary>
    [ConfigField(Label = "Transient Retry Count", Min = 0, Max = 10,
        Description = "Auto-retry deadlock / lock-wait-timeout on single statements. 0 disables.",
        Group = "Performance", Order = 83, Collapsed = true)]
    public int TransientRetryCount { get; set; } = 3;

    /// <summary>
    /// Base backoff in milliseconds for transient retries; the delay grows exponentially
    /// (base × 2^attempt) with a little random jitter. Default: 50.
    /// </summary>
    [ConfigField(Label = "Transient Retry Base Delay (ms)", Min = 0, Max = 10_000,
        Description = "Base backoff for transient retries; grows exponentially with jitter.",
        Group = "Performance", Order = 84, Collapsed = true)]
    public int TransientRetryBaseDelayMs { get; set; } = 50;

    /// <summary>
    /// Intended threshold for the N+1 detector: warn when one query template fires this
    /// many times inside a single request scope. <b>Not currently applied:</b> no
    /// request-scope counting is implemented and <c>N1QueryDetectedEvent</c> is never
    /// published, so this setting has no effect at any value.
    /// </summary>
    [ConfigField(Label = "N+1 Detector Threshold", Min = 0,
        Description = "Intended N+1 warning threshold. The detector is not implemented; no effect.",
        Group = "Observability", Order = 90, Collapsed = true)]
    public int N1DetectorThreshold { get; set; } = 0;

    /// <summary>
    /// Intended to capture <c>EXPLAIN (FORMAT JSON)</c> for a slow query and attach it to
    /// the <c>SlowQueryEvent</c>. <b>Not currently applied:</b> no call site runs EXPLAIN,
    /// so <c>SlowQueryEvent.ExplainJson</c> is always null whatever this is set to.
    /// </summary>
    [ConfigField(Label = "Capture EXPLAIN On Slow",
        Description = "Intended EXPLAIN capture on slow queries. Not currently implemented.",
        Group = "Observability", Order = 91, Collapsed = true)]
    public bool CaptureExplainOnSlowQuery { get; set; } = true;

    /// <summary>
    /// Intended default <c>varchar</c> length for a string property with no explicit
    /// <c>[Column(Size = …)]</c>. <b>Not currently applied:</b> type inference uses its own
    /// hard-coded default of 255, which this value merely happens to match.
    /// </summary>
    [ConfigField(Label = "Default String Size", Min = 1, Max = 65_535,
        Description = "Default VARCHAR length for string columns without an explicit Size.",
        Group = "Schema Sync", Order = 63, Collapsed = true)]
    public int DefaultStringSize { get; set; } = 255;

    /// <summary>
    /// Builds and returns the Npgsql connection string from the current configuration.
    /// </summary>
    public string BuildConnectionString()
    {
        // Built through NpgsqlConnectionStringBuilder rather than by concatenation, so a
        // value containing ';' or '=' is quoted instead of injecting connection options.
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Database,
            Username = Username,
            Password = Password,
            Pooling = EnablePooling,
            MinPoolSize = MinPoolSize,
            MaxPoolSize = MaxPoolSize,
            ConnectionIdleLifetime = ConnectionLifetime,
            Timeout = ConnectionTimeout,
            CommandTimeout = CommandTimeout,
            SslMode = SslMode switch
            {
                PostgreSqlSslMode.Disable    => Npgsql.SslMode.Disable,
                PostgreSqlSslMode.Allow      => Npgsql.SslMode.Allow,
                PostgreSqlSslMode.Require    => Npgsql.SslMode.Require,
                PostgreSqlSslMode.VerifyCA   => Npgsql.SslMode.VerifyCA,
                PostgreSqlSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
                _                            => Npgsql.SslMode.Prefer
            }
        };

        if (!string.IsNullOrWhiteSpace(DefaultSchema)) builder.SearchPath = DefaultSchema;
        if (!string.IsNullOrWhiteSpace(ApplicationName)) builder.ApplicationName = ApplicationName;
        if (!string.IsNullOrWhiteSpace(SslCertificatePath)) builder.SslCertificate = SslCertificatePath;
        if (!string.IsNullOrWhiteSpace(SslKeyPath)) builder.SslKey = SslKeyPath;
        if (!string.IsNullOrWhiteSpace(SslRootCertificatePath)) builder.RootCertificate = SslRootCertificatePath;

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

        if (MinPoolSize < 0)
            errors.Add("MinPoolSize cannot be negative");

        if (MaxPoolSize < 1 || MaxPoolSize < MinPoolSize)
            errors.Add("MaxPoolSize must be positive and at least MinPoolSize");

        if (ConnectionTimeout < 0 || CommandTimeout < 0)
            errors.Add("Connection and command timeouts cannot be negative");

        if (MaxBatchInsertSize < 1)
            errors.Add("MaxBatchInsertSize must be positive");

        if (string.IsNullOrWhiteSpace(DefaultSchema))
            errors.Add("DefaultSchema is required");

        if (SslMode is PostgreSqlSslMode.VerifyCA or PostgreSqlSslMode.VerifyFull
            && !string.IsNullOrWhiteSpace(SslRootCertificatePath)
            && !File.Exists(SslRootCertificatePath))
            errors.Add($"Root certificate not found at '{SslRootCertificatePath}'");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}

/// <summary>
/// TLS negotiation mode, mirroring libpq's <c>sslmode</c>. Declared here rather than
/// reusing Npgsql's enum so the configuration surface does not leak a provider type.
/// </summary>
public enum PostgreSqlSslMode
{
    /// <summary>Never use TLS.</summary>
    Disable,

    /// <summary>Use TLS only if the server insists.</summary>
    Allow,

    /// <summary>Use TLS when available, without verifying the certificate. The default.</summary>
    Prefer,

    /// <summary>Require TLS, but do not verify the certificate. Encrypts without authenticating.</summary>
    Require,

    /// <summary>Require TLS and verify the certificate chain against a trusted CA.</summary>
    VerifyCA,

    /// <summary>Require TLS, verify the chain, and check the host name matches. Use this in production.</summary>
    VerifyFull
}
