# CodeLogic.SystemStats

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SystemStats)](https://www.nuget.org/packages/CodeLogic.SystemStats)

Cross-platform system statistics (CPU, memory, processes) for [CodeLogic](https://github.com/Media2A/CodeLogic) applications on Windows and Linux.

## Install

```
dotnet add package CodeLogic.SystemStats
```

## Quick Start

All data-returning methods return a `Result<T>`: check `IsSuccess`, then read
`Value` (or `Error` on failure).

```csharp
var statsLib = new SystemStatsLibrary();
// After library initialization via CodeLogic framework:

var cpu = await statsLib.GetCpuStatsAsync();
if (cpu.IsSuccess)
    Console.WriteLine($"CPU: {cpu.Value!.OverallUsagePercent:F1}%");

var cpuInfo  = await statsLib.GetCpuInfoAsync();        // static: model, cores, vendor
var memInfo  = await statsLib.GetMemoryInfoAsync();     // static: total RAM
var memory   = await statsLib.GetMemoryStatsAsync();    // live usage
var uptime   = await statsLib.GetSystemUptimeAsync();
var snapshot = await statsLib.GetSystemSnapshotAsync(); // combined + fires event
var all      = await statsLib.GetAllProcessesAsync();
var topCpu   = await statsLib.GetTopProcessesByCpuAsync(5);
var topMem   = await statsLib.GetTopProcessesByMemoryAsync(5);
var one      = await statsLib.GetProcessStatsAsync(processId: 1234);
```

## Features

- **CPU monitoring** — static `CpuInfo` (model, cores, vendor, architecture) and live `CpuStats` (overall + per-core) with configurable sampling
- **Memory stats** — static `MemoryInfo` and live `MemoryStats` (total/used/available/cached/buffers), with MiB/GiB helper properties
- **Uptime** — system uptime since last boot
- **Process monitoring** — enumerate all processes, query by PID, or rank by CPU/memory usage
- **System snapshots** — combined CPU + memory + uptime + top processes in a single call
- **Threshold events** — publishes `HighCpuUsageEvent` / `HighMemoryUsageEvent` when thresholds are exceeded, and `SystemSnapshotTakenEvent` after each snapshot
- **Result caching** — cached reads (configurable duration); clear via `statsLib.Stats.ClearCache()`
- **Cross-platform providers** — Windows (PerformanceCounters + P/Invoke `GlobalMemoryStatusEx`) and Linux (`/proc`)

## Configuration

Config file: `config.systemstats.json`

```json
{
  "EnableCaching": true,
  "CacheDurationSeconds": 5,
  "CpuSamplingIntervalMs": 100,
  "CpuSamplesForAverage": 3,
  "EnableTemperatureMonitoring": true,
  "EnableProcessMonitoring": true,
  "MaxTopProcesses": 10,
  "HighCpuThresholdPercent": 90.0,
  "HighMemoryThresholdPercent": 90.0
}
```

## Events

Published on the CodeLogic event bus (when one is available):

- `SystemSnapshotTakenEvent` — after each successful `GetSystemSnapshotAsync()`.
- `HighCpuUsageEvent` — when overall CPU usage ≥ `HighCpuThresholdPercent`.
- `HighMemoryUsageEvent` — when memory usage ≥ `HighMemoryThresholdPercent`.

Threshold checks run on `GetCpuStatsAsync`, `GetMemoryStatsAsync`, and
`GetSystemSnapshotAsync`.

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- Windows: `System.Diagnostics.PerformanceCounter`, `System.Management`
- Linux: `/proc` filesystem access

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
