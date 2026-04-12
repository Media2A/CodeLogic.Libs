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

Connect to any Source Engine server via RCON:

```csharp
using CL.GameNetQuery.Valve;

using var rcon = new ValveRconClient("192.168.1.100", 27015, "rcon_password");
await rcon.ConnectAsync();

string response = await rcon.SendCommandAsync("status");
Console.WriteLine(response);

rcon.Disconnect();
```

## Counter-Strike 2

High-level wrapper for CS2 servers:

```csharp
using CL.GameNetQuery.Valve.Games;

using var cs2 = new CounterStrike2("192.168.1.100", 27015, "rcon_password");
await cs2.ConnectAsync();

// Get server status
var (info, players) = await cs2.GetStatusAsync();
Console.WriteLine($"Server: {info.Name}, Map: {info.Map}, Players: {info.PlayerCount}/{info.MaxPlayers}");

// Server management
await cs2.ChangeMapAsync("de_dust2");
await cs2.KickPlayerAsync("PlayerName");
await cs2.BanPlayerAsync("PlayerName");
await cs2.SayAsync("Hello from RCON!");
await cs2.SetCvarAsync("sv_cheats", "0");
await cs2.RestartAsync();

cs2.Disconnect();
```

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

```csharp
using CL.GameNetQuery.Valve;

var info = await ValveUdpQuery.GetServerInfoAsync("192.168.1.100", 27015);
if (info is not null)
{
    Console.WriteLine($"{info.Name} - {info.Map} ({info.PlayerCount}/{info.MaxPlayers})");
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

// For CSS
var info = ValveStatusParser.ParseStatus(rawStatus);
var (serverInfo, playerList) = ValveStatusParser.ParseStatusWithPlayers(rawStatus);

// For CS2 (different format)
string hostname = ValveStatusParserCS2.GetHostname(rawStatus);
string mapName = ValveStatusParserCS2.GetMapName(rawStatus);
var (current, max) = ValveStatusParserCS2.GetPlayerCount(rawStatus);
```

## Minecraft

### UDP Query

```csharp
using CL.GameNetQuery.Minecraft;

var result = await MinecraftQueryClient.QueryServer("mc.example.com", 25565);
if (result.Success)
{
    Console.WriteLine($"{result.Info.Name} - {result.Info.PlayerCount}/{result.Info.MaxPlayers}");
    Console.WriteLine($"Map: {result.Info.Map}");
}
```

### RCON

```csharp
using CL.GameNetQuery.Minecraft;

using var rcon = new MinecraftRconClient("mc.example.com", 25575, "rcon_password");
await rcon.ConnectAsync();

string response = await rcon.SendCommandAsync("list");
Console.WriteLine(response);

rcon.Disconnect();
```

## Query Result Model

All queries return a `QueryResult`:

```csharp
public class QueryResult
{
    public bool Success { get; }
    public ServerInfo? Info { get; }
    public IReadOnlyList<PlayerInfo>? Players { get; }
    public string? Error { get; }
    public long LatencyMs { get; }
}
```

## CodeLogic Integration

Register as a CodeLogic library:

```csharp
await Libraries.LoadAsync<GameNetQueryLibrary>();
```

The library is lightweight and has no external dependencies beyond CodeLogic core.
