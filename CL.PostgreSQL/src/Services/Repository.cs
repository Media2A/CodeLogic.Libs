using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Npgsql;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Generic repository providing CRUD and query operations for entity type T.
/// Uses PostgreSQL double-quoted identifiers and RETURNING * for insert.
/// </summary>
public sealed class Repository<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly int _slowQueryThresholdMs;
    private readonly TransactionScope? _transactionScope;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _cachedProperties = new();

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

    public async Task<Result<T>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var parameters = BuildInsertParameters(entity);
            var columns = string.Join(", ", parameters.Keys.Select(k => $"\"{k}\""));
            var paramNames = string.Join(", ", parameters.Keys.Select((k, i) => $"@p{i}"));
            var sql = $"INSERT INTO \"{schemaName}\".\"{tableName}\" ({columns}) VALUES ({paramNames}) RETURNING *";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var inserted = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                int idx = 0;
                foreach (var kv in parameters)
                {
                    cmd.Parameters.AddWithValue($"@p{idx}", TypeConverter.ToDbValue(kv.Value) ?? DBNull.Value);
                    idx++;
                }
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    return MapReaderToEntity(reader);
                return entity;
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            _logger?.Debug($"[PostgreSQL] Inserted into \"{schemaName}\".\"{tableName}\"");
            return Result<T>.Success(inserted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] InsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "postgresql.insert_failed"));
        }
    }

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

            var (schemaName, tableName) = GetTableInfo();
            _logger?.Debug($"[PostgreSQL] Bulk-inserted {count} records into \"{schemaName}\".\"{tableName}\"");
            return Result<int>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] InsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.bulk_insert_failed"));
        }
    }

    public async Task<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var sql = $"SELECT * FROM \"{schemaName}\".\"{tableName}\" WHERE \"{pkCol}\" = @id LIMIT 1";

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
            _logger?.Error($"[PostgreSQL] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    public async Task<Result<List<T>>> GetByColumnAsync(string column, object value, CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var sql = $"SELECT * FROM \"{schemaName}\".\"{tableName}\" WHERE \"{column}\" = @val";

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
            _logger?.Error($"[PostgreSQL] GetByColumnAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    public async Task<Result<List<T>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var sql = $"SELECT * FROM \"{schemaName}\".\"{tableName}\"";

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
            _logger?.Error($"[PostgreSQL] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    public async Task<Result<PagedResult<T>>> GetPagedAsync(
        int page,
        int pageSize,
        string? orderByColumn = null,
        bool descending = false,
        CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var offset = (page - 1) * pageSize;
            var orderClause = orderByColumn is not null
                ? $" ORDER BY \"{orderByColumn}\" {(descending ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM \"{schemaName}\".\"{tableName}\"";
            var dataSql = $"SELECT * FROM \"{schemaName}\".\"{tableName}\"{orderClause} LIMIT {pageSize} OFFSET {offset}";

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
            _logger?.Error($"[PostgreSQL] GetPagedAsync failed: {ex.Message}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "postgresql.get_failed"));
        }
    }

    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var sql = $"SELECT COUNT(*) FROM \"{schemaName}\".\"{tableName}\"";
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
            _logger?.Error($"[PostgreSQL] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "postgresql.count_failed"));
        }
    }

    public async Task<Result<T>> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var parameters = BuildUpdateParameters(entity);

            var setClauses = string.Join(", ", parameters.Keys.Select((k, i) => $"\"{k}\" = @u{i}"));
            var sql = $"UPDATE \"{schemaName}\".\"{tableName}\" SET {setClauses} WHERE \"{pkCol}\" = @__pk RETURNING *";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var updated = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                int idx = 0;
                foreach (var kv in parameters)
                {
                    cmd.Parameters.AddWithValue($"@u{idx}", TypeConverter.ToDbValue(kv.Value) ?? DBNull.Value);
                    idx++;
                }
                cmd.Parameters.AddWithValue("@__pk", pkProp.GetValue(entity) ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    return MapReaderToEntity(reader);
                return entity;
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            _logger?.Debug($"[PostgreSQL] Updated record in \"{schemaName}\".\"{tableName}\"");
            return Result<T>.Success(updated);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] UpdateAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "postgresql.update_failed"));
        }
    }

    public async Task<Result<bool>> DeleteAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var sql = $"DELETE FROM \"{schemaName}\".\"{tableName}\" WHERE \"{pkCol}\" = @id";

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
            _logger?.Debug($"[PostgreSQL] Deleted record from \"{schemaName}\".\"{tableName}\"");
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] DeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "postgresql.delete_failed"));
        }
    }

    public async Task<Result<int>> IncrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> selector,
        TProperty amount,
        CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var colName = PostgreSQLExpressionVisitor.TranslateSelector(selector);
            var sql = $"UPDATE \"{schemaName}\".\"{tableName}\" SET \"{colName}\" = \"{colName}\" + @amount WHERE \"{pkCol}\" = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@amount", (object?)amount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] IncrementAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.update_failed"));
        }
    }

    public async Task<Result<int>> DecrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> selector,
        TProperty amount,
        CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var pkProp = GetPrimaryKeyProperty();
            var pkCol = GetColumnName(pkProp);
            var colName = PostgreSQLExpressionVisitor.TranslateSelector(selector);
            var sql = $"UPDATE \"{schemaName}\".\"{tableName}\" SET \"{colName}\" = \"{colName}\" - @amount WHERE \"{pkCol}\" = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@amount", (object?)amount ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] DecrementAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.update_failed"));
        }
    }

    public async Task<Result<List<T>>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        try
        {
            var (schemaName, tableName) = GetTableInfo();
            var (whereClause, parameters) = PostgreSQLExpressionVisitor.Translate(predicate);
            var sql = $"SELECT * FROM \"{schemaName}\".\"{tableName}\" WHERE {whereClause}";

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
            _logger?.Error($"[PostgreSQL] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.find_failed"));
        }
    }

    public async Task<Result<int>> RawExecuteAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        try
        {
            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                if (parameters is not null)
                    foreach (var kv in parameters)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] RawExecuteAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "postgresql.raw_execute_failed"));
        }
    }

    public async Task<Result<List<T>>> RawQueryAsync(
        string sql,
        Dictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        try
        {
            LogQuery(sql);
            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                if (parameters is not null)
                    foreach (var kv in parameters)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapReaderToEntity(reader));
                return items;
            }).ConfigureAwait(false);

            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[PostgreSQL] RawQueryAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "postgresql.raw_query_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<TResult> ExecuteAsync<TResult>(Func<NpgsqlConnection, Task<TResult>> action)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId).ConfigureAwait(false);
    }

    private T MapReaderToEntity(NpgsqlDataReader reader)
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
            if (colAttr?.AutoIncrement == true) continue;

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
            if (prop == pkProp) continue;
            var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
            if (colAttr?.AutoIncrement == true) continue;
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

    private static (string Schema, string Table) GetTableInfo()
    {
        var attr = typeof(T).GetCustomAttribute<TableAttribute>();
        var table = !string.IsNullOrEmpty(attr?.Name) ? attr.Name! : typeof(T).Name;
        var schema = !string.IsNullOrEmpty(attr?.Schema) ? attr.Schema! : "public";
        return (schema, table);
    }

    private static PropertyInfo[] GetCachedProperties() =>
        _cachedProperties.GetOrAdd(typeof(T), t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<IgnoreAttribute>() is null)
             .ToArray());

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        if (elapsedMs >= _slowQueryThresholdMs)
            _logger?.Warning($"[PostgreSQL] Slow query ({elapsedMs}ms): {sql}");
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[PostgreSQL] SQL: {sql}");
    }
}
