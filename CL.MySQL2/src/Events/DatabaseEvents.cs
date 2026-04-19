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
    DateTime DetectedAt,
    string? ExplainJson = null) : IEvent;

/// <summary>
/// Published after every query, fast or slow. Carries SQL text, elapsed ms, row count,
/// cache hit flag, and the connection it ran on. Subscribe for
/// dashboards / metrics / traces.
/// </summary>
public record QueryExecutedEvent(
    string ConnectionId,
    string Query,
    long ElapsedMs,
    int RowCount,
    bool CacheHit,
    DateTime CompletedAt) : IEvent;

/// <summary>Published when a cached query result is returned.</summary>
public record CacheHitEvent(
    string ConnectionId,
    string TableName,
    string CacheKey,
    DateTime At) : IEvent;

/// <summary>Published on cache miss (query had to hit the DB).</summary>
public record CacheMissEvent(
    string ConnectionId,
    string TableName,
    string CacheKey,
    DateTime At) : IEvent;

/// <summary>
/// Published when the N+1 detector observes the same query template firing many times
/// within a single request scope. Fires once per scope per offending template.
/// </summary>
public record N1QueryDetectedEvent(
    string ConnectionId,
    string QueryTemplate,
    int Count,
    DateTime At) : IEvent;

/// <summary>Published when the library's health state changes.</summary>
public record HealthChangedEvent(
    string ConnectionId,
    bool IsHealthy,
    string Message,
    DateTime ChangedAt) : IEvent;
