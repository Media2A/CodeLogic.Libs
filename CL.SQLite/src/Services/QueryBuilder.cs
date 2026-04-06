using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using CL.SQLite.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.Sqlite;

namespace CL.SQLite.Services;

/// <summary>
/// Fluent query builder for entity type <typeparamref name="T"/>.
/// Chains WHERE, ORDER BY, GROUP BY, LIMIT/OFFSET clauses and executes
/// terminal operations returning <see cref="Result{T}"/>.
/// </summary>
public sealed class QueryBuilder<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly int _slowQueryThresholdMs;

    // Builder state
    private readonly List<(string Clause, Dictionary<string, object?> Params)> _wheres = [];
    private readonly List<string> _orderBys = [];
    private readonly List<string> _groupBys = [];
    private int? _limit;
    private int? _offset;
    private string? _selectColumns;
    private int _paramCounter;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>
    /// Initializes a new instance of <see cref="QueryBuilder{T}"/>.
    /// </summary>
    /// <param name="connectionManager">The connection manager used to obtain and release database connections.</param>
    /// <param name="logger">Optional logger for debug and warning output.</param>
    /// <param name="slowQueryThresholdMs">Queries exceeding this threshold in milliseconds are logged as warnings.</param>
    public QueryBuilder(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        int slowQueryThresholdMs = 500)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    // ── Fluent chain methods ──────────────────────────────────────────────────

    /// <summary>
    /// Adds a WHERE condition translated from the given LINQ predicate expression.
    /// Multiple calls are combined with AND.
    /// </summary>
    /// <param name="predicate">A LINQ predicate expression to translate to SQL.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        var (clause, parms) = SQLiteExpressionVisitor.Parse(predicate);
        // Re-key parameters to avoid collisions
        var rekeyed = new Dictionary<string, object?>();
        foreach (var kv in parms)
        {
            var newKey = $"@qb_{_paramCounter++}";
            rekeyed[newKey] = kv.Value;
            clause = clause.Replace(kv.Key, newKey);
        }
        _wheres.Add((clause, rekeyed));
        return this;
    }

    /// <summary>
    /// Adds an ascending ORDER BY clause for the column selected by <paramref name="keySelector"/>.
    /// </summary>
    /// <param name="keySelector">Expression identifying the column to sort by.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SQLiteExpressionVisitor.ParseOrderBy(keySelector);
        _orderBys.Add($"\"{col}\" ASC");
        return this;
    }

    /// <summary>
    /// Adds a descending ORDER BY clause for the column selected by <paramref name="keySelector"/>.
    /// </summary>
    /// <param name="keySelector">Expression identifying the column to sort by.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SQLiteExpressionVisitor.ParseOrderBy(keySelector);
        _orderBys.Add($"\"{col}\" DESC");
        return this;
    }

    /// <summary>
    /// Adds a secondary ascending ORDER BY clause for the column selected by <paramref name="keySelector"/>.
    /// </summary>
    /// <param name="keySelector">Expression identifying the column to sort by.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> ThenBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SQLiteExpressionVisitor.ParseOrderBy(keySelector);
        _orderBys.Add($"\"{col}\" ASC");
        return this;
    }

    /// <summary>
    /// Adds a secondary descending ORDER BY clause for the column selected by <paramref name="keySelector"/>.
    /// </summary>
    /// <param name="keySelector">Expression identifying the column to sort by.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SQLiteExpressionVisitor.ParseOrderBy(keySelector);
        _orderBys.Add($"\"{col}\" DESC");
        return this;
    }

    /// <summary>
    /// Sets the maximum number of rows to return (LIMIT clause).
    /// </summary>
    /// <param name="count">The maximum row count.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> Limit(int count) { _limit = count; return this; }

    /// <summary>
    /// Sets the number of rows to skip before returning results (OFFSET clause).
    /// </summary>
    /// <param name="count">The number of rows to skip.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> Offset(int count) { _offset = count; return this; }

    /// <summary>
    /// Alias for <see cref="Limit"/>. Sets the maximum number of rows to return.
    /// </summary>
    /// <param name="count">The maximum row count.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> Take(int count) => Limit(count);

    /// <summary>
    /// Alias for <see cref="Offset"/>. Sets the number of rows to skip.
    /// </summary>
    /// <param name="count">The number of rows to skip.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> Skip(int count) => Offset(count);

    /// <summary>
    /// Restricts the SELECT column list to those referenced in <paramref name="selector"/>.
    /// </summary>
    /// <param name="selector">Expression identifying the column(s) to include in the SELECT clause.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> Select(Expression<Func<T, object?>> selector)
    {
        _selectColumns = SQLiteExpressionVisitor.ParseSelect(selector);
        return this;
    }

    /// <summary>
    /// Adds a GROUP BY clause for the column selected by <paramref name="keySelector"/>.
    /// </summary>
    /// <param name="keySelector">Expression identifying the column to group by.</param>
    /// <returns>This builder instance for fluent chaining.</returns>
    public QueryBuilder<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = SQLiteExpressionVisitor.ParseGroupBy(keySelector);
        _groupBys.Add($"\"{col}\"");
        return this;
    }

    // ── Terminal methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Executes the built query and returns all matching rows as a list.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of matching entities.</returns>
    public async Task<Result<List<T>>> ToListAsync(CancellationToken ct = default)
    {
        try
        {
            var (sql, parms) = BuildSelectSql();
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapReader(reader));
                return items;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder.ToListAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    /// <summary>
    /// Executes the query with LIMIT 1 and returns the first matching entity, or <c>null</c> if none found.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the first entity or <c>null</c>.</returns>
    public async Task<Result<T?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        try
        {
            Limit(1);
            var (sql, parms) = BuildSelectSql();
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await _connectionManager.ExecuteAsync<T?>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapReader(reader) : null;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<T?>.Success(result);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder.FirstOrDefaultAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    /// <summary>
    /// Executes the query as a paginated request, returning a page of results alongside the total row count.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of rows per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing a <see cref="PagedResult{T}"/> with items and total count.</returns>
    public async Task<Result<PagedResult<T>>> ToPagedListAsync(
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM \"{tableName}\"{whereClause}{groupBySql}";
            var (dataSql, dataParms) = BuildSelectSql(page, pageSize);
            LogQuery(dataSql);

            var sw = Stopwatch.StartNew();
            var (items, total) = await _connectionManager.ExecuteAsync<(List<T>, long)>(async conn =>
            {
                await using var countCmd = BuildCommand(conn, countSql, parms);
                var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                await using var cmd = BuildCommand(conn, dataSql, dataParms);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var entities = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    entities.Add(MapReader(reader));

                return (entities, totalCount);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(dataSql, sw.ElapsedMilliseconds);

            return Result<PagedResult<T>>.Success(new PagedResult<T>
            {
                Items = items,
                PageNumber = page,
                PageSize = pageSize,
                TotalItems = total
            });
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder.ToPagedListAsync failed: {ex.Message}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    /// <summary>
    /// Executes a COUNT(*) query using the current WHERE conditions and returns the total row count.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the number of matching rows.</returns>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT COUNT(*) FROM \"{tableName}\"{whereClause}";

            LogQuery(sql);
            var count = await _connectionManager.ExecuteAsync<long>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null ? 0L : Convert.ToInt64(result);
            }, ct).ConfigureAwait(false);

            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder.CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    /// <summary>
    /// Executes a SUM aggregate over the column identified by <paramref name="selector"/>.
    /// </summary>
    /// <param name="selector">Expression identifying the numeric column to sum.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the sum value.</returns>
    public Task<Result<TResult>> SumAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("SUM", selector, ct);

    /// <summary>
    /// Executes a MAX aggregate over the column identified by <paramref name="selector"/>.
    /// </summary>
    /// <param name="selector">Expression identifying the column to find the maximum value of.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the maximum value.</returns>
    public Task<Result<TResult>> MaxAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("MAX", selector, ct);

    /// <summary>
    /// Executes a MIN aggregate over the column identified by <paramref name="selector"/>.
    /// </summary>
    /// <param name="selector">Expression identifying the column to find the minimum value of.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the minimum value.</returns>
    public Task<Result<TResult>> MinAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("MIN", selector, ct);

    /// <summary>
    /// Deletes all rows matching the current WHERE conditions.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the number of rows deleted.</returns>
    public async Task<Result<int>> DeleteAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"DELETE FROM \"{tableName}\"{whereClause}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();
            var affected = await _connectionManager.ExecuteAsync<int>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder.DeleteAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "sqlite.delete_failed"));
        }
    }

    /// <summary>
    /// Updates all rows matching the current WHERE conditions with the specified column values.
    /// </summary>
    /// <param name="updates">A dictionary mapping column names to their new values.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the number of rows updated.</returns>
    public async Task<Result<int>> UpdateAsync(
        Dictionary<string, object?> updates,
        CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, whereParms) = BuildWhereSql();
            var setClauses = string.Join(", ", updates.Keys.Select(k => $"\"{k}\" = @upd_{k}"));
            var sql = $"UPDATE \"{tableName}\" SET {setClauses}{whereClause}";

            var allParms = new Dictionary<string, object?>(whereParms);
            foreach (var kv in updates)
                allParms[$"@upd_{kv.Key}"] = kv.Value;

            LogQuery(sql);
            var sw = Stopwatch.StartNew();
            var affected = await _connectionManager.ExecuteAsync<int>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, allParms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder.UpdateAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "sqlite.update_failed"));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private (string Sql, Dictionary<string, object?> Params) BuildSelectSql(
        int? page = null,
        int? pageSize = null)
    {
        var tableName = GetTableName();
        var selectCols = _selectColumns ?? "*";
        var (whereClause, parms) = BuildWhereSql();
        var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;
        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;

        int? effectiveLimit = page.HasValue ? pageSize : _limit;
        int? effectiveOffset = page.HasValue ? (page.Value - 1) * (pageSize ?? 0) : _offset;

        var limitSql = effectiveLimit.HasValue ? $" LIMIT {effectiveLimit.Value}" : string.Empty;
        var offsetSql = effectiveOffset.HasValue ? $" OFFSET {effectiveOffset.Value}" : string.Empty;

        var sql = $"SELECT {selectCols} FROM \"{tableName}\"{whereClause}{groupBySql}{orderBySql}{limitSql}{offsetSql}";
        return (sql, parms);
    }

    private (string Clause, Dictionary<string, object?> Params) BuildWhereSql()
    {
        if (_wheres.Count == 0) return (string.Empty, new());

        var allParms = new Dictionary<string, object?>();
        var clauses = new List<string>();

        foreach (var (clause, parms) in _wheres)
        {
            clauses.Add(clause);
            foreach (var kv in parms)
                allParms[kv.Key] = kv.Value;
        }

        return ($" WHERE {string.Join(" AND ", clauses)}", allParms);
    }

    private static SqliteCommand BuildCommand(SqliteConnection conn, string sql, Dictionary<string, object?> parms)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var kv in parms)
            cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        return cmd;
    }

    private static T MapReader(SqliteDataReader reader)
    {
        var entity = new T();
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<SQLiteColumnAttribute>() is not null)
            .ToArray();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            var prop = props.FirstOrDefault(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                var mapped = attr?.ColumnName ?? p.Name;
                return string.Equals(mapped, colName, StringComparison.OrdinalIgnoreCase);
            });

            if (prop is null || !prop.CanWrite) continue;
            var raw = reader.IsDBNull(i) ? null : reader.GetValue(i);
            prop.SetValue(entity, ConvertFromDbValue(raw, prop.PropertyType));
        }

        return entity;
    }

    private static object? ConvertFromDbValue(object? value, Type targetType)
    {
        if (value is null || value is DBNull) return null;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying == typeof(bool)) return Convert.ToInt64(value) != 0;
        if (underlying == typeof(int)) return Convert.ToInt32(value);
        if (underlying == typeof(long)) return Convert.ToInt64(value);
        if (underlying == typeof(double)) return Convert.ToDouble(value);
        if (underlying == typeof(float)) return Convert.ToSingle(value);
        if (underlying == typeof(decimal)) return Convert.ToDecimal(value);
        if (underlying == typeof(DateTime) && value is string dtStr) return DateTime.Parse(dtStr);
        if (underlying == typeof(Guid) && value is string guidStr) return Guid.Parse(guidStr);
        if (underlying.IsEnum) return Enum.ToObject(underlying, Convert.ToInt64(value));
        try { return Convert.ChangeType(value, underlying); } catch { return value; }
    }

    private async Task<Result<TResult>> ExecuteAggregateAsync<TResult>(
        string func,
        Expression<Func<T, TResult>> selector,
        CancellationToken ct)
    {
        try
        {
            var col = SQLiteExpressionVisitor.ParseOrderBy(selector);
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT {func}(\"{col}\") FROM \"{tableName}\"{whereClause}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();
            var value = await _connectionManager.ExecuteAsync<TResult>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is null || raw is DBNull) return default!;
                return (TResult)Convert.ChangeType(raw, typeof(TResult))!;
            }, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<TResult>.Success(value);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] QueryBuilder aggregate failed: {ex.Message}", ex);
            return Result<TResult>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    private static string GetTableName()
    {
        var attr = typeof(T).GetCustomAttribute<SQLiteTableAttribute>();
        return attr?.TableName ?? typeof(T).Name;
    }

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        if (elapsedMs >= _slowQueryThresholdMs)
            _logger?.Warning($"[SQLite] Slow query ({elapsedMs}ms): {sql}");
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[SQLite] SQL: {sql}");
    }
}
