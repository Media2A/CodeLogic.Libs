using CodeLogic.Core.Events;

namespace CL.PostgreSQL.Events;

public record DatabaseConnectedEvent(
    string ConnectionId,
    string Host,
    int Port,
    string Database,
    string ServerVersion,
    DateTime ConnectedAt) : IEvent;

public record DatabaseDisconnectedEvent(
    string ConnectionId,
    DateTime DisconnectedAt) : IEvent;

public record TableSyncedEvent(
    string ConnectionId,
    string SchemaName,
    string TableName,
    bool Created,
    List<string> Operations,
    TimeSpan Duration) : IEvent;

public record SlowQueryEvent(
    string ConnectionId,
    string Query,
    long ElapsedMs,
    DateTime DetectedAt) : IEvent;

public record HealthChangedEvent(
    string ConnectionId,
    bool IsHealthy,
    string Message,
    DateTime ChangedAt) : IEvent;
