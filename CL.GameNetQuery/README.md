# CL.GameNetQuery

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GameNetQuery)](https://www.nuget.org/packages/CodeLogic.GameNetQuery)

Game server query library for CodeLogic 3 -- Valve Source Engine (CSS, CS2) and Minecraft server queries via UDP and RCON.

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

// Minecraft -- UDP query
var mcResponse = MinecraftQueryClient.QueryServer("192.168.1.20", 25565);
var motd = MinecraftQueryClient.GetStatusValue(mcResponse, "hostname");
```

## Features

- **Valve A2S_INFO** -- query Source Engine server name, map, player count, and metadata via UDP
- **Valve A2S_PLAYER** -- retrieve the full player list with scores and play durations
- **Valve RCON** -- authenticate and execute remote console commands over TCP
- **Minecraft query** -- GameSpy4 UDP protocol for server status and player info
- **Zero configuration** -- all query classes are static or standalone; no config file needed

## Configuration

CL.GameNetQuery does not require a configuration file. All query clients accept connection parameters directly.

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- CodeLogic 3.0.0+

## License

MIT -- see [LICENSE](../LICENSE) for details.
