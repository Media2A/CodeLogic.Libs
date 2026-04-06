# System Monitoring — CL.SystemStats

CL.SystemStats provides cross-platform CPU usage, memory statistics (total/available/used), and per-process monitoring. Works on Windows, Linux, and macOS.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.SystemStats.SystemStatsLibrary>();
```

---

## Configuration (`config.systemstats.json`)

```json
{
  "CpuSampleIntervalMs": 1000,
  "EnablePerProcessStats": true,
  "HistoryRetentionMinutes": 60
}
```

---

## CPU Statistics

```csharp
var stats = context.GetLibrary<CL.SystemStats.SystemStatsLibrary>();

// Current CPU usage (0.0 - 100.0)
double cpuPercent = await stats.GetCpuUsageAsync();
Console.WriteLine($"CPU: {cpuPercent:F1}%");

// Per-core breakdown
var cores = await stats.GetPerCoreCpuUsageAsync();
for (int i = 0; i < cores.Length; i++)
    Console.WriteLine($"  Core {i}: {cores[i]:F1}%");
```

---

## Memory Statistics

```csharp
var memory = await stats.GetMemoryStatsAsync();

Console.WriteLine($"Total:     {memory.TotalBytes / 1_073_741_824.0:F1} GB");
Console.WriteLine($"Used:      {memory.UsedBytes / 1_073_741_824.0:F1} GB");
Console.WriteLine($"Available: {memory.AvailableBytes / 1_073_741_824.0:F1} GB");
Console.WriteLine($"Usage:     {memory.UsagePercent:F1}%");
```

### MemoryStats

```csharp
public sealed class MemoryStats
{
    public long TotalBytes     { get; init; }
    public long UsedBytes      { get; init; }
    public long AvailableBytes { get; init; }
    public double UsagePercent { get; init; }   // 0.0 - 100.0
    public DateTime MeasuredAt { get; init; }
}
```

---

## System Snapshot

Get CPU and memory in one call:

```csharp
var snapshot = await stats.GetSnapshotAsync();

Console.WriteLine($"CPU:    {snapshot.CpuPercent:F1}%");
Console.WriteLine($"Memory: {snapshot.Memory.UsagePercent:F1}% used");
Console.WriteLine($"Uptime: {snapshot.SystemUptime}");
```

### SystemSnapshot

```csharp
public sealed class SystemSnapshot
{
    public double CpuPercent    { get; init; }
    public MemoryStats Memory   { get; init; }
    public TimeSpan SystemUptime { get; init; }
    public int ProcessorCount   { get; init; }
    public string OsDescription { get; init; }
    public DateTime MeasuredAt  { get; init; }
}
```

---

## Per-Process Statistics

```csharp
// Current process
var proc = await stats.GetCurrentProcessStatsAsync();
Console.WriteLine($"PID:         {proc.ProcessId}");
Console.WriteLine($"CPU:         {proc.CpuPercent:F1}%");
Console.WriteLine($"Memory (WS): {proc.WorkingSetBytes / 1_048_576.0:F1} MB");
Console.WriteLine($"Threads:     {proc.ThreadCount}");

// Specific process by PID
var other = await stats.GetProcessStatsAsync(pid: 1234);

// All running processes
var allProcesses = await stats.GetAllProcessStatsAsync();
var top5 = allProcesses
    .OrderByDescending(p => p.CpuPercent)
    .Take(5);
```

### ProcessStats

```csharp
public sealed class ProcessStats
{
    public int ProcessId         { get; init; }
    public string ProcessName    { get; init; }
    public double CpuPercent     { get; init; }
    public long WorkingSetBytes  { get; init; }
    public long PrivateMemoryBytes { get; init; }
    public int ThreadCount       { get; init; }
    public TimeSpan CpuTime      { get; init; }
    public DateTime StartTime    { get; init; }
}
```

---

## Continuous Monitoring

Use with the CodeLogic event bus to emit alerts when thresholds are exceeded:

```csharp
public async Task OnStartAsync(LibraryContext context)
{
    _ = MonitorAsync(context, _cts.Token);
}

private async Task MonitorAsync(LibraryContext context, CancellationToken ct)
{
    var stats = context.GetLibrary<CL.SystemStats.SystemStatsLibrary>();

    while (!ct.IsCancellationRequested)
    {
        var snapshot = await stats.GetSnapshotAsync();

        if (snapshot.CpuPercent > 90.0)
        {
            await context.Events.PublishAsync(new ComponentAlertEvent(
                ComponentId: Manifest.Id,
                Message:     $"High CPU: {snapshot.CpuPercent:F1}%",
                Severity:    AlertSeverity.Warning
            ));
        }

        if (snapshot.Memory.UsagePercent > 85.0)
        {
            await context.Events.PublishAsync(new ComponentAlertEvent(
                ComponentId: Manifest.Id,
                Message:     $"High memory: {snapshot.Memory.UsagePercent:F1}%",
                Severity:    AlertSeverity.Warning
            ));
        }

        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}
```

---

## Health Check

The health check compares current CPU and memory usage against configurable thresholds:

```csharp
// Returns Healthy if CPU < 80% and memory < 80%
// Returns Degraded if CPU > 80% or memory > 80%
// Returns Unhealthy if CPU > 95% or memory > 95%
var status = await stats.HealthCheckAsync();
```
