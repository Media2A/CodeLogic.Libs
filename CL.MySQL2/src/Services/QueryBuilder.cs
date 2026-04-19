using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Fluent query builder for entity type <typeparamref name="T"/>.
/// Chains WHERE, ORDER BY, JOIN, GROUP BY, LIMIT/OFFSET clauses and executes
/// terminal operations returning <see cref="Result{T}"/>.
/// </summary>
public sealed class QueryBuilder<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private string _connectionId;
    private readonly TransactionScope? _transactionScope;
    private readonly int _slowQueryThresholdMs;

    // Builder state
    private readonly List<(string Clause, Dictionary<string, object?> Params)> _wheres = [];
    private readonly List<string> _orderBys = [];
    private readonly List<string> _joins = [];
    private readonly List<string> _groupBys = [];
    private int? _limit;
    private int? _offset;
    private string? _selectColumns;
    private int _paramCounter;
    private TimeSpan? _cacheTtl;

    // ── Constructors ──────────────────────────────────────────────────────────

    public QueryBuilder(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        string connectionId = "Default",
        int slowQueryThresholdMs = 1000)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _connectionId = connectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    public QueryBuilder(
        ConnectionManager connectionManager,
        ILogger? logger,
        TransactionScope transactionScope,
        int slowQueryThresholdMs = 1000)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _transactionScope = transactionScope ?? throw new ArgumentNullException(nameof(transactionScope));
        _connectionId = transactionScope.ConnectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    // ── Fluent chain methods ──────────────────────────────────────────────────

    public QueryBuilder<T> Where(Expression<Func<T, bool>> predicate)
    {
        var (clause, parms) = MySqlExpressionVisitor.Translate(predicate);
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

    public QueryBuilder<T> OrderBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = MySqlExpressionVisitor.TranslateSelector(keySelector);
        _orderBys.Add($"`{col}` ASC");
        return this;
    }

    public QueryBuilder<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = MySqlExpressionVisitor.TranslateSelector(keySelector);
        _orderBys.Add($"`{col}` DESC");
        return this;
    }

    public QueryBuilder<T> Limit(int count) { _limit = count; return this; }
    public QueryBuilder<T> Offset(int count) { _offset = count; return this; }
    public QueryBuilder<T> Take(int count) => Limit(count);
    public QueryBuilder<T> Skip(int count) => Offset(count);

    public QueryBuilder<T> Join(string table, string condition, JoinType type = JoinType.Inner)
    {
        var keyword = type switch
        {
            JoinType.Left  => "LEFT JOIN",
            JoinType.Right => "RIGHT JOIN",
            JoinType.Cross => "CROSS JOIN",
            _              => "INNER JOIN"
        };
        _joins.Add($"{keyword} `{table}` ON {condition}");
        return this;
    }

    public QueryBuilder<T> Select(Expression<Func<T, object?>> selector)
    {
        if (selector.Body is NewExpression newExpr)
        {
            var cols = newExpr.Members!
                .Select(m => $"`{GetColumnName(m)}`");
            _selectColumns = string.Join(", ", cols);
        }
        else
        {
            var col = MySqlExpressionVisitor.TranslateSelector(selector);
            _selectColumns = $"`{col}`";
        }
        return this;
    }

    public QueryBuilder<T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var col = MySqlExpressionVisitor.TranslateSelector(keySelector);
        _groupBys.Add($"`{col}`");
        return this;
    }

    public QueryBuilder<T> WithConnection(string connectionId)
    {
        _connectionId = connectionId;
        return this;
    }

    /// <summary>
    /// Enable result caching for this query. Cached results are automatically invalidated
    /// when any mutation (INSERT/UPDATE/DELETE) occurs on the same table.
    /// Has no effect inside a transaction scope (reads in transactions see uncommitted writes).
    /// </summary>
    public QueryBuilder<T> WithCache(TimeSpan ttl)
    {
        _cacheTtl = ttl;
        return this;
    }

    private bool ShouldCache => _cacheTtl is not null && _transactionScope is null;

    // ── Terminal methods ──────────────────────────────────────────────────────

    public async Task<Result<List<T>>> ToListAsync(CancellationToken ct = default)
    {
        try
        {
            var (sql, parms) = BuildSelectSql();

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, sql, parms);
                return await QueryCache.GetOrSetAsync(cacheKey, GetTableName(), () => ExecuteToList(sql, parms, ct), _cacheTtl!.Value).ConfigureAwait(false);
            }

            return await ExecuteToList(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.ToListAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    private async Task<Result<List<T>>> ExecuteToList(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var sw = Stopwatch.StartNew();

        var list = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            var items = new List<T>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                items.Add(map(reader));
            return items;
        }).ConfigureAwait(false);

        sw.Stop();
        LogSlowQuery(sql, sw.ElapsedMilliseconds);
        return Result<List<T>>.Success(list);
    }

    public async Task<Result<T?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        try
        {
            Limit(1);
            var (sql, parms) = BuildSelectSql();

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, sql, parms);
                return await QueryCache.GetOrSetAsync(cacheKey, GetTableName(), () => ExecuteFirstOrDefault(sql, parms, ct), _cacheTtl!.Value).ConfigureAwait(false);
            }

            return await ExecuteFirstOrDefault(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.FirstOrDefaultAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    private async Task<Result<T?>> ExecuteFirstOrDefault(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var sw = Stopwatch.StartNew();

        var result = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            return map(reader);
        }).ConfigureAwait(false);

        sw.Stop();
        LogSlowQuery(sql, sw.ElapsedMilliseconds);
        return Result<T?>.Success(result);
    }

    public async Task<Result<PagedResult<T>>> ToPagedListAsync(
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        try
        {
            var tblName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
            var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM `{tblName}`{joinSql}{whereClause}{groupBySql}";
            var dataSql = BuildSelectSql(page, pageSize).Sql;

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, dataSql, parms);
                return await QueryCache.GetOrSetAsync(cacheKey, tblName,
                    () => ExecutePagedList(countSql, page, pageSize, parms, ct), _cacheTtl!.Value).ConfigureAwait(false);
            }

            return await ExecutePagedList(countSql, page, pageSize, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.ToPagedListAsync failed: {ex.Message}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    private async Task<Result<PagedResult<T>>> ExecutePagedList(
        string countSql, int page, int pageSize, Dictionary<string, object?> parms, CancellationToken ct)
    {
        var dataSql = BuildSelectSql(page, pageSize).Sql;
        LogQuery(dataSql);

        var sw = Stopwatch.StartNew();
        var (items, total) = await ExecuteAsync(async conn =>
        {
            await using var countCmd = BuildCommand(conn, countSql, parms);
            var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

            var (dSql, dParms) = BuildSelectSql(page, pageSize);
            await using var cmd = BuildCommand(conn, dSql, dParms);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
            var entities = new List<T>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                entities.Add(map(reader));

            return (entities, totalCount);
        }).ConfigureAwait(false);

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

    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var tblName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
            var sql = $"SELECT COUNT(*) FROM `{tblName}`{joinSql}{whereClause}";

            if (ShouldCache)
            {
                var cacheKey = QueryCache.BuildCacheKey(_connectionId, sql, parms);
                return await QueryCache.GetOrSetAsync(cacheKey, tblName, () => ExecuteCount(sql, parms, ct), _cacheTtl!.Value).ConfigureAwait(false);
            }

            return await ExecuteCount(sql, parms, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    private async Task<Result<long>> ExecuteCount(string sql, Dictionary<string, object?> parms, CancellationToken ct)
    {
        LogQuery(sql);
        var count = await ExecuteAsync(async conn =>
        {
            await using var cmd = BuildCommand(conn, sql, parms);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }).ConfigureAwait(false);

        return Result<long>.Success(count);
    }

    public Task<Result<TResult>> MaxAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("MAX", selector, ct);

    public Task<Result<TResult>> MinAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("MIN", selector, ct);

    public Task<Result<TResult>> SumAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
        => ExecuteAggregateAsync<TResult>("SUM", selector, ct);

    public async Task<Result<double>> AverageAsync<TResult>(
        Expression<Func<T, TResult>> selector,
        CancellationToken ct = default)
    {
        try
        {
            var col = MySqlExpressionVisitor.TranslateSelector(selector);
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT AVG(`{col}`) FROM `{tableName}`{whereClause}";

            LogQuery(sql);
            var value = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return raw is null || raw is DBNull ? 0.0 : Convert.ToDouble(raw);
            }).ConfigureAwait(false);

            return Result<double>.Success(value);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.AverageAsync failed: {ex.Message}", ex);
            return Result<double>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    public async Task<Result<int>> DeleteAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"DELETE FROM `{tableName}`{whereClause}";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            QueryCache.Invalidate(GetTableName());
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.DeleteAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.delete_failed"));
        }
    }

    public async Task<Result<int>> UpdateAsync(Dictionary<string, object?> updates, CancellationToken ct = default)
    {
        try
        {
            foreach (var key in updates.Keys) EnsureValidColumn(key);
            var tableName = GetTableName();
            var (whereClause, whereParms) = BuildWhereSql();
            var setClauses = string.Join(", ", updates.Keys.Select(k => $"`{k}` = @upd_{k}"));
            var sql = $"UPDATE `{tableName}` SET {setClauses}{whereClause}";

            var allParms = new Dictionary<string, object?>(whereParms);
            foreach (var kv in updates)
                allParms[$"@upd_{kv.Key}"] = TypeConverter.ToDbValue(kv.Value);

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, allParms);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            QueryCache.Invalidate(GetTableName());
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder.UpdateAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private (string Sql, Dictionary<string, object?> Params) BuildSelectSql(
        int? page = null,
        int? pageSize = null)
    {
        var tableName = GetTableName();
        var selectCols = _selectColumns ?? "*";
        var joinSql = _joins.Count > 0 ? " " + string.Join(" ", _joins) : string.Empty;
        var (whereClause, parms) = BuildWhereSql();
        var groupBySql = _groupBys.Count > 0 ? $" GROUP BY {string.Join(", ", _groupBys)}" : string.Empty;
        var orderBySql = _orderBys.Count > 0 ? $" ORDER BY {string.Join(", ", _orderBys)}" : string.Empty;

        int? effectiveLimit = page.HasValue ? pageSize : _limit;
        int? effectiveOffset = page.HasValue ? (page.Value - 1) * (pageSize ?? 0) : _offset;

        var limitSql = effectiveLimit.HasValue ? $" LIMIT {effectiveLimit.Value}" : string.Empty;
        var offsetSql = effectiveOffset.HasValue ? $" OFFSET {effectiveOffset.Value}" : string.Empty;

        var sql = $"SELECT {selectCols} FROM `{tableName}`{joinSql}{whereClause}{groupBySql}{orderBySql}{limitSql}{offsetSql}";
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

    private async Task<TResult> ExecuteAsync<TResult>(Func<MySqlConnection, Task<TResult>> action)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId).ConfigureAwait(false);
    }

    private MySqlCommand BuildCommand(MySqlConnection conn, string sql, Dictionary<string, object?> parms)
    {
        var cmd = conn.CreateCommand();
        if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
        cmd.CommandText = sql;
        foreach (var kv in parms)
            cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        return cmd;
    }

    private async Task<Result<TResult>> ExecuteAggregateAsync<TResult>(
        string func,
        Expression<Func<T, TResult>> selector,
        CancellationToken ct)
    {
        try
        {
            var col = MySqlExpressionVisitor.TranslateSelector(selector);
            var tableName = GetTableName();
            var (whereClause, parms) = BuildWhereSql();
            var sql = $"SELECT {func}(`{col}`) FROM `{tableName}`{whereClause}";

            LogQuery(sql);
            var value = await ExecuteAsync(async conn =>
            {
                await using var cmd = BuildCommand(conn, sql, parms);
                var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (raw is null || raw is DBNull) return default!;
                return (TResult)Convert.ChangeType(raw, typeof(TResult))!;
            }).ConfigureAwait(false);

            return Result<TResult>.Success(value);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] QueryBuilder aggregate failed: {ex.Message}", ex);
            return Result<TResult>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    private static string GetTableName() => EntityMetadata<T>.TableName;

    private static string GetColumnName(MemberInfo member)
    {
        var attr = member.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : member.Name;
    }

    private static void EnsureValidColumn(string column) =>
        _ = EntityMetadata<T>.RequireColumn(column);

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        if (elapsedMs >= _slowQueryThresholdMs)
            _logger?.Warning($"[MySQL2] [{_connectionId}] Slow query ({elapsedMs}ms): {sql}");
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MySQL2] SQL: {sql}");
    }
}
