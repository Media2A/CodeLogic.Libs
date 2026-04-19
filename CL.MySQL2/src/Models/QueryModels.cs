namespace CL.MySQL2.Models;

/// <summary>
/// Represents a page of results from a paged query.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>The items on the current page.</summary>
    public List<T> Items { get; init; } = [];

    /// <summary>The 1-based page number.</summary>
    public int PageNumber { get; init; }

    /// <summary>The maximum number of items per page.</summary>
    public int PageSize { get; init; }

    /// <summary>Total number of items across all pages.</summary>
    public long TotalItems { get; init; }

    /// <summary>Total number of pages.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalItems / PageSize) : 0;

    /// <summary>True when there is a page before this one.</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>True when there is a page after this one.</summary>
    public bool HasNextPage => PageNumber < TotalPages;
}

/// <summary>SQL JOIN types.</summary>
public enum JoinType
{
    Inner,
    Left,
    Right,
    Cross
}

/// <summary>
/// The result of a table synchronization operation.
/// </summary>
public sealed class SyncResult
{
    /// <summary>Whether the sync completed without errors.</summary>
    public bool Success { get; init; }

    /// <summary>The name of the table that was synced.</summary>
    public string TableName { get; init; } = string.Empty;

    /// <summary>DDL operations performed (e.g., "CREATE TABLE", "ADD COLUMN x").</summary>
    public List<string> Operations { get; init; } = [];

    /// <summary>Any non-fatal errors encountered during the sync.</summary>
    public List<string> Errors { get; init; } = [];

    /// <summary>How long the sync took.</summary>
    public TimeSpan Duration { get; init; }
}
