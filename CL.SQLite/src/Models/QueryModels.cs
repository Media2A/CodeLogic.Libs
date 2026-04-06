namespace CL.SQLite.Models;

public record SQLiteQuery
{
    public required string QueryString { get; init; }
    public Dictionary<string, object?> Parameters { get; init; } = new();
}

public record TableSyncResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }

    public static TableSyncResult Succeeded(string msg) => new() { Success = true, Message = msg };
    public static TableSyncResult Failed(string msg, Exception? ex = null) => new() { Success = false, Message = msg, Exception = ex };
}

public enum TransactionIsolation
{
    Deferred,
    Immediate,
    Exclusive
}

public class WhereCondition
{
    public required string Column { get; init; }
    public required string Operator { get; init; }
    public object? Value { get; init; }
    public string LogicalOperator { get; init; } = "AND";
}

public class OrderByClause
{
    public required string Column { get; init; }
    public required SortOrder Order { get; init; }
}

public enum SortOrder
{
    Asc,
    Desc
}

public class AggregateFunction
{
    public required AggregateType Type { get; init; }
    public required string Column { get; init; }
    public required string Alias { get; init; }
}

public enum AggregateType
{
    Sum,
    Avg,
    Min,
    Max,
    Count
}

public class PagedResult<T>
{
    public required List<T> Items { get; init; }
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public long TotalItems { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalItems / PageSize) : 0;
}
