# CodeLogic.GameNetQuery

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GameNetQuery)](https://www.nuget.org/packages/CodeLogic.GameNetQuery)

Game server query library for [CodeLogic](https://github.com/Media2A/CodeLogic) — Valve Source Engine (CSS, CS2) and Minecraft server queries via UDP and RCON.

## Install

```
dotnet add package CodeLogic.GameNetQuery
```

## Quick Start

```csharp
// Valve Source Engine -- UDP query (no password needed)
var serverInfo = await ValveUdpQuery.GetServerInfoAsync("192.168.1.10", 27015);
Console.WriteLine($"{serverInfo?.Hostname} - {serverInfo?.PlayerCount}/{serverInfo?.MaxPlayers}");

var players = await ValveUdpQuery.GetPlayerListAsync("192.168.1.10", 27015);

// Valve RCON -- authenticated commands
using var rcon = new ValveRconClient("192.168.1.10", 27015, "rconpassword");
if (await rcon.ConnectAsync())
{
    var response = await rcon.SendCommandAsync("status");
    Console.WriteLine(response);
}

// High-level CS2 admin wrapper over RCON
using var cs2 = new CounterStrike2("192.168.1.10", 27015, "rconpassword");
if (await cs2.ConnectAsync())
{
    var (info, players) = await cs2.GetStatusAsync();
    await cs2.ChangeMapAsync("de_dust2");
    await cs2.SayAsync("gg");
}

// Minecraft -- UDP query (synchronous; returns key:value lines)
var mcResponse = MinecraftQueryClient.QueryServer("192.168.1.20", 25565);
var motd = MinecraftQueryClient.GetStatusValue(mcResponse, "hostname");

// Minecraft -- RCON
using var mcRcon = new MinecraftRconClient("192.168.1.20", 25575, "rconpassword");
if (await mcRcon.ConnectAsync())
    Console.WriteLine(await mcRcon.SendCommandAsync("list"));
```

## Features

- **Valve A2S_INFO** -- query Source Engine server name, map, player count, and metadata via UDP
- **Valve A2S_PLAYER** -- retrieve the full player list with scores and play durations
- **Valve RCON** -- authenticate and execute remote console commands over TCP (string or `IPAddress` host)
- **CS2 / CSS admin wrappers** -- `CounterStrike2` and `CounterStrikeSource` expose typed helpers
  (map change, kick, ban/unban, cvars, exec config, say) plus parsed `GetStatusAsync`; CS2 adds
  cheats, friendly-fire, team-balance, and slay
- **`status` parsers** -- `ValveStatusParser` (Source 1) and `ValveStatusParserCS2` turn raw `status`
  output into `ServerInfo` + `PlayerInfo`, including tags, version, address, and CS2 spawngroups
- **Minecraft query** -- GameSpy4 UDP protocol for server status and player info
- **Minecraft RCON** -- remote console over the Source RCON protocol
- **Zero configuration** -- all query classes are static or standalone; no config file needed

## Configuration

CL.GameNetQuery does not require a configuration file. All query clients accept connection parameters directly.

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
