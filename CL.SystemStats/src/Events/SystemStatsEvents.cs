using CodeLogic.Core.Events;

namespace CL.SystemStats.Events;

/// <summary>
/// Published after a full <see cref="Models.SystemSnapshot"/> is successfully taken.
/// </summary>
/// <param name="CpuUsagePercent">Overall CPU utilization at snapshot time.</param>
/// <param name="MemoryUsagePercent">Memory utilization percentage at snapshot time.</param>
/// <param name="Uptime">System uptime at snapshot time.</param>
/// <param name="TakenAt">UTC timestamp of the snapshot.</param>
public record SystemSnapshotTakenEvent(
    double CpuUsagePercent,
    double MemoryUsagePercent,
    TimeSpan Uptime,
    DateTime TakenAt
) : IEvent;

/// <summary>
/// Published when the overall CPU usage exceeds the configured threshold.
/// </summary>
/// <param name="UsagePercent">The measured CPU usage percentage.</param>
/// <param name="ThresholdPercent">The configured threshold that was breached.</param>
/// <param name="DetectedAt">UTC timestamp when the threshold breach was detected.</param>
public record HighCpuUsageEvent(
    double UsagePercent,
    double ThresholdPercent,
    DateTime DetectedAt
) : IEvent;

/// <summary>
/// Published when the memory usage exceeds the configured threshold.
/// </summary>
/// <param name="UsagePercent">The measured memory usage percentage.</param>
/// <param name="ThresholdPercent">The configured threshold that was breached.</param>
/// <param name="DetectedAt">UTC timestamp when the threshold breach was detected.</param>
public record HighMemoryUsageEvent(
    double UsagePercent,
    double ThresholdPercent,
    DateTime DetectedAt
) : IEvent;
