namespace CL.SystemStats.Models;

/// <summary>
/// Statistics for a single running process.
/// </summary>
/// <param name="ProcessId">Operating system process identifier.</param>
/// <param name="Name">Process name (without extension).</param>
/// <param name="WorkingSetBytes">Current working set (resident memory) in bytes.</param>
/// <param name="CpuUsagePercent">CPU utilization percentage for this process (0–100). May be approximate.</param>
/// <param name="ThreadCount">Number of threads.</param>
/// <param name="HandleCount">Number of open handles (Windows) or file descriptors (Linux).</param>
/// <param name="StartTime">UTC time when the process was started; null if unavailable.</param>
/// <param name="TotalCpuTime">Accumulated CPU time consumed; null if unavailable.</param>
/// <param name="PriorityClass">Priority class string (e.g., "Normal", "High", "BelowNormal").</param>
public record ProcessStats(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    double CpuUsagePercent,
    int ThreadCount,
    int HandleCount,
    DateTime? StartTime,
    TimeSpan? TotalCpuTime,
    string PriorityClass
)
{
    /// <summary>Working set in mebibytes (MiB).</summary>
    public double WorkingSetMiB => WorkingSetBytes / (1024.0 * 1024.0);
}

/// <summary>
/// A full point-in-time snapshot of the system's health.
/// </summary>
/// <param name="CpuStats">Live CPU usage at the time of the snapshot.</param>
/// <param name="MemoryStats">Live memory usage at the time of the snapshot.</param>
/// <param name="Uptime">System uptime since last boot.</param>
/// <param name="TopProcessesByCpu">Top processes sorted by CPU usage.</param>
/// <param name="TopProcessesByMemory">Top processes sorted by memory usage.</param>
/// <param name="TakenAt">UTC timestamp when the snapshot was composed.</param>
public record SystemSnapshot(
    CpuStats CpuStats,
    MemoryStats MemoryStats,
    TimeSpan Uptime,
    IReadOnlyList<ProcessStats> TopProcessesByCpu,
    IReadOnlyList<ProcessStats> TopProcessesByMemory,
    DateTime TakenAt
);
