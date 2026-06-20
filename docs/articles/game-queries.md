# Game Server Queries

`CL.GameNetQuery` provides game server querying for Valve Source Engine games (CS2, CSS) and Minecraft servers.

## Features

| Feature | Description |
|---------|-------------|
| **Valve RCON** | Remote console protocol for Source Engine servers |
| **Valve UDP Query** | A2S_INFO and A2S_PLAYER queries via UDP |
| **Status Parsing** | Parse `status` command output into structured data |
| **Counter-Strike 2** | High-level CS2 wrapper with map change, kick, ban, cvar commands |
| **Counter-Strike: Source** | High-level CSS wrapper with similar commands |
| **Minecraft RCON** | Minecraft server remote console protocol |
| **Minecraft UDP Query** | Minecraft server status via UDP |

## Valve RCON

Connect to any Source Engine server via RCON. The client accepts the IP either
as a `string` or an `IPAddress`, and `ConnectAsync` returns `false` instead of
throwing when the connection or authentication fails:

```csharp
using System.Net;
using CL.GameNetQuery.Valve;

using var rcon = new ValveRconClient("192.168.1.100", 27015, "rcon_password");
// or: new ValveRconClient(IPAddress.Parse("192.168.1.100"), 27015, "rcon_password");

if (await rcon.ConnectAsync(timeoutMs: 5000))
{
    string response = await rcon.SendCommandAsync("status");
    Console.WriteLine(response);
}

rcon.Disconnect();
```

> The RCON port is a `ushort`. `SendCommandAsync` throws
> `InvalidOperationException` if called before a successful `ConnectAsync`, and
> returns an empty string on transport errors.

## Counter-Strike 2

High-level wrapper for CS2 servers:

```csharp
using CL.GameNetQuery.Valve.Games;

using var cs2 = new CounterStrike2("192.168.1.100", 27015, "rcon_password");
await cs2.ConnectAsync();

// Get server status (CS2-specific status parsing)
var (info, players) = await cs2.GetStatusAsync();
Console.WriteLine($"Server: {info.Hostname}, Map: {info.Map}, Players: {info.PlayerCount} (+{info.BotCount} bots)");
foreach (var p in players)
    Console.WriteLine($"  {p.Name} [{p.UniqueId}] ping={p.Ping} bot={p.IsBot}");

// Or get the unparsed status output
string raw = await cs2.GetStatusRawAsync();

// Server management
await cs2.ChangeMapAsync("de_dust2");
await cs2.KickPlayerAsync("PlayerName");
await cs2.BanPlayerAsync("PlayerName");
await cs2.UnbanPlayerAsync("PlayerName");
await cs2.SlayPlayerAsync("PlayerName");
await cs2.SayAsync("Hello from RCON!");
await cs2.SetHostnameAsync("My CS2 Server");
await cs2.ExecConfigAsync("gamemode_competitive.cfg");
await cs2.SetCvarAsync("mp_roundtime", "1.92");
string val = await cs2.GetCvarAsync("mp_roundtime");
await cs2.EnableCheatsAsync();
await cs2.DisableCheatsAsync();
await cs2.SetFriendlyFireAsync(false);
await cs2.SetTeamBalanceAsync(true);
await cs2.RestartAsync();

cs2.Disconnect();
```

`CounterStrike2` exposes the full command set; `CounterStrikeSource` exposes the
common subset (`ChangeMapAsync`, `KickPlayerAsync`, `BanPlayerAsync`,
`UnbanPlayerAsync`, `SetHostnameAsync`, `ExecConfigAsync`, `SetCvarAsync`,
`GetCvarAsync`, `RestartAsync`, `SayAsync`). The cheat/friendly-fire/team-balance/slay
helpers are CS2-only.

## Counter-Strike: Source

Same API as CS2:

```csharp
using CL.GameNetQuery.Valve.Games;

using var css = new CounterStrikeSource("192.168.1.100", 27015, "rcon_password");
await css.ConnectAsync();

var (info, players) = await css.GetStatusAsync();
```

## Valve UDP Query (No RCON Required)

Query any Source Engine server without RCON credentials:

`ValveUdpQuery` is a static helper. The port is a `ushort` and both methods take
an optional `timeoutMs` (default 3000). `GetServerInfoAsync` returns `null` on
failure; `GetPlayerListAsync` returns an empty list on failure.

```csharp
using CL.GameNetQuery.Valve;

var info = await ValveUdpQuery.GetServerInfoAsync("192.168.1.100", 27015, timeoutMs: 3000);
if (info is not null)
{
    Console.WriteLine($"{info.Hostname} - {info.Map} ({info.PlayerCount}/{info.MaxPlayers})");
}

var players = await ValveUdpQuery.GetPlayerListAsync("192.168.1.100", 27015);
foreach (var player in players)
{
    Console.WriteLine($"  {player.Name} - Score: {player.Score} - Duration: {player.Duration}s");
}
```

