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
    [ConfigField(Label = "Enable Caching", Description = "Cache stat results to avoid hammering OS counters.",
        Group = "Caching", Order = 0)]
    public bool EnableCaching { get; set; } = true;

    /// <summary>How long cached results are considered fresh (seconds). Default: 5.</summary>
    [ConfigField(Label = "Cache Duration (s)", Min = 1, Max = 600, Group = "Caching", Order = 1)]
    public int CacheDurationSeconds { get; set; } = 5;

    /// <summary>Delay between the two CPU counter samples used to compute usage (ms). Default: 100.</summary>
    [ConfigField(Label = "CPU Sampling Interval (ms)", Min = 1, Max = 10000,
        Description = "Delay between CPU counter samples when computing usage %.",
        Group = "CPU", Order = 10, Collapsed = true)]
    public int CpuSamplingIntervalMs { get; set; } = 100;

    /// <summary>Number of samples averaged to smooth CPU readings. Default: 3.</summary>
    [ConfigField(Label = "CPU Samples for Average", Min = 1, Max = 20,
        Description = "How many samples are averaged to smooth readings.",
        Group = "CPU", Order = 11, Collapsed = true)]
    public int CpuSamplesForAverage { get; set; } = 3;

    /// <summary>Whether temperature monitoring is enabled (not all platforms support this). Default: <see langword="true"/>.</summary>
    [ConfigField(Label = "Enable Temperature Monitoring",
        Description = "Platform-dependent. Set to false to avoid privileged calls.",
        Group = "Features", Order = 20)]
    public bool EnableTemperatureMonitoring { get; set; } = true;

    /// <summary>Whether process enumeration and monitoring is enabled. Default: <see langword="true"/>.</summary>
    [ConfigField(Label = "Enable Process Monitoring",
        Description = "List and rank processes by CPU/memory.",
        Group = "Features", Order = 21)]
    public bool EnableProcessMonitoring { get; set; } = true;

    /// <summary>Maximum number of processes returned by top-N queries. Default: 10.</summary>
    [ConfigField(Label = "Max Top Processes", Min = 1, Max = 200,
        Group = "Features", Order = 22, Collapsed = true)]
    public int MaxTopProcesses { get; set; } = 10;

    /// <summary>CPU usage threshold (%) that triggers a <c>HighCpuUsageEvent</c>. Default: 90.0.</summary>
    [ConfigField(Label = "High CPU Threshold (%)", Min = 1, Max = 100,
        Description = "Above this, a HighCpuUsageEvent fires.",
        Group = "Alerts", Order = 30)]
    public double HighCpuThresholdPercent { get; set; } = 90.0;

    /// <summary>Memory usage threshold (%) that triggers a <c>HighMemoryUsageEvent</c>. Default: 90.0.</summary>
    [ConfigField(Label = "High Memory Threshold (%)", Min = 1, Max = 100,
        Description = "Above this, a HighMemoryUsageEvent fires.",
        Group = "Alerts", Order = 31)]
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
