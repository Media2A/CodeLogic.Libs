using CodeLogic.Core.Configuration;

namespace CL.SystemStats.Models;

/// <summary>
/// Configuration for the <c>CL.SystemStats</c> library.
/// Serialized as <c>config.systemstats.json</c> in the library's config directory.
/// </summary>
[ConfigSection("systemstats")]
public class SystemStatsConfig : ConfigModelBase
{
    /// <summary>Whether result caching is enabled. Default: <see langword="true"/>.</summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>How long cached results are considered fresh (seconds). Default: 5.</summary>
    public int CacheDurationSeconds { get; set; } = 5;

    /// <summary>Delay between the two CPU counter samples used to compute usage (ms). Default: 100.</summary>
    public int CpuSamplingIntervalMs { get; set; } = 100;

    /// <summary>Number of samples averaged to smooth CPU readings. Default: 3.</summary>
    public int CpuSamplesForAverage { get; set; } = 3;

    /// <summary>Whether temperature monitoring is enabled (not all platforms support this). Default: <see langword="true"/>.</summary>
    public bool EnableTemperatureMonitoring { get; set; } = true;

    /// <summary>Whether process enumeration and monitoring is enabled. Default: <see langword="true"/>.</summary>
    public bool EnableProcessMonitoring { get; set; } = true;

    /// <summary>Maximum number of processes returned by top-N queries. Default: 10.</summary>
    public int MaxTopProcesses { get; set; } = 10;

    /// <summary>CPU usage threshold (%) that triggers a <c>HighCpuUsageEvent</c>. Default: 90.0.</summary>
    public double HighCpuThresholdPercent { get; set; } = 90.0;

    /// <summary>Memory usage threshold (%) that triggers a <c>HighMemoryUsageEvent</c>. Default: 90.0.</summary>
    public double HighMemoryThresholdPercent { get; set; } = 90.0;

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (CacheDurationSeconds <= 0)
            errors.Add("CacheDurationSeconds must be > 0");

        if (CpuSamplingIntervalMs <= 0)
            errors.Add("CpuSamplingIntervalMs must be > 0");

        if (CpuSamplesForAverage <= 0)
            errors.Add("CpuSamplesForAverage must be > 0");

        if (MaxTopProcesses <= 0)
            errors.Add("MaxTopProcesses must be > 0");

        if (HighCpuThresholdPercent is <= 0 or > 100)
            errors.Add("HighCpuThresholdPercent must be in range (0, 100]");

        if (HighMemoryThresholdPercent is <= 0 or > 100)
            errors.Add("HighMemoryThresholdPercent must be in range (0, 100]");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}
