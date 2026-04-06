namespace CL.PostgreSQL.Models;

/// <summary>
/// Represents a page of results from a paged query.
/// </summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public long TotalItems { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalItems / PageSize) : 0;
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}

/// <summary>A single WHERE predicate clause for use in dynamic queries.</summary>
public sealed class WhereCondition
{
    public required string Column { get; init; }
    public string Operator { get; init; } = "=";
    public object? Value { get; init; }
    public string LogicalOperator { get; init; } = "AND";
}

/// <summary>Specifies an ORDER BY clause for a query.</summary>
public sealed class OrderByClause
{
    public required string Column { get; init; }
    public SortOrder Order { get; init; } = SortOrder.Asc;
}

/// <summary>Specifies a JOIN clause for a query.</summary>
public sealed class JoinClause
{
    public JoinType Type { get; init; } = JoinType.Inner;
    public required string Table { get; init; }
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

/// <summary>Describes a SQL aggregate function applied to a column.</summary>
public sealed class AggregateFunction
{
    public AggregateType Type { get; init; }
    public required string Column { get; init; }
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

/// <summary>The result of a table synchronization operation.</summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public string TableName { get; init; } = string.Empty;
    public string SchemaName { get; init; } = "public";
    public List<string> Operations { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public TimeSpan Duration { get; init; }
}
