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
    private readonly string _connectionId;
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
    /// <param name="connectionId">Named SQLite connection to use (as configured in <c>config.sqlite.json</c>).</param>
    /// <param name="slowQueryThresholdMs">Queries exceeding this threshold in milliseconds are logged as warnings.</param>
    public QueryBuilder(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        string connectionId = "Default",
        int slowQueryThresholdMs = 500)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _connectionId = connectionId;
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
        // Re-key parameters to avoid collisions. Longer parameter names are replaced
        // first so @p1 can't clobber a substring of @p10/@p11 (predicates with 11+ params).
        var rekeyed = new Dictionary<string, object?>();
        foreach (var kv in parms.OrderByDescending(k => k.Key.Length))
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
    /// <remarks>
    /// Both a single-member selector (<c>o =&gt; o.Total</c>) and an anonymous-type selector
    /// (<c>o =&gt; new { o.Id, o.Total }</c>) resolve each projected member back to the source
    /// entity's mapped <see cref="SQLiteColumnAttribute.ColumnName"/>, and every emitted name is
    /// double-quoted, so renamed columns and reserved words are both handled.
    /// </remarks>
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
    /// <remarks>
    /// GROUP BY reaches the row-returning terminals (<see cref="ToListAsync"/> and
    /// <see cref="ToPagedListAsync"/>) and <see cref="CountAsync"/>, which then counts groups
    /// rather than rows. The typed aggregates (<see cref="SumAsync{TResult}"/>,
    /// <see cref="MinAsync{TResult}"/>, <see cref="MaxAsync{TResult}"/>) throw
    /// <see cref="NotSupportedException"/> on a grouped builder, because a per-group aggregate
    /// has no single scalar answer — read the grouped rows with <see cref="ToListAsync"/> instead.
    /// </remarks>
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
            }, _connectionId, ct).ConfigureAwait(false);

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
    /// The LIMIT applies to this call only: the builder is left untouched and can be reused.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the first entity or <c>null</c>.</returns>
    public async Task<Result<T?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        try
        {
            var (sql, parms) = BuildSelectSql(limitOverride: 1);
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await _connectionManager.ExecuteAsync<T?>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapReader(reader) : null;
            }, _connectionId, ct).ConfigureAwait(false);

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
        if (page < 1 || pageSize < 1)
            return Result<PagedResult<T>>.Failure(Error.Validation(
                "sqlite.paging", "page and pageSize must both be >= 1."));
        try
        {
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;

            // With a GROUP BY, COUNT(*) would return the first group's row count; the total a
            // caller is paging through is the number of groups, so the grouped query is wrapped.
            var countSql = groupBySql.Length == 0
                ? $"SELECT COUNT(*) FROM \"{tableName}\"{whereClause}"
                : $"SELECT COUNT(*) FROM (SELECT 1 FROM \"{tableName}\"{whereClause}{groupBySql})";
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
            }, _connectionId, ct).ConfigureAwait(false);

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
    /// <remarks>
    /// On a builder carrying <see cref="GroupBy{TKey}"/> the count is the <i>number of groups</i>
    /// — the grouped query is wrapped in <c>SELECT COUNT(*) FROM (…)</c> — matching the
    /// <c>TotalItems</c> reported by <see cref="ToPagedListAsync"/> for the same builder.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the number of matching rows, or groups.</returns>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;
            var sql = groupBySql.Length == 0
                ? $"SELECT COUNT(*) FROM \"{tableName}\"{whereClause}"
                : $"SELECT COUNT(*) FROM (SELECT 1 FROM \"{tableName}\"{whereClause}{groupBySql})";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();
            var count = await _connectionManager.ExecuteAsync<long>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null ? 0L : Convert.ToInt64(result);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
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
    /// <exception cref="NotSupportedException">The builder carries a <see cref="GroupBy{TKey}"/>.</exception>
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
    /// <exception cref="NotSupportedException">The builder carries a <see cref="GroupBy{TKey}"/>.</exception>
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
    /// <exception cref="NotSupportedException">The builder carries a <see cref="GroupBy{TKey}"/>.</exception>
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
            }, _connectionId, ct).ConfigureAwait(false);

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
            // Positional parameter names: a column name is an arbitrary quoted identifier and
            // need not be a legal parameter token (the WHERE parameters are named @qb_n, so the
            // two sets cannot collide).
            var allParms = new Dictionary<string, object?>(whereParms);
            var setClauses = new List<string>();
            foreach (var kv in updates)
            {
                // Resolve through the entity's mapped columns rather than trusting the key.
                // A key is an arbitrary caller-supplied string: interpolated straight into the
                // SET list it can close its own quoted identifier and append assignments the
                // caller never asked for. Resolving also gives us the property type, so the
                // value goes through the same converter the repository writes with.
                var column = RequireColumn(kv.Key);
                var paramName = $"@p{setClauses.Count}";
                setClauses.Add($"\"{column.ColumnName}\" = {paramName}");
                allParms[paramName] = SQLiteValueConverter.ToDbValue(kv.Value, column.Property.PropertyType);
            }

            var sql = $"UPDATE \"{tableName}\" SET {string.Join(", ", setClauses)}{whereClause}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();
            var affected = await _connectionManager.ExecuteAsync<int>(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, allParms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

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
        int? pageSize = null,
        int? limitOverride = null)
    {
        var tableName = GetTableName();
        var selectCols = _selectColumns ?? "*";
        var (whereClause, parms) = BuildWhereSql();
        var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;
        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;

        int? effectiveLimit = page.HasValue ? pageSize : (limitOverride ?? _limit);
        int? effectiveOffset = page.HasValue ? (page.Value - 1) * (pageSize ?? 0) : _offset;

        // SQLite's grammar is LIMIT expr [OFFSET expr] — a bare OFFSET is a syntax error, so an
        // offset with no limit is emitted as the idiomatic "no upper bound" form LIMIT -1.
        var limitSql = effectiveLimit.HasValue
            ? $" LIMIT {effectiveLimit.Value}"
            : effectiveOffset.HasValue ? " LIMIT -1" : string.Empty;
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
            prop.SetValue(entity, SQLiteValueConverter.FromDbValue(raw, prop.PropertyType));
        }

        return entity;
    }

    private async Task<Result<TResult>> ExecuteAggregateAsync<TResult>(
        string func,
        Expression<Func<T, TResult>> selector,
        CancellationToken ct)
    {
        // A grouped aggregate has one value per group, which will not fit in a single scalar
        // Result. Silently dropping the grouping would answer a question nobody asked.
        if (_groupBys.Count > 0)
            throw new NotSupportedException(
                $"{func} cannot be combined with GroupBy: a grouped aggregate yields one value " +
                "per group, not a single scalar. Use ToListAsync (or RawQueryAsync) to read the " +
                "grouped rows instead.");

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
            }, _connectionId, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Resolves a caller-supplied key to a mapped column on <typeparamref name="T"/>, by
    /// column name or by property name. Throws when the key names nothing, so an unmapped
    /// or hostile key is rejected before it can reach the SQL text.
    /// </summary>
    private static (string ColumnName, PropertyInfo Property) RequireColumn(string key)
    {
        foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
            if (attr is null) continue;
            var columnName = string.IsNullOrWhiteSpace(attr.ColumnName) ? prop.Name : attr.ColumnName;
            if (string.Equals(columnName, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                return (columnName, prop);
        }

        throw new ArgumentException(
            $"'{key}' is not a mapped column on '{typeof(T).Name}'.", nameof(key));
    }

    private static string GetTableName()
    {
        var attr = typeof(T).GetCustomAttribute<SQLiteTableAttribute>();
        return attr?.TableName ?? typeof(T).Name;
    }

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        if (elapsedMs < _slowQueryThresholdMs) return;

        _logger?.Warning($"[SQLite] {string.Format(SQLiteObservability.Strings.SlowQueryDetected, elapsedMs, sql)}");
        SQLiteObservability.RecordSlowQuery(GetTableName(), sql, elapsedMs);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[SQLite] SQL: {sql}");
    }
}
