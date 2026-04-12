namespace CL.GameNetQuery.Models;

/// <summary>
/// Result of a server query operation.
/// </summary>
public sealed class QueryResult
{
    /// <summary>Whether the query succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Server info (if query succeeded).</summary>
    public ServerInfo? Info { get; init; }

    /// <summary>Player list (if available).</summary>
    public IReadOnlyList<PlayerInfo> Players { get; init; } = [];

    /// <summary>Error message (if query failed).</summary>
    public string? Error { get; init; }

    /// <summary>How long the query took in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Creates a successful query result.</summary>
    /// <param name="info">The server information retrieved.</param>
    /// <param name="players">Optional list of players on the server.</param>
    /// <param name="durationMs">Duration of the query in milliseconds.</param>
    /// <returns>A successful <see cref="QueryResult"/>.</returns>
    public static QueryResult Ok(ServerInfo info, IReadOnlyList<PlayerInfo>? players = null, long durationMs = 0) => new()
    {
        Success = true,
        Info = info,
        Players = players ?? [],
        DurationMs = durationMs
    };

    /// <summary>Creates a failed query result.</summary>
    /// <param name="error">The error message describing the failure.</param>
    /// <param name="durationMs">Duration of the query in milliseconds.</param>
    /// <returns>A failed <see cref="QueryResult"/>.</returns>
    public static QueryResult Fail(string error, long durationMs = 0) => new()
    {
        Success = false,
        Error = error,
        DurationMs = durationMs
    };
}
