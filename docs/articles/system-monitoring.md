# System Monitoring — CL.SystemStats

CL.SystemStats provides cross-platform CPU, memory, uptime, and per-process
monitoring. Providers exist for **Windows** (PerformanceCounters + P/Invoke
`GlobalMemoryStatusEx`) and **Linux** (the `/proc` filesystem). On unknown
platforms the library falls back to the Linux provider, which fails gracefully
when `/proc` is unavailable.

All data-returning methods return a `CodeLogic.Core.Results.Result<T>`: check
`IsSuccess`, read `Value` on success, or `Error` on failure.

---

## Registration

```csharp
var statsLib = new SystemStatsLibrary();
// Register/boot through the CodeLogic framework lifecycle
// (Configure → Initialize → Start) before calling any stats method.
```

After initialization the library exposes the stats API directly, and also via
the underlying service through the `Stats` property
(`SystemStatsService`). Both expose the same methods.

---

## Configuration (`config.systemstats.json`)

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

| Field | Default | Description |
| --- | --- | --- |
| `EnableCaching` | `true` | Cache stat results to avoid hammering OS counters. |
| `CacheDurationSeconds` | `5` | How long cached results are considered fresh (1–600). |
| `CpuSamplingIntervalMs` | `100` | Delay between the two CPU counter samples used to compute usage (1–10000). |
| `CpuSamplesForAverage` | `3` | Number of samples averaged to smooth CPU readings (1–20). |
| `EnableTemperatureMonitoring` | `true` | Platform-dependent; set `false` to avoid privileged calls. |
| `EnableProcessMonitoring` | `true` | Enable process enumeration and ranking. |
| `MaxTopProcesses` | `10` | Maximum processes returned by top-N queries (1–200). |
| `HighCpuThresholdPercent` | `90.0` | Above this, a `HighCpuUsageEvent` fires (1–100). |
| `HighMemoryThresholdPercent` | `90.0` | Above this, a `HighMemoryUsageEvent` fires (1–100). |

Configuration is validated during `Initialize`; an invalid config throws.

---

## CPU Statistics

```csharp
// Static CPU info (model, cores, vendor, architecture)
var info = await statsLib.GetCpuInfoAsync();
if (info.IsSuccess)
{
    var cpu = info.Value!;
    Console.WriteLine($"{cpu.ModelName} ({cpu.Vendor})");
    Console.WriteLine($"Cores: {cpu.PhysicalCoreCount} physical / {cpu.LogicalCoreCount} logical");
    Console.WriteLine($"Max speed: {cpu.MaxSpeedMHz} MHz, arch {cpu.Architecture}");
}

// Live usage snapshot (overall + per-core)
var stats = await statsLib.GetCpuStatsAsync();
if (stats.IsSuccess)
{
    var s = stats.Value!;
    Console.WriteLine($"CPU: {s.OverallUsagePercent:F1}%");
    for (int i = 0; i < s.PerCoreUsagePercent.Count; i++)
        Console.WriteLine($"  Core {i}: {s.PerCoreUsagePercent[i]:F1}%");
}
```

### CpuInfo

```csharp
public record CpuInfo(
    string ModelName,
    int PhysicalCoreCount,
    int LogicalCoreCount,
    string Vendor,
    double MaxSpeedMHz,
    string Architecture);
```

### CpuStats

```csharp
public record CpuStats(
    double OverallUsagePercent,             // 0–100, across all cores
    IReadOnlyList<double> PerCoreUsagePercent,
    DateTime SampledAt);                    // UTC
```

---

## Memory Statistics

```csharp
// Static memory info (total physical RAM)
var memInfo = await statsLib.GetMemoryInfoAsync();
if (memInfo.IsSuccess)
    Console.WriteLine($"Total RAM: {memInfo.Value!.TotalGiB:F1} GiB");

// Live memory usage snapshot
var mem = await statsLib.GetMemoryStatsAsync();
if (mem.IsSuccess)
{
    var m = mem.Value!;
    Console.WriteLine($"Total:     {m.TotalGiB:F1} GiB");
    Console.WriteLine($"Used:      {m.UsedMiB / 1024.0:F1} GiB");
    Console.WriteLine($"Available: {m.AvailableGiB:F1} GiB");
    Console.WriteLine($"Usage:     {m.UsagePercent:F1}%");
}
```

### MemoryInfo

```csharp
public record MemoryInfo(long TotalBytes)
{
    public double TotalMiB { get; }   // TotalBytes / 1024^2
    public double TotalGiB { get; }   // TotalBytes / 1024^3
}
```

### MemoryStats

