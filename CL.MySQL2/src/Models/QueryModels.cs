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

/// <summary>
/// A single WHERE predicate clause for use in dynamic queries.
/// </summary>
public sealed class WhereCondition
{
    /// <summary>The column name to filter on. Required.</summary>
    public required string Column { get; init; }

    /// <summary>Comparison operator (e.g., "=", "!=", ">", "LIKE"). Default: "=".</summary>
    public string Operator { get; init; } = "=";

    /// <summary>The value to compare against.</summary>
    public object? Value { get; init; }

    /// <summary>Logical connector to the previous clause ("AND" or "OR"). Default: "AND".</summary>
    public string LogicalOperator { get; init; } = "AND";
}

/// <summary>
/// Specifies an ORDER BY clause for a query.
/// </summary>
public sealed class OrderByClause
{
    /// <summary>The column to sort by. Required.</summary>
    public required string Column { get; init; }

    /// <summary>Sort direction. Default: Asc.</summary>
    public SortOrder Order { get; init; } = SortOrder.Asc;
}

/// <summary>
/// Specifies a JOIN clause for a query.
/// </summary>
public sealed class JoinClause
{
    /// <summary>The JOIN type. Default: Inner.</summary>
    public JoinType Type { get; init; } = JoinType.Inner;

    /// <summary>The table to join. Required.</summary>
    public required string Table { get; init; }

    /// <summary>The ON condition (e.g., "a.id = b.a_id"). Required.</summary>
    public required string Condition { get; init; }
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
/// Describes a SQL aggregate function applied to a column.
/// </summary>
public sealed class AggregateFunction
{
    /// <summary>The aggregate type (COUNT, SUM, etc.).</summary>
    public AggregateType Type { get; init; }

    /// <summary>The column the function is applied to. Required.</summary>
    public required string Column { get; init; }

    /// <summary>The alias for the result column. Required.</summary>
    public required string Alias { get; init; }
}

/// <summary>SQL aggregate function types.</summary>
public enum AggregateType
{
    Count,
    Sum,
    Avg,
    Min,
    Max
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
