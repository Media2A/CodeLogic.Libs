using CodeLogic.Core.Configuration;

namespace CL.MySQL2.Configuration;

/// <summary>
/// Process-wide cache configuration for CL.MySQL2. Applies to all databases unless a
/// per-database override is set on <see cref="MySqlDatabaseConfig.CacheEnabledOverride"/>.
/// </summary>
[ConfigSection("mysql.cache")]
public sealed class CacheConfiguration : ConfigModelBase
{
    /// <summary>Master switch for the query result cache.</summary>
    [ConfigField(Label = "Enabled", Description = "Turn the query result cache on or off globally.",
        Group = "General", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of cached entries across all databases.</summary>
    [ConfigField(Label = "Max Entries", Min = 0,
        Description = "Total cached entries. Soft cap enforced lazily.",
        Group = "Capacity", Order = 10)]
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>
    /// Soft memory cap for the cache in megabytes. Currently advisory —
    /// eviction uses entry count; memory accounting lands in a follow-up.
    /// </summary>
    [ConfigField(Label = "Max Memory (MB)", Min = 0,
        Description = "Soft memory cap. Advisory; current eviction uses entry count.",
        Group = "Capacity", Order = 11, Collapsed = true)]
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>
    /// Default TTL used when <c>WithCache()</c> is called without arguments.
    /// </summary>
    [ConfigField(Label = "Default TTL (seconds)", Min = 1,
        Description = "Default cache lifetime when .WithCache() has no TTL argument.",
        Group = "Behavior", Order = 20)]
    public int DefaultTtlSeconds { get; set; } = 60;

    /// <summary>
    /// Time-near-now closure values used as query parameters (e.g.
    /// <c>DateTime.UtcNow.AddDays(-30)</c>) are rounded down to this window when
    /// forming the cache key. Without this, every call produces a unique cache key
    /// and nothing ever hits. Set to 0 to disable and use raw timestamps.
    /// </summary>
    [ConfigField(Label = "Time Quantize (seconds)", Min = 0,
        Description = "Round DateTime parameters to this window when building cache keys. 0 disables.",
        Group = "Behavior", Order = 21)]
    public int TimeQuantizeSeconds { get; set; } = 60;

    /// <summary>Whether the cache publishes hit/miss events on the event bus.</summary>
    [ConfigField(Label = "Publish Cache Events",
        Description = "Emit CacheHitEvent / CacheMissEvent for observability.",
        Group = "Observability", Order = 30)]
    public bool PublishEvents { get; set; } = true;
}