```csharp
public record MemoryStats(
    long TotalBytes,
    long AvailableBytes,    // free + reclaimable
    long UsedBytes,         // Total − Available
    long CachedBytes,       // filesystem cache (may be 0 on Windows)
    long BuffersBytes,      // kernel buffers (Linux-specific; 0 on Windows)
    double UsagePercent,    // 0–100
    DateTime SampledAt)     // UTC
{
    public double UsedMiB      { get; }
    public double TotalGiB     { get; }
    public double AvailableGiB { get; }
}
```

---

## Uptime

```csharp
var uptime = await statsLib.GetSystemUptimeAsync();
if (uptime.IsSuccess)
    Console.WriteLine($"Uptime: {uptime.Value}");
```

---

## System Snapshot

A single call combines CPU, memory, uptime, and the top processes. It also
publishes a `SystemSnapshotTakenEvent` and evaluates the high-CPU / high-memory
thresholds.

```csharp
var snap = await statsLib.GetSystemSnapshotAsync();
if (snap.IsSuccess)
{
    var s = snap.Value!;
    Console.WriteLine($"CPU:    {s.CpuStats.OverallUsagePercent:F1}%");
    Console.WriteLine($"Memory: {s.MemoryStats.UsagePercent:F1}% used");
    Console.WriteLine($"Uptime: {s.Uptime}");
    Console.WriteLine($"Top CPU process: {s.TopProcessesByCpu[0].Name}");
}
```

### SystemSnapshot

```csharp
public record SystemSnapshot(
    CpuStats CpuStats,
    MemoryStats MemoryStats,
    TimeSpan Uptime,
    IReadOnlyList<ProcessStats> TopProcessesByCpu,
    IReadOnlyList<ProcessStats> TopProcessesByMemory,
    DateTime TakenAt);
```

---

## Per-Process Statistics

```csharp
// Specific process by PID
var proc = await statsLib.GetProcessStatsAsync(processId: 1234);
if (proc.IsSuccess)
{
    var p = proc.Value!;
    Console.WriteLine($"PID:         {p.ProcessId}");
    Console.WriteLine($"Name:        {p.Name}");
    Console.WriteLine($"CPU:         {p.CpuUsagePercent:F1}%");
    Console.WriteLine($"Memory (WS): {p.WorkingSetMiB:F1} MiB");
    Console.WriteLine($"Threads:     {p.ThreadCount}");
}

// All running processes
var all = await statsLib.GetAllProcessesAsync();

// Ranked top-N (count typically clamped/served per MaxTopProcesses)
var topCpu = await statsLib.GetTopProcessesByCpuAsync(5);
var topMem = await statsLib.GetTopProcessesByMemoryAsync(5);
```

### ProcessStats

```csharp
public record ProcessStats(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    double CpuUsagePercent,     // 0–100; may be approximate
    int ThreadCount,
    int HandleCount,            // handles (Windows) / file descriptors (Linux)
    DateTime? StartTime,        // UTC; null if unavailable
    TimeSpan? TotalCpuTime,     // null if unavailable
    string PriorityClass)
{
    public double WorkingSetMiB { get; }
}
```

---

## Events

The service publishes these events on the CodeLogic event bus when an event bus
is available:

| Event | Published when |
| --- | --- |
| `SystemSnapshotTakenEvent` | After a successful `GetSystemSnapshotAsync()`. Carries `CpuUsagePercent`, `MemoryUsagePercent`, `Uptime`, `TakenAt`. |
| `HighCpuUsageEvent` | Overall CPU usage ≥ `HighCpuThresholdPercent`. Carries `UsagePercent`, `ThresholdPercent`, `DetectedAt`. |
| `HighMemoryUsageEvent` | Memory usage ≥ `HighMemoryThresholdPercent`. Carries `UsagePercent`, `ThresholdPercent`, `DetectedAt`. |

Threshold checks run on `GetCpuStatsAsync`, `GetMemoryStatsAsync`, and
`GetSystemSnapshotAsync`.

```csharp
context.Events.Subscribe<HighCpuUsageEvent>(async evt =>
{
    Console.WriteLine($"High CPU: {evt.UsagePercent:F1}% (threshold {evt.ThresholdPercent}%)");
    await Task.CompletedTask;
});
```

---

## Caching

When `EnableCaching` is `true`, results from `GetCpuInfoAsync`,
`GetCpuStatsAsync`, `GetMemoryInfoAsync`, `GetMemoryStatsAsync`,
`GetSystemUptimeAsync`, and `GetAllProcessesAsync` are cached for
`CacheDurationSeconds`. Per-PID and top-N process queries, and full snapshots,
are not cached (they are volatile or always recomputed).

Clear the cache manually through the underlying service:

```csharp
statsLib.Stats.ClearCache();
```

---

## Health Check

The library's `HealthCheckAsync()` (invoked by the framework) reports:

- **Unhealthy** — the service is not initialized, or a probe throws.
- **Degraded** — CPU stats could not be read.
- **Healthy** — CPU stats read successfully; the message includes the current
  CPU percentage and active platform provider.
