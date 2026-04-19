using System.Collections.Concurrent;
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
/// Generic repository providing CRUD and query operations for entity type <typeparamref name="T"/>.
/// Uses reflection with a static per-type cache for performance.
/// </summary>
public sealed class Repository<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly int _slowQueryThresholdMs;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _cachedProperties = new();

    // ── Table metadata (cached per type) ─────────────────────────────────────

    private static readonly string _tableName = GetTableName();
    private static readonly string[] _pkColumnNames = GetPrimaryKeyColumnNames();

    /// <summary>
    /// Initializes a new instance of <see cref="Repository{T}"/>.
    /// </summary>
    /// <param name="connectionManager">The connection manager used to obtain and release database connections.</param>
    /// <param name="logger">Optional logger for debug and warning output.</param>
    /// <param name="connectionId">Named SQLite connection to use (as configured in <c>config.sqlite.json</c>).</param>
    /// <param name="slowQueryThresholdMs">Queries exceeding this threshold in milliseconds are logged as warnings.</param>
    public Repository(
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

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a new entity into the table. If the entity has an auto-increment primary key,
    /// the generated row ID is written back to the entity's PK property.
    /// </summary>
    /// <param name="entity">The entity to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the SQLite row ID of the inserted row.</returns>
    public async Task<Result<long>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var props = GetCachedProperties();
            var cols = new List<string>();
            var paramNames = new List<string>();
            var values = new Dictionary<string, object?>();

            foreach (var prop in props)
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                if (colAttr?.IsAutoIncrement == true) continue;
                var colName = colAttr?.ColumnName ?? prop.Name;
                cols.Add($"\"{colName}\"");
                paramNames.Add($"@{colName}");
                values[$"@{colName}"] = ConvertToDbValue(prop.GetValue(entity), prop.PropertyType);
            }

            var sql = $"INSERT INTO \"{_tableName}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", paramNames)}); SELECT last_insert_rowid();";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var rowId = await _connectionManager.ExecuteAsync<long>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null ? 0L : Convert.ToInt64(result);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            // Assign auto-increment PK
            var pkProp = TryGetAutoIncrementPk();
            if (pkProp is not null)
            {
                var converted = Convert.ChangeType(rowId, pkProp.PropertyType);
                pkProp.SetValue(entity, converted);
            }

            _logger?.Debug($"[SQLite] Inserted into '{_tableName}', rowid={rowId}");
            return Result<long>.Success(rowId);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] InsertAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "sqlite.insert_failed"));
        }
    }

    /// <summary>
    /// Inserts the entity or replaces an existing row with the same primary key (INSERT OR REPLACE).
    /// </summary>
    /// <param name="entity">The entity to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> UpsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var props = GetCachedProperties();
            var cols = new List<string>();
            var paramNames = new List<string>();
            var values = new Dictionary<string, object?>();

            foreach (var prop in props)
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                var colName = colAttr?.ColumnName ?? prop.Name;
                cols.Add($"\"{colName}\"");
                paramNames.Add($"@{colName}");
                values[$"@{colName}"] = ConvertToDbValue(prop.GetValue(entity), prop.PropertyType);
            }

            var sql = $"INSERT OR REPLACE INTO \"{_tableName}\" ({string.Join(", ", cols)}) VALUES ({string.Join(", ", paramNames)});";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] UpsertAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.upsert_failed"));
        }
    }

    /// <summary>
    /// Retrieves a single entity by its primary key value.
    /// </summary>
    /// <param name="id">The primary key value to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the matching entity, or <c>null</c> if not found.</returns>
    public async Task<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count == 0)
                return Result<T?>.Failure(Error.Validation("sqlite.no_pk", $"Entity '{typeof(T).Name}' has no primary key."));

            var pkCol = pkCols[0].ColumnName;
            var sql = $"SELECT * FROM \"{_tableName}\" WHERE \"{pkCol}\" = @id LIMIT 1;";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var entity = await _connectionManager.ExecuteAsync<T?>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapFromReader(reader) : null;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<T?>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Retrieves a single entity by a composite primary key. The number and order of
    /// <paramref name="keys"/> must match the entity's primary key columns.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="keys">The ordered set of primary key values.</param>
    /// <returns>A <see cref="Result{T}"/> containing the matching entity, or <c>null</c> if not found.</returns>
    public async Task<Result<T?>> GetByKeysAsync(CancellationToken ct, params object[] keys)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count != keys.Length)
                return Result<T?>.Failure(Error.Validation("sqlite.pk_mismatch",
                    $"Expected {pkCols.Count} key(s), got {keys.Length}."));

            var (clause, parameters) = BuildPrimaryKeyWhere(pkCols, keys);
            var sql = $"SELECT * FROM \"{_tableName}\" WHERE {clause} LIMIT 1;";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var entity = await _connectionManager.ExecuteAsync<T?>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapFromReader(reader) : null;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<T?>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetByKeysAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Retrieves all rows from the table up to the specified limit.
    /// </summary>
    /// <param name="limit">Maximum number of rows to return (defaults to 1000).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of entities.</returns>
    public async Task<Result<List<T>>> GetAllAsync(int limit = 1000, CancellationToken ct = default)
    {
        try
        {
            var sql = $"SELECT * FROM \"{_tableName}\" LIMIT {limit};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapFromReader(reader));
                return items;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Retrieves all rows matching the given predicate expression.
    /// </summary>
    /// <param name="predicate">A LINQ predicate expression translated to a SQL WHERE clause.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of matching entities.</returns>
    public async Task<Result<List<T>>> FindAsync(
        Expression<Func<T, bool>> predicate,
        CancellationToken ct = default)
    {
        try
        {
            var (whereClause, parameters) = SQLiteExpressionVisitor.Parse(predicate);
            var sql = $"SELECT * FROM \"{_tableName}\" WHERE {whereClause};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapFromReader(reader));
                return items;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.find_failed"));
        }
    }

    /// <summary>
    /// Updates an existing row by its primary key with the current property values of the entity.
    /// </summary>
    /// <param name="entity">The entity containing updated values. The primary key must be set.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count == 0)
                return Result.Failure(Error.Validation("sqlite.no_pk", $"Entity '{typeof(T).Name}' has no primary key."));

            var props = GetCachedProperties();
            var setClauses = new List<string>();
            var values = new Dictionary<string, object?>();
            var pkColNames = pkCols.Select(p => p.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in props)
            {
                var colAttr = prop.GetCustomAttribute<SQLiteColumnAttribute>();
                var colName = colAttr?.ColumnName ?? prop.Name;
                if (pkColNames.Contains(colName)) continue;
                setClauses.Add($"\"{colName}\" = @set_{colName}");
                values[$"@set_{colName}"] = ConvertToDbValue(prop.GetValue(entity), prop.PropertyType);
            }

            var pkValues = pkCols.Select(pk => pk.Property.GetValue(entity)).ToArray();
            var (whereClause, whereParams) = BuildPrimaryKeyWhere(pkCols, pkValues!);
            foreach (var kv in whereParams) values[kv.Key] = kv.Value;

            var sql = $"UPDATE \"{_tableName}\" SET {string.Join(", ", setClauses)} WHERE {whereClause};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in values)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] UpdateAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.update_failed"));
        }
    }

    /// <summary>
    /// Deletes the row with the given single primary key value.
    /// </summary>
    /// <param name="id">The primary key value identifying the row to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> DeleteAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count == 0)
                return Result.Failure(Error.Validation("sqlite.no_pk", $"Entity '{typeof(T).Name}' has no primary key."));

            var pkCol = pkCols[0].ColumnName;
            var sql = $"DELETE FROM \"{_tableName}\" WHERE \"{pkCol}\" = @id;";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DeleteAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.delete_failed"));
        }
    }

    /// <summary>
    /// Deletes the row identified by a composite primary key. The number and order of
    /// <paramref name="keys"/> must match the entity's primary key columns.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="keys">The ordered set of primary key values.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public async Task<Result> DeleteByKeysAsync(CancellationToken ct, params object[] keys)
    {
        try
        {
            var pkCols = GetPrimaryKeyColumns();
            if (pkCols.Count != keys.Length)
                return Result.Failure(Error.Validation("sqlite.pk_mismatch",
                    $"Expected {pkCols.Count} key(s), got {keys.Length}."));

            var (clause, parameters) = BuildPrimaryKeyWhere(pkCols, keys);
            var sql = $"DELETE FROM \"{_tableName}\" WHERE {clause};";
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await _connectionManager.ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] DeleteByKeysAsync failed: {ex.Message}", ex);
            return Result.Failure(Error.FromException(ex, "sqlite.delete_failed"));
        }
    }

    /// <summary>
    /// Returns the total number of rows in the table.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the row count.</returns>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var sql = $"SELECT COUNT(*) FROM \"{_tableName}\";";
            LogQuery(sql);

            var count = await _connectionManager.ExecuteAsync<long>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return result is null ? 0L : Convert.ToInt64(result);
            }, _connectionId, ct).ConfigureAwait(false);

            return Result<long>.Success(count);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "sqlite.count_failed"));
        }
    }

    /// <summary>
    /// Returns a paginated slice of rows from the table.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of rows per page.</param>
    /// <param name="orderBy">Optional column name to sort by.</param>
    /// <param name="desc">When <c>true</c>, sorts in descending order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing a <see cref="PagedResult{T}"/> with items and total count.</returns>
    public async Task<Result<PagedResult<T>>> GetPagedAsync(
        int page,
        int pageSize,
        string? orderBy = null,
        bool desc = false,
        CancellationToken ct = default)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var orderClause = orderBy is not null
                ? $" ORDER BY \"{orderBy}\" {(desc ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM \"{_tableName}\";";
            var dataSql = $"SELECT * FROM \"{_tableName}\"{orderClause} LIMIT {pageSize} OFFSET {offset};";
            LogQuery(dataSql);
            var sw = Stopwatch.StartNew();

            var (items, total) = await _connectionManager.ExecuteAsync<(List<T>, long)>(async conn =>
            {
                await using var countCmd = conn.CreateCommand();
                countCmd.CommandText = countSql;
                var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = dataSql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var entities = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    entities.Add(MapFromReader(reader));

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
            _logger?.Error($"[SQLite] GetPagedAsync failed: {ex.Message}", ex);
            return Result<PagedResult<T>>.Failure(Error.FromException(ex, "sqlite.get_failed"));
        }
    }

    /// <summary>
    /// Executes a raw SQL SELECT statement and maps the results to a list of entities.
    /// </summary>
    /// <param name="sql">The raw SQL SELECT query to execute.</param>
    /// <param name="params">Optional named parameters to bind (e.g. <c>@id</c> → value).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the list of mapped entities.</returns>
    public async Task<Result<List<T>>> RawQueryAsync(
        string sql,
        Dictionary<string, object?>? @params = null,
        CancellationToken ct = default)
    {
        try
        {
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await _connectionManager.ExecuteAsync<List<T>>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (@params is not null)
                    foreach (var kv in @params)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    items.Add(MapFromReader(reader));
                return items;
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<List<T>>.Success(list);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] RawQueryAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "sqlite.query_failed"));
        }
    }

    /// <summary>
    /// Executes a raw non-query SQL statement (INSERT, UPDATE, DELETE, etc.).
    /// </summary>
    /// <param name="sql">The raw SQL statement to execute.</param>
    /// <param name="params">Optional named parameters to bind (e.g. <c>@id</c> → value).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="Result{T}"/> containing the number of rows affected.</returns>
    public async Task<Result<int>> RawExecuteAsync(
        string sql,
        Dictionary<string, object?>? @params = null,
        CancellationToken ct = default)
    {
        try
        {
            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await _connectionManager.ExecuteAsync<int>(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                if (@params is not null)
                    foreach (var kv in @params)
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[SQLite] RawExecuteAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "sqlite.execute_failed"));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static T MapFromReader(SqliteDataReader reader)
    {
        var entity = new T();
        var props = GetCachedProperties();

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

    private static object? ConvertToDbValue(object? value, Type type)
    {
        if (value is null) return null;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(bool)) return (bool)value ? 1 : 0;
        if (underlying == typeof(DateTime)) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fff");
        if (underlying == typeof(DateTimeOffset)) return ((DateTimeOffset)value).ToString("yyyy-MM-dd HH:mm:ss.fffzzz");
        if (underlying == typeof(Guid)) return value.ToString();
        if (underlying.IsEnum) return Convert.ToInt64(value);

        return value;
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
        if (underlying == typeof(DateTime) && value is string dtStr)
            return DateTime.Parse(dtStr);
        if (underlying == typeof(Guid) && value is string guidStr)
            return Guid.Parse(guidStr);
        if (underlying.IsEnum)
            return Enum.ToObject(underlying, Convert.ToInt64(value));

        try { return Convert.ChangeType(value, underlying); }
        catch { return value; }
    }

    private static List<(string ColumnName, PropertyInfo Property)> GetPrimaryKeyColumns()
    {
        return GetCachedProperties()
            .Where(p => p.GetCustomAttribute<SQLiteColumnAttribute>()?.IsPrimaryKey == true)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                return (attr?.ColumnName ?? p.Name, p);
            })
            .ToList();
    }

    private static string[] GetPrimaryKeyColumnNames() =>
        GetCachedProperties()
            .Where(p => p.GetCustomAttribute<SQLiteColumnAttribute>()?.IsPrimaryKey == true)
            .Select(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                return attr?.ColumnName ?? p.Name;
            })
            .ToArray();

    private static (string Clause, Dictionary<string, object?> Parameters) BuildPrimaryKeyWhere(
        List<(string ColumnName, PropertyInfo Property)> pkCols,
        object[] values)
    {
        var parameters = new Dictionary<string, object?>();
        var clauses = new List<string>();

        for (int i = 0; i < pkCols.Count; i++)
        {
            var paramName = $"@pk{i}";
            clauses.Add($"\"{pkCols[i].ColumnName}\" = {paramName}");
            parameters[paramName] = values[i];
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    private static PropertyInfo? TryGetAutoIncrementPk() =>
        GetCachedProperties()
            .FirstOrDefault(p =>
            {
                var attr = p.GetCustomAttribute<SQLiteColumnAttribute>();
                return attr?.IsPrimaryKey == true && attr.IsAutoIncrement;
            });

    private static string GetTableName()
    {
        var attr = typeof(T).GetCustomAttribute<SQLiteTableAttribute>();
        return attr?.TableName ?? typeof(T).Name;
    }

    private static PropertyInfo[] GetCachedProperties() =>
        _cachedProperties.GetOrAdd(typeof(T), t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<SQLiteColumnAttribute>() is not null)
             .ToArray());

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
