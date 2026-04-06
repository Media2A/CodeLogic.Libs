using CodeLogic.Core.Events;

namespace CL.SQLite.Events;

public record TableSyncedEvent(string TableName, bool Created, string Message, DateTime SyncedAt) : IEvent;
public record SlowQueryEvent(string TableName, string Query, long ElapsedMs, DateTime DetectedAt) : IEvent;
