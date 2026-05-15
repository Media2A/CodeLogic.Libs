namespace CL.MySQL2.Services;

/// <summary>
/// Diagnostic snapshot of <see cref="QueryCache"/> state. Surfaced via
/// <see cref="MySQL2Library.GetCacheStats"/> for the admin UI so operators
/// can see what's actually living in cache without dumping values.
/// </summary>
/// <param name="TotalEntries">Total entries in the underlying store, across all tables.</param>
/// <param name="EntriesByTable">Entry count grouped by tableName.</param>
/// <param name="TableVersions">
/// Per-table version counters. Bumped on every mutation; participates in
/// the cache key so old entries become un-hittable on the read path.
/// </param>
public sealed record QueryCacheStats(
    int TotalEntries,
    System.Collections.Generic.IReadOnlyDictionary<string, int> EntriesByTable,
    System.Collections.Generic.IReadOnlyDictionary<string, long> TableVersions);
