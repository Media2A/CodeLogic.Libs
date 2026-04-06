using CodeLogic.Core.Localization;

namespace CL.SQLite.Localization;

[LocalizationSection("sqlite")]
public class SQLiteStrings : LocalizationModelBase
{
    [LocalizedString] public string LibraryInitialized { get; set; } = "SQLite library initialized: {0}";
    [LocalizedString] public string LibraryStarted { get; set; } = "SQLite library started";
    [LocalizedString] public string LibraryStopped { get; set; } = "SQLite library stopped";
    [LocalizedString] public string ConnectionCreated { get; set; } = "Created new SQLite connection: {0}";
    [LocalizedString] public string ConnectionReused { get; set; } = "Reused pooled connection";
    [LocalizedString] public string ConnectionReleased { get; set; } = "Connection returned to pool";
    [LocalizedString] public string TableSyncStarted { get; set; } = "Table sync started: {0}";
    [LocalizedString] public string TableCreated { get; set; } = "Created table: {0}";
    [LocalizedString] public string TableSynced { get; set; } = "Synced table: {0}";
    [LocalizedString] public string TableSyncFailed { get; set; } = "Table sync failed: {0}: {1}";
    [LocalizedString] public string SlowQueryDetected { get; set; } = "Slow query ({0}ms): {1}";
    [LocalizedString] public string HealthCheckPassed { get; set; } = "SQLite is operational: {0}";
    [LocalizedString] public string HealthCheckFailed { get; set; } = "SQLite health check failed: {0}";
}
