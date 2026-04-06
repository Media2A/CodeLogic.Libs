using CodeLogic.Core.Localization;

namespace CL.PostgreSQL.Localization;

[LocalizationSection("postgresql")]
public class PostgreSQLStrings : LocalizationModelBase
{
    [LocalizedString(Description = "Logged after the library completes initialization. {0} = database count.")]
    public string LibraryInitialized { get; set; } = "PostgreSQL library initialized with {0} database(s)";

    [LocalizedString(Description = "Logged when the library starts.")]
    public string LibraryStarted { get; set; } = "PostgreSQL library started";

    [LocalizedString(Description = "Logged when the library stops.")]
    public string LibraryStopped { get; set; } = "PostgreSQL library stopped";

    [LocalizedString(Description = "Logged when a database is registered. {0} = connection ID, {1} = host.")]
    public string ConnectionRegistered { get; set; } = "Registered database: {0} -> {1}";

    [LocalizedString(Description = "Logged when a connection test succeeds. {0} = connection ID, {1} = server version.")]
    public string ConnectionTestSuccess { get; set; } = "Connection '{0}' test successful (v{1})";

    [LocalizedString(Description = "Logged when a connection test fails. {0} = connection ID, {1} = error.")]
    public string ConnectionTestFailed { get; set; } = "Connection '{0}' test failed: {1}";

    [LocalizedString(Description = "Logged when table sync begins. {0} = schema, {1} = table.")]
    public string TableSyncStarted { get; set; } = "Table sync started: {0}.{1}";

    [LocalizedString(Description = "Logged when a table is created. {0} = schema, {1} = table.")]
    public string TableCreated { get; set; } = "Table created: {0}.{1}";

    [LocalizedString(Description = "Logged when a table is synced. {0} = schema, {1} = table.")]
    public string TableSynced { get; set; } = "Table synced: {0}.{1}";

    [LocalizedString(Description = "Logged when table sync fails. {0} = schema, {1} = table.")]
    public string TableSyncFailed { get; set; } = "Table sync failed: {0}.{1}";

    [LocalizedString(Description = "Logged when a slow query is detected. {0} = elapsed ms, {1} = query.")]
    public string SlowQueryDetected { get; set; } = "Slow query detected ({0}ms): {1}";

    [LocalizedString(Description = "Logged when all connections are healthy. {0} = connection count.")]
    public string HealthCheckPassed { get; set; } = "All {0} database connection(s) operational";

    [LocalizedString(Description = "Logged when health check fails. {0} = failed connection IDs.")]
    public string HealthCheckFailed { get; set; } = "Failed connections: {0}";
}