## Status Parsing

Parse raw `status` command output into structured data:

```csharp
using CL.GameNetQuery.Valve;

string rawStatus = await rcon.SendCommandAsync("status");

// For CSS / Source 1
ServerInfo info = ValveStatusParser.ParseStatus(rawStatus);
var (serverInfo, playerList) = ValveStatusParser.ParseStatusWithPlayers(rawStatus);

// Individual field extractors (CSS)
string hostname = ValveStatusParser.GetHostname(rawStatus);
string map = ValveStatusParser.GetMapName(rawStatus);
var (humans, bots) = ValveStatusParser.GetPlayerCount(rawStatus);
var (ip, port) = ValveStatusParser.GetServerAddress(rawStatus);
string version = ValveStatusParser.GetVersion(rawStatus);
List<string> tags = ValveStatusParser.GetTags(rawStatus);
List<PlayerInfo> players = ValveStatusParser.GetPlayerList(rawStatus);

// For CS2 (different player/map format)
string cs2Hostname = ValveStatusParserCS2.GetHostname(rawStatus);
string cs2Map = ValveStatusParserCS2.GetMapName(rawStatus);
var (cs2Humans, cs2Bots) = ValveStatusParserCS2.GetPlayerCount(rawStatus);
List<PlayerInfo> cs2Players = ValveStatusParserCS2.GetPlayerList(rawStatus);
List<string> spawngroups = ValveStatusParserCS2.GetSpawngroups(rawStatus);
```

## Minecraft

### UDP Query

`MinecraftQueryClient` implements the GameSpy4 query protocol. `QueryServer` is
**synchronous** and returns the parsed response as newline-separated `key: value`
lines (an empty string on failure). Use `GetStatusValue` to read individual
fields by key (case-insensitive); Minecraft color codes are stripped automatically.

```csharp
using CL.GameNetQuery.Minecraft;

string response = MinecraftQueryClient.QueryServer("mc.example.com", 25565);
if (!string.IsNullOrEmpty(response))
{
    string motd       = MinecraftQueryClient.GetStatusValue(response, "hostname");
    string map        = MinecraftQueryClient.GetStatusValue(response, "map");
    string numPlayers = MinecraftQueryClient.GetStatusValue(response, "numplayers");
    string maxPlayers = MinecraftQueryClient.GetStatusValue(response, "maxplayers");
    Console.WriteLine($"{motd} - {numPlayers}/{maxPlayers} on {map}");
}
```

> The query port (default 25565) must have `enable-query=true` set in
> `server.properties`; it is separate from the RCON port.

### RCON

`MinecraftRconClient` uses the same Source RCON wire protocol with
Minecraft-specific auth handling. The port is a `ushort`.

```csharp
using CL.GameNetQuery.Minecraft;

using var rcon = new MinecraftRconClient("mc.example.com", 25575, "rcon_password");
if (await rcon.ConnectAsync())
{
    string response = await rcon.SendCommandAsync("list");
    Console.WriteLine(response);
}

rcon.Disconnect();
```

## Models

### ServerInfo

```csharp
public sealed class ServerInfo
{
    public string Hostname { get; init; }            // server name
    public string Map { get; init; }
    public int PlayerCount { get; init; }            // human players
    public int MaxPlayers { get; init; }
    public int BotCount { get; init; }
    public string Version { get; init; }
    public string Ip { get; init; }
    public int Port { get; init; }
    public bool IsOnline { get; init; }
    public IReadOnlyList<string> Tags { get; init; }
    public string RawResponse { get; init; }         // raw output, for debugging
    public DateTime QueriedUtc { get; init; }
}
```

### PlayerInfo

```csharp
public sealed class PlayerInfo
{
    public string Name { get; init; }
    public string UniqueId { get; init; }            // SteamID (Valve) / UUID (Minecraft)
    public int Score { get; init; }
    public float Duration { get; init; }             // seconds connected
    public int Ping { get; init; }
    public int Loss { get; init; }
    public string State { get; init; }
    public bool IsBot { get; init; }
}
```

### QueryResult

```csharp
public sealed class QueryResult
{
    public bool Success { get; init; }
    public ServerInfo? Info { get; init; }
    public IReadOnlyList<PlayerInfo> Players { get; init; }  // empty (never null) when unavailable
    public string? Error { get; init; }
    public long DurationMs { get; init; }

    public static QueryResult Ok(ServerInfo info, IReadOnlyList<PlayerInfo>? players = null, long durationMs = 0);
    public static QueryResult Fail(string error, long durationMs = 0);
}
```

## CodeLogic Integration

Register as a CodeLogic library:

```csharp
await Libraries.LoadAsync<GameNetQueryLibrary>();
```

The library is lightweight and has no external dependencies beyond CodeLogic core.
