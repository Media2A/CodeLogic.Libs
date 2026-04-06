namespace CL.SystemStats.Models;

/// <summary>
/// Static information about the system's CPU(s).
/// </summary>
/// <param name="ModelName">Processor model name (e.g., "Intel(R) Core(TM) i7-9750H").</param>
/// <param name="PhysicalCoreCount">Number of physical CPU cores.</param>
/// <param name="LogicalCoreCount">Number of logical processors (including hyper-threading).</param>
/// <param name="Vendor">Processor vendor string (e.g., "GenuineIntel").</param>
/// <param name="MaxSpeedMHz">Maximum processor clock speed in MHz (0 if unknown).</param>
/// <param name="Architecture">Architecture description (e.g., "X64", "Arm64").</param>
public record CpuInfo(
    string ModelName,
    int PhysicalCoreCount,
    int LogicalCoreCount,
    string Vendor,
    double MaxSpeedMHz,
    string Architecture
);

/// <summary>
/// A live CPU usage snapshot.
/// </summary>
/// <param name="OverallUsagePercent">Overall CPU utilization across all cores (0–100).</param>
/// <param name="PerCoreUsagePercent">Per-core utilization percentages (index = core number).</param>
/// <param name="SampledAt">UTC timestamp when the sample was taken.</param>
public record CpuStats(
    double OverallUsagePercent,
    IReadOnlyList<double> PerCoreUsagePercent,
    DateTime SampledAt
);
