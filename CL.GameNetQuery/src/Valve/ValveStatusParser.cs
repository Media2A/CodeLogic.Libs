using System.Text.RegularExpressions;
using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// Parses Valve Source Engine RCON "status" command output.
/// Works for CSS (Source 1) servers.
/// </summary>
public static class ValveStatusParser
{
    public static string GetHostname(string status)
    {
        var match = Regex.Match(status, @"hostname:\s*(.+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    public static string GetMapName(string status)
    {
        var match = Regex.Match(status, @"map\s*:\s*(\S+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    public static (int HumanCount, int BotCount) GetPlayerCount(string status)
    {
        var match = Regex.Match(status, @"players\s*:\s*(\d+)\s*humans,\s*(\d+)\s*bots");
        return match.Success
            ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
            : (0, 0);
    }

    public static (string Ip, int Port) GetServerAddress(string status)
    {
        var match = Regex.Match(status, @"udp/ip\s*:\s*(\S+):(\d+)");
        return match.Success
            ? (match.Groups[1].Value, int.Parse(match.Groups[2].Value))
            : (string.Empty, 0);
    }

    public static string GetVersion(string status)
    {
        var match = Regex.Match(status, @"version\s*:\s*(\S+)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    public static List<string> GetTags(string status)
    {
        var match = Regex.Match(status, @"tags\s*:\s*(.+)");
        return match.Success
            ? match.Groups[1].Value.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
    }

    public static List<PlayerInfo> GetPlayerList(string status)
    {
        var players = new List<PlayerInfo>();
        var regex = new Regex(
            @"#\s+(?<userid>\d+)\s+""(?<name>.+?)""\s+(?<uniqueid>\S+)\s*(?<connected>\S*)\s*(?<ping>\d*)\s*(?<loss>\d*)\s*(?<state>\S*)\s*(?<adr>\S*)?",
            RegexOptions.Compiled);

        foreach (var line in status.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith('#') || line.Contains("userid name")) continue;

            var match = regex.Match(line);
            if (!match.Success) continue;

            var uniqueId = match.Groups["uniqueid"].Value;
            players.Add(new PlayerInfo
            {
                Name = match.Groups["name"].Value,
                UniqueId = uniqueId,
                Ping = int.TryParse(match.Groups["ping"].Value, out var p) ? p : 0,
                Loss = int.TryParse(match.Groups["loss"].Value, out var l) ? l : 0,
                State = match.Groups["state"].Value,
                IsBot = uniqueId.Equals("BOT", StringComparison.OrdinalIgnoreCase)
            });
        }

        return players;
    }

    public static ServerInfo ParseStatus(string status) =>
        ParseStatusWithPlayers(status).Info;

    public static (ServerInfo Info, List<PlayerInfo> Players) ParseStatusWithPlayers(string status)
    {
        var (humanCount, botCount) = GetPlayerCount(status);
        var (ip, port) = GetServerAddress(status);

        var info = new ServerInfo
        {
            Hostname = GetHostname(status),
            Map = GetMapName(status),
            PlayerCount = humanCount,
            BotCount = botCount,
            Version = GetVersion(status),
            Ip = ip,
            Port = port,
            Tags = GetTags(status),
            IsOnline = !string.IsNullOrEmpty(GetHostname(status)),
            RawResponse = status,
            QueriedUtc = DateTime.UtcNow
        };

        return (info, GetPlayerList(status));
    }
}
