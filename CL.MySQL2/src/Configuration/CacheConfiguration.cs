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
    /// Obsolete. The in-process store bounds the cache by entry count, not by bytes —
    /// use <see cref="MaxEntries"/>. Retained so existing config files keep deserializing.
    /// </summary>
    [Obsolete("The in-process cache store bounds by entry count, not bytes. Use MaxEntries instead; this value is ignored.")]
    [ConfigField(Label = "Max Memory (MB)", Min = 0,
        Description = "Obsolete and ignored — the in-process store evicts by entry count. Use Max Entries.",
        Group = "Capacity", Order = 11, Collapsed = true)]
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>
    /// Default cache lifetime used by the parameterless <c>WithCache()</c> overload on
    /// <see cref="Services.QueryBuilder{T}"/> and <see cref="Services.ProjectedQuery{TSource, TResult}"/>.
    /// A <c>WithCache(TimeSpan)</c> call still wins for that query.
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
    /// Whether <c>CacheHitEvent</c> / <c>CacheMissEvent</c> are published. When false the cache
    /// still works; it just stops emitting hit/miss observability events. Default: true.
    /// </summary>
    [ConfigField(Label = "Publish Cache Events",
        Description = "Emit CacheHitEvent / CacheMissEvent for observability.",
        Group = "Observability", Order = 30)]
    public bool PublishEvents { get; set; } = true;
}
