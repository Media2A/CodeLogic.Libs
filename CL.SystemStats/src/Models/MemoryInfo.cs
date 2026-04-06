namespace CL.SystemStats.Models;

/// <summary>
/// Static information about the system's physical memory.
/// </summary>
/// <param name="TotalBytes">Total physical RAM in bytes.</param>
public record MemoryInfo(long TotalBytes)
{
    /// <summary>Total physical RAM in mebibytes (MiB).</summary>
    public double TotalMiB => TotalBytes / (1024.0 * 1024.0);

    /// <summary>Total physical RAM in gibibytes (GiB).</summary>
    public double TotalGiB => TotalBytes / (1024.0 * 1024.0 * 1024.0);
}

/// <summary>
/// A live memory usage snapshot.
/// </summary>
/// <param name="TotalBytes">Total physical RAM in bytes.</param>
/// <param name="AvailableBytes">Available (free + reclaimable) RAM in bytes.</param>
/// <param name="UsedBytes">RAM currently in use (Total − Available) in bytes.</param>
/// <param name="CachedBytes">Memory used for filesystem cache in bytes (may be 0 on Windows).</param>
/// <param name="BuffersBytes">Memory used for kernel buffers in bytes (Linux-specific; 0 on Windows).</param>
/// <param name="UsagePercent">Percentage of RAM in use (0–100).</param>
/// <param name="SampledAt">UTC timestamp when the sample was taken.</param>
public record MemoryStats(
    long TotalBytes,
    long AvailableBytes,
    long UsedBytes,
    long CachedBytes,
    long BuffersBytes,
    double UsagePercent,
    DateTime SampledAt
)
{
    /// <summary>Used RAM in mebibytes (MiB).</summary>
    public double UsedMiB => UsedBytes / (1024.0 * 1024.0);

    /// <summary>Total RAM in gibibytes (GiB).</summary>
    public double TotalGiB => TotalBytes / (1024.0 * 1024.0 * 1024.0);

    /// <summary>Available RAM in gibibytes (GiB).</summary>
    public double AvailableGiB => AvailableBytes / (1024.0 * 1024.0 * 1024.0);
}
