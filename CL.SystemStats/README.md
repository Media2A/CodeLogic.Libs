# CL.SystemStats

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.SystemStats)](https://www.nuget.org/packages/CodeLogic.SystemStats)

Cross-platform system statistics (CPU, memory, processes) for CodeLogic 3 applications on Windows and Linux.

## Install

```
dotnet add package CodeLogic.SystemStats
```

## Quick Start

```csharp
var statsLib = new SystemStatsLibrary();
// After library initialization via CodeLogic framework:

var cpu = await statsLib.GetCpuStatsAsync();
Console.WriteLine($"CPU: {cpu.Value!.OverallUsagePercent:F1}%");

var memory = await statsLib.GetMemoryStatsAsync();
var snapshot = await statsLib.GetSystemSnapshotAsync();
var topCpu = await statsLib.GetTopProcessesByCpuAsync(5);
```

## Features

- **CPU monitoring** — static info and live usage snapshots with configurable sampling
- **Memory stats** — total, used, and available memory for the system
- **Process monitoring** — enumerate all processes, query by PID, or rank by CPU/memory usage
- **System snapshots** — combined CPU + memory + uptime in a single call
- **Threshold events** — fires `HighCpuUsageEvent` / `HighMemoryUsageEvent` when limits are exceeded

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

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- CodeLogic 3.0.0+
- Windows: `System.Diagnostics.PerformanceCounter`, `System.Management`
- Linux: `/proc` filesystem access

## License

MIT -- see [LICENSE](../LICENSE) for details.
