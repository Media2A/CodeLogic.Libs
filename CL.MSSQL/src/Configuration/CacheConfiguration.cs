using CodeLogic.Core.Configuration;

namespace CL.MSSQL.Configuration;

/// <summary>
/// Process-wide cache configuration for CL.MSSQL. Applies to all databases unless a
/// per-database override is set on <see cref="SqlServerDatabaseConfig.CacheEnabledOverride"/>.
/// </summary>
[ConfigSection("mssql.cache")]
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
    /// Not used. The in-process store bounds the cache by entry count, not by bytes —
    /// size it with <see cref="MaxEntries"/>.
    /// </summary>
    [Obsolete("Not applied. The in-process cache store bounds by entry count, not bytes — " +
              "use MaxEntries instead.")]
    [ConfigField(Label = "Max Memory (MB)", Min = 0,
        Description = "Obsolete. Not applied — the store evicts by entry count; use Max Entries.",
        Group = "Capacity", Order = 11, Collapsed = true)]
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>
    /// TTL used by the parameterless <c>WithCache()</c> overload on the query builder and on a
    /// projected query. <c>WithCache(TimeSpan)</c> still wins for a query that names its own TTL.
    /// </summary>
    [ConfigField(Label = "Default TTL (seconds)", Min = 1,
        Description = "TTL used by .WithCache() when the query does not supply one.",
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
    /// Whether a cache lookup publishes <c>CacheHitEvent</c> / <c>CacheMissEvent</c>. Turn it
    /// off on a hot path where the events are only noise; the cache itself keeps working and
    /// <c>QueryExecutedEvent</c> still carries its <c>CacheHit</c> flag.
    /// </summary>
    [ConfigField(Label = "Publish Cache Events",
        Description = "Publish CacheHitEvent / CacheMissEvent on every cache lookup.",
        Group = "Observability", Order = 30)]
    public bool PublishEvents { get; set; } = true;
}
