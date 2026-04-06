using CodeLogic.Core.Events;

namespace CL.MySQL2.Events;

/// <summary>Published when a MySQL connection is successfully opened.</summary>
public record DatabaseConnectedEvent(
    string ConnectionId,
    string Host,
    int Port,
    string Database,
    DateTime ConnectedAt) : IEvent;

/// <summary>Published when a MySQL connection is closed.</summary>
public record DatabaseDisconnectedEvent(
    string ConnectionId,
    DateTime DisconnectedAt) : IEvent;

/// <summary>Published after a table is synchronized (created or altered).</summary>
public record TableSyncedEvent(
    string ConnectionId,
    string TableName,
    bool Created,
    List<string> Operations,
    TimeSpan Duration) : IEvent;

/// <summary>Published when a query exceeds the configured slow-query threshold.</summary>
public record SlowQueryEvent(
    string ConnectionId,
    string Query,
    long ElapsedMs,
    DateTime DetectedAt) : IEvent;

/// <summary>Published when the library's health state changes.</summary>
public record HealthChangedEvent(
    string ConnectionId,
    bool IsHealthy,
    string Message,
    DateTime ChangedAt) : IEvent;
