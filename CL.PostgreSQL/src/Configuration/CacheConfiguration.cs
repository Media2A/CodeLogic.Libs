using CodeLogic.Core.Configuration;

namespace CL.PostgreSQL.Configuration;

/// <summary>
/// Process-wide cache configuration for CL.PostgreSQL. Applies to all databases unless a
/// per-database override is set on <see cref="PostgreSqlDatabaseConfig.CacheEnabledOverride"/>.
/// </summary>
[ConfigSection("postgresql.cache")]
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
    /// <b>Obsolete.</b> The in-process store bounds the cache by entry count, not by bytes,
    /// and nothing reads this value. Use <see cref="MaxEntries"/>.
    /// </summary>
    [Obsolete("The in-process cache bounds by entry count, not bytes. Use MaxEntries instead; this value is not read.")]
    [ConfigField(Label = "Max Memory (MB)", Min = 0,
        Description = "Obsolete — the cache evicts by entry count. Use Max Entries.",
        Group = "Capacity", Order = 11, Collapsed = true)]
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>
    /// Default cache lifetime used by the parameterless <c>WithCache()</c> overload on the
    /// query builder and on a projected query. <c>WithCache(TimeSpan)</c> still wins where
    /// a call supplies its own TTL.
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

    /// <summary>
    /// Whether <c>CacheHitEvent</c> / <c>CacheMissEvent</c> are published. True by default;
    /// set false to silence the per-query cache observability without disabling the cache.
    /// </summary>
    [ConfigField(Label = "Publish Cache Events",
        Description = "Emit CacheHitEvent / CacheMissEvent for observability.",
        Group = "Observability", Order = 30)]
    public bool PublishEvents { get; set; } = true;
}
