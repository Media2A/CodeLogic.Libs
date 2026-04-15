using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CodeLogic;
using CodeLogic.Core.Events;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Generic repository providing CRUD and query operations for entity type <typeparamref name="T"/>.
/// Uses reflection with a static per-type cache for performance.
/// </summary>
public sealed class Repository<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly int _slowQueryThresholdMs;
    private readonly TransactionScope? _transactionScope;

    // Static property cache — populated once per entity type
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _cachedProperties = new();

    // Static column-name whitelist cache (case-insensitive) — defends against SQL injection
    // when a column name is passed as a string parameter (e.g., GetByColumnAsync, GetPagedAsync).
    private static readonly ConcurrentDictionary<Type, HashSet<string>> _cachedColumnNames = new();

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a repository using the given connection manager and connection ID.
    /// </summary>
    public Repository(
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

    /// <summary>
    /// Creates a repository that participates in an existing transaction scope.
    /// </summary>
    public Repository(
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

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Inserts a single entity and returns the inserted entity (with auto-generated ID if applicable).</summary>
    public async Task<Result<T>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var parameters = BuildInsertParameters(entity);
            var columns = string.Join(", ", parameters.Keys.Select(k => $"`{k}`"));
            var paramNames = string.Join(", ", parameters.Keys.Select(k => $"@{k}"));
            var sql = $"INSERT INTO `{tableName}` ({columns}) VALUES ({paramNames}); SELECT LAST_INSERT_ID();";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue($"@{kv.Key}", TypeConverter.ToDbValue(kv.Value) ?? DBNull.Value);
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            // Set auto-increment PK if applicable
            var pkProp = TryGetPrimaryKeyProperty();
            if (pkProp is not null && pkProp.GetCustomAttribute<ColumnAttribute>()?.AutoIncrement == true && lastId is not null)
            {
                var converted = Convert.ChangeType(lastId, pkProp.PropertyType);
                pkProp.SetValue(entity, converted);
            }

            _logger?.Debug($"[MySQL2] Inserted into `{tableName}`");
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] InsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mysql.insert_failed"));
        }
    }

    /// <summary>Bulk-inserts a collection of entities and returns the count inserted.</summary>
    public async Task<Result<int>> InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var count = 0;
            foreach (var entity in list)
            {
                var result = await InsertAsync(entity, ct).ConfigureAwait(false);
                if (result.IsFailure) return Result<int>.Failure(result.Error!);
                count++;
            }

            var tableName = GetTableName();
            _logger?.Debug($"[MySQL2] Bulk-inserted {count} records into `{tableName}`");
            return Result<int>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] InsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.bulk_insert_failed"));
        }
    }

    /// <summary>Retrieves an entity by its primary key value.</summary>
    public async Task<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var sql = $"SELECT * FROM `{tableName}` WHERE `{pkCol}` = @id LIMIT 1";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false)
                    ? MapReaderToEntity(reader)
                    : null;
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            return Result<T?>.Success(result);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities where the specified column equals the given value.</summary>
    public async Task<Result<List<T>>> GetByColumnAsync(string column, object value, CancellationToken ct = default)
    {
        try
        {
            EnsureValidColumn(column);
            var tableName = GetTableName();
            var sql = $"SELECT * FROM `{tableName}` WHERE `{column}` = @val";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@val", TypeConverter.ToDbValue(value) ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapReaderToEntity(reader));
                return items;
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetByColumnAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities in the table.</summary>
    public async Task<Result<List<T>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var sql = $"SELECT * FROM `{tableName}`";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapReaderToEntity(reader));
                return items;
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Retrieves a paged result set.</summary>
    public async Task<Result<PagedResult<T>>> GetPagedAsync(
        int page,
        int pageSize,
        string? orderByColumn = null,
        bool descending = false,
        CancellationToken ct = default)
    {
        try
        {
            if (orderByColumn is not null) EnsureValidColumn(orderByColumn);
            var tableName = GetTableName();
            var offset = (page - 1) * pageSize;
            var orderClause = orderByColumn is not null
                ? $" ORDER BY `{orderByColumn}` {(descending ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM `{tableName}`";
            var dataSql = $"SELECT * FROM `{tableName}`{orderClause} LIMIT {pageSize} OFFSET {offset}";

            LogQuery(dataSql);
            var sw = Stopwatch.StartNew();

            var (items, total) = await ExecuteAsync(async conn =>
            {
                await using var countCmd = conn.CreateCommand();
                if (_transactionScope is not null) countCmd.Transaction = _transactionScope.Transaction;
                countCmd.CommandText = countSql;
                var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = dataSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var entities = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    entities.Add(MapReaderToEntity(reader));

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
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] GetPagedAsync failed: {ex.Message}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Returns the total row count for the table.</summary>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var sql = $"SELECT COUNT(*) FROM `{tableName}`";
            LogQuery(sql);

            var count = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }).ConfigureAwait(false);

            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "mysql.count_failed"));
        }
    }

    /// <summary>Updates an existing entity and returns the updated entity.</summary>
    public async Task<Result<T>> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var parameters = BuildUpdateParameters(entity);

            var setClauses = string.Join(", ", parameters.Keys.Select(k => $"`{k}` = @{k}"));
            var sql = $"UPDATE `{tableName}` SET {setClauses} WHERE `{pkCol}` = @__pk";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue($"@{kv.Key}", TypeConverter.ToDbValue(kv.Value) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@__pk", pkProp.GetValue(entity));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            _logger?.Debug($"[MySQL2] Updated record in `{tableName}`");
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] UpdateAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    /// <summary>Deletes an entity by its primary key value.</summary>
    public async Task<Result<bool>> DeleteAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var sql = $"DELETE FROM `{tableName}` WHERE `{pkCol}` = @id";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            _logger?.Debug($"[MySQL2] Deleted record from `{tableName}`");
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] DeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.delete_failed"));
        }
    }

    /// <summary>Atomically increments a numeric property by the given amount.</summary>
    public async Task<Result<int>> IncrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty amount,
        CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var colName = MySqlExpressionVisitor.TranslateSelector(propertySelector);
            var sql = $"UPDATE `{tableName}` SET `{colName}` = `{colName}` + @amount WHERE `{pkCol}` = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@amount", amount);
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] IncrementAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    /// <summary>Atomically decrements a numeric property by the given amount.</summary>
    public async Task<Result<int>> DecrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty amount,
        CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var colName = MySqlExpressionVisitor.TranslateSelector(propertySelector);
            var sql = $"UPDATE `{tableName}` SET `{colName}` = `{colName}` - @amount WHERE `{pkCol}` = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@amount", amount);
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] DecrementAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    /// <summary>Returns all entities matching the given LINQ predicate.</summary>
    public async Task<Result<List<T>>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        try
        {
            var tableName = GetTableName();
            var (whereClause, parameters) = MySqlExpressionVisitor.Translate(predicate);
            var sql = $"SELECT * FROM `{tableName}` WHERE {whereClause}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, TypeConverter.ToDbValue(kv.Value) ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapReaderToEntity(reader));
                return items;
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mysql.find_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<TResult> ExecuteAsync<TResult>(Func<MySqlConnection, Task<TResult>> action)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId).ConfigureAwait(false);
    }

    private T MapReaderToEntity(MySqlDataReader reader)
    {
        var entity = new T();
        var props = GetCachedProperties();

        for (int i = 0; i < reader.FieldCount; i++)
        {
            var colName = reader.GetName(i);
            var prop = props.FirstOrDefault(p =>
            {
                var attr = p.GetCustomAttribute<ColumnAttribute>();
                var mappedName = !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : p.Name;
                return string.Equals(mappedName, colName, StringComparison.OrdinalIgnoreCase);
            });

            if (prop is null || !prop.CanWrite) continue;

            var rawValue = reader.IsDBNull(i) ? null : reader.GetValue(i);
            var converted = TypeConverter.FromDbValue(rawValue, prop.PropertyType);
            prop.SetValue(entity, converted);
        }

        return entity;
    }

    private Dictionary<string, object?> BuildInsertParameters(T entity)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in GetCachedProperties())
        {
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            if (colAttr?.AutoIncrement == true) continue; // skip AI columns on insert

            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;
            result[colName] = prop.GetValue(entity);
        }
        return result;
    }

    private Dictionary<string, object?> BuildUpdateParameters(T entity)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var pkProp = GetPrimaryKeyProperty();
        foreach (var prop in GetCachedProperties())
        {
            if (prop == pkProp) continue; // exclude PK from SET clause
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;
            result[colName] = prop.GetValue(entity);
        }
        return result;
    }

    private PropertyInfo GetPrimaryKeyProperty()
    {
        var pk = TryGetPrimaryKeyProperty();
        if (pk is null)
            throw new InvalidOperationException(
                $"Entity '{typeof(T).Name}' has no property marked with [Column(Primary = true)].");
        return pk;
    }

    private PropertyInfo? TryGetPrimaryKeyProperty() =>
        GetCachedProperties()
            .FirstOrDefault(p => p.GetCustomAttribute<ColumnAttribute>()?.Primary == true);

    private static string GetColumnName(PropertyInfo prop)
    {
        var attr = prop.GetCustomAttribute<ColumnAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : prop.Name;
    }

    private static string GetTableName()
    {
        var attr = typeof(T).GetCustomAttribute<TableAttribute>();
        return !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : typeof(T).Name;
    }

    private static PropertyInfo[] GetCachedProperties() =>
        _cachedProperties.GetOrAdd(typeof(T), t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null)
             .ToArray());

    /// <summary>
    /// Throws ArgumentException if <paramref name="column"/> is not a known column on <typeparamref name="T"/>.
    /// Used by APIs that accept a column name as a string (e.g., GetByColumnAsync, GetPagedAsync)
    /// to prevent SQL injection via the column-name parameter.
    /// </summary>
    private static void EnsureValidColumn(string column)
    {
        if (string.IsNullOrEmpty(column))
            throw new ArgumentException("Column name cannot be null or empty.", nameof(column));

        var allowed = _cachedColumnNames.GetOrAdd(typeof(T), _ =>
            new HashSet<string>(GetCachedProperties().Select(GetColumnName), StringComparer.OrdinalIgnoreCase));

        if (!allowed.Contains(column))
            throw new ArgumentException(
                $"Column '{column}' is not defined on entity '{typeof(T).Name}'.", nameof(column));
    }

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        if (elapsedMs >= _slowQueryThresholdMs)
            _logger?.Warning($"[MySQL2] Slow query ({elapsedMs}ms): {sql}");
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MySQL2] SQL: {sql}");
    }
}
