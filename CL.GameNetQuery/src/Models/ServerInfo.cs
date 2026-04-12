namespace CL.GameNetQuery.Models;

/// <summary>
/// Represents the current status/info of a game server.
/// </summary>
public sealed class ServerInfo
{
    /// <summary>Server hostname/name.</summary>
    public string Hostname { get; init; } = string.Empty;

    /// <summary>Current map name.</summary>
    public string Map { get; init; } = string.Empty;

    /// <summary>Number of human players.</summary>
    public int PlayerCount { get; init; }

    /// <summary>Maximum player slots.</summary>
    public int MaxPlayers { get; init; }

    /// <summary>Number of bots.</summary>
    public int BotCount { get; init; }

    /// <summary>Game/server version string.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Server IP address.</summary>
    public string Ip { get; init; } = string.Empty;

    /// <summary>Server port.</summary>
    public int Port { get; init; }

    /// <summary>Whether the server is online and responding.</summary>
    public bool IsOnline { get; init; }

    /// <summary>Server tags (if available).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Raw status response (for debugging).</summary>
    public string RawResponse { get; init; } = string.Empty;

    /// <summary>When this info was queried.</summary>
    public DateTime QueriedUtc { get; init; } = DateTime.UtcNow;
}
