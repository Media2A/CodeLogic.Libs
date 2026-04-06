using CodeLogic.Core.Localization;

namespace CL.MySQL2.Localization;

/// <summary>
/// Localized strings for the CL.MySQL2 library.
/// Serialized to / from <c>mysql.{culture}.json</c> in the library's localization directory.
/// </summary>
[LocalizationSection("mysql")]
public class MySQL2Strings : LocalizationModelBase
{
    [LocalizedString(Description = "Logged when a database connection is successfully established.")]
    public string ConnectionEstablished { get; set; } = "Database connection established";

    [LocalizedString(Description = "Logged when a connection attempt fails.")]
    public string ConnectionFailed { get; set; } = "Failed to connect to database";

    [LocalizedString(Description = "Logged when a database connection is closed.")]
    public string ConnectionClosed { get; set; } = "Database connection closed";

    [LocalizedString(Description = "Logged when a test connection succeeds.")]
    public string ConnectionTestSuccess { get; set; } = "Connection test successful";

    [LocalizedString(Description = "Logged when a test connection fails.")]
    public string ConnectionTestFailed { get; set; } = "Connection test failed";

    [LocalizedString(Description = "Logged after the library completes initialization.")]
    public string LibraryInitialized { get; set; } = "MySQL2 library initialized";

    [LocalizedString(Description = "Logged when the library starts.")]
    public string LibraryStarted { get; set; } = "MySQL2 library started";

    [LocalizedString(Description = "Logged when the library stops.")]
    public string LibraryStopped { get; set; } = "MySQL2 library stopped";

    [LocalizedString(Description = "Logged when table synchronization begins. {0} = table name.")]
    public string TableSyncStarted { get; set; } = "Table synchronization started for {0}";

    [LocalizedString(Description = "Logged when table synchronization completes. {0} = table name.")]
    public string TableSyncCompleted { get; set; } = "Table synchronization completed for {0}";

    [LocalizedString(Description = "Logged when a new table is created. {0} = table name.")]
    public string TableCreated { get; set; } = "Table {0} created successfully";

    [LocalizedString(Description = "Logged when a table schema is updated. {0} = table name.")]
    public string TableUpdated { get; set; } = "Table {0} updated successfully";

    [LocalizedString(Description = "Logged when table synchronization fails. {0} = table name.")]
    public string TableSyncFailed { get; set; } = "Table synchronization failed for {0}";

    [LocalizedString(Description = "Logged when a slow query is detected. {0} = elapsed ms, {1} = query.")]
    public string SlowQueryDetected { get; set; } = "Slow query detected ({0}ms): {1}";

    [LocalizedString(Description = "Logged after a record is inserted. {0} = table name.")]
    public string RecordInserted { get; set; } = "Record inserted into {0}";

    [LocalizedString(Description = "Logged after bulk insert. {0} = count, {1} = table name.")]
    public string RecordsBulkInserted { get; set; } = "{0} records inserted into {1}";

    [LocalizedString(Description = "Logged after a record is updated. {0} = table name.")]
    public string RecordUpdated { get; set; } = "Record updated in {0}";

    [LocalizedString(Description = "Logged after a record is deleted. {0} = table name.")]
    public string RecordDeleted { get; set; } = "Record deleted from {0}";

    [LocalizedString(Description = "Logged when a requested record does not exist. {0} = table name.")]
    public string RecordNotFound { get; set; } = "Record not found in {0}";

    [LocalizedString(Description = "Logged when a transaction starts.")]
    public string TransactionStarted { get; set; } = "Transaction started";

    [LocalizedString(Description = "Logged when a transaction commits successfully.")]
    public string TransactionCommitted { get; set; } = "Transaction committed successfully";

    [LocalizedString(Description = "Logged when a transaction is rolled back.")]
    public string TransactionRolledBack { get; set; } = "Transaction rolled back";

    [LocalizedString(Description = "Logged when a health check passes.")]
    public string HealthCheckPassed { get; set; } = "Health check passed";

    [LocalizedString(Description = "Logged when a health check fails. {0} = reason.")]
    public string HealthCheckFailed { get; set; } = "Health check failed: {0}";

    [LocalizedString(Description = "Logged on configuration error. {0} = error detail.")]
    public string ConfigurationError { get; set; } = "Configuration error: {0}";

    [LocalizedString(Description = "Logged on database error. {0} = error detail.")]
    public string DatabaseError { get; set; } = "Database error: {0}";
}
