using System.Diagnostics;
using System.Linq.Expressions;
using CL.MySQL2.Core;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// Generic repository providing CRUD for entity type <typeparamref name="T"/>.
/// Uses compiled materializers via <see cref="EntityMetadata{T}"/> — no per-row reflection.
/// </summary>
public sealed class Repository<T> where T : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly int _slowQueryThresholdMs;
    private readonly int _maxBatchInsertSize;
    private readonly TransactionScope? _transactionScope;

    // ── Constructors ──────────────────────────────────────────────────────────

    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger = null,
        string connectionId = "Default",
        int slowQueryThresholdMs = 1000,
        int maxBatchInsertSize = 500)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _connectionId = connectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _maxBatchInsertSize = maxBatchInsertSize;
    }

    public Repository(
        ConnectionManager connectionManager,
        ILogger? logger,
        TransactionScope transactionScope,
        int slowQueryThresholdMs = 1000,
        int maxBatchInsertSize = 500)
    {
        _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
        _logger = logger;
        _transactionScope = transactionScope ?? throw new ArgumentNullException(nameof(transactionScope));
        _connectionId = transactionScope.ConnectionId;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _maxBatchInsertSize = maxBatchInsertSize;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────

    /// <summary>Inserts a single entity and returns it (with auto-generated PK populated).</summary>
    public async Task<Result<T>> InsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"`{c.ColumnName}`"));
            var paramList  = string.Join(", ", insertCols.Select(c => $"@{c.ColumnName}"));
            var sql = $"INSERT INTO `{table}` ({columnList}) VALUES ({paramList}); SELECT LAST_INSERT_ID();";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var col in insertCols)
                    cmd.Parameters.AddWithValue($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity)) ?? DBNull.Value);
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            var pk = EntityMetadata<T>.PrimaryKey;
            if (pk is not null && pk.IsAutoIncrement && lastId is not null && lastId is not DBNull)
            {
                var converted = Convert.ChangeType(lastId, pk.Property.PropertyType);
                pk.Set(entity, converted);
            }

            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] InsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mysql.insert_failed"));
        }
    }

    /// <summary>
    /// Bulk-inserts a collection of entities using real batched INSERT statements.
    /// Batches of up to <c>maxBatchInsertSize</c> (default 500) are sent per round-trip.
    /// </summary>
    public async Task<Result<int>> InsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.TableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => $"`{c.ColumnName}`"));

            var inserted = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += _maxBatchInsertSize)
                {
                    var end = Math.Min(start + _maxBatchInsertSize, list.Count);
                    var count = end - start;

                    await using var cmd = conn.CreateCommand();
                    if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;

                    var valueTuples = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var entity = list[start + i];
                        var tupleParts = new string[insertCols.Length];
                        for (var j = 0; j < insertCols.Length; j++)
                        {
                            var paramName = $"@p_{i}_{j}";
                            tupleParts[j] = paramName;
                            cmd.Parameters.AddWithValue(paramName, TypeConverter.ToDbValue(insertCols[j].Get(entity!)) ?? DBNull.Value);
                        }
                        valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
                    }

                    cmd.CommandText = $"INSERT INTO `{table}` ({columnList}) VALUES {string.Join(", ", valueTuples)};";
                    LogQuery(cmd.CommandText);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    inserted += count;
                }
                return inserted;
            }).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[MySQL2] Bulk-inserted {inserted} records into `{table}` in {sw.ElapsedMilliseconds}ms");
            QueryCache.Invalidate(table);
            return Result<int>.Success(inserted);
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
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"SELECT * FROM `{table}` WHERE `{pk.ColumnName}` = @id LIMIT 1";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                return map(reader);
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
            var col = EntityMetadata<T>.RequireColumn(column);
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT * FROM `{table}` WHERE `{col.ColumnName}` = @val";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@val", TypeConverter.ToDbValue(value) ?? DBNull.Value);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
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
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT * FROM `{table}`";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
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
    public async Task<Result<Models.PagedResult<T>>> GetPagedAsync(
        int page,
        int pageSize,
        string? orderByColumn = null,
        bool descending = false,
        CancellationToken ct = default)
    {
        try
        {
            var orderCol = orderByColumn is not null
                ? EntityMetadata<T>.RequireColumn(orderByColumn).ColumnName
                : null;

            var table = EntityMetadata<T>.TableName;
            var offset = (page - 1) * pageSize;
            var orderClause = orderCol is not null
                ? $" ORDER BY `{orderCol}` {(descending ? "DESC" : "ASC")}"
                : string.Empty;

            var countSql = $"SELECT COUNT(*) FROM `{table}`";
            var dataSql = $"SELECT * FROM `{table}`{orderClause} LIMIT {pageSize} OFFSET {offset}";

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
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var entities = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) entities.Add(map(reader));
                return (entities, totalCount);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(dataSql, sw.ElapsedMilliseconds);

            return Result<Models.PagedResult<T>>.Success(new Models.PagedResult<T>
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
            return Result<Models.PagedResult<T>>.Failure(Error.FromException(ex, "mysql.get_failed"));
        }
    }

    /// <summary>Returns the total row count for the table.</summary>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var sql = $"SELECT COUNT(*) FROM `{table}`";
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

    /// <summary>Updates an existing entity by PK.</summary>
    public async Task<Result<T>> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var setCols = EntityMetadata<T>.Columns.Where(c => c != pk).ToArray();
            var setClauses = string.Join(", ", setCols.Select(c => $"`{c.ColumnName}` = @{c.ColumnName}"));
            var sql = $"UPDATE `{table}` SET {setClauses} WHERE `{pk.ColumnName}` = @__pk";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                foreach (var col in setCols)
                    cmd.Parameters.AddWithValue($"@{col.ColumnName}", TypeConverter.ToDbValue(col.Get(entity)) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@__pk", pk.Get(entity));
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            QueryCache.Invalidate(table);
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
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"DELETE FROM `{table}` WHERE `{pk.ColumnName}` = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] DeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mysql.delete_failed"));
        }
    }

    /// <summary>
    /// Atomically adjusts a numeric column by <paramref name="delta"/>. Negative for decrement.
    /// </summary>
    public async Task<Result<int>> AdjustAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty delta,
        CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var colName = MySqlExpressionVisitor.TranslateSelector(propertySelector);
            var sql = $"UPDATE `{table}` SET `{colName}` = `{colName}` + @delta WHERE `{pk.ColumnName}` = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@delta", delta);
                cmd.Parameters.AddWithValue("@id", id);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] AdjustAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mysql.update_failed"));
        }
    }

    /// <summary>Increment a numeric column by <paramref name="amount"/>.</summary>
    public Task<Result<int>> IncrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty amount,
        CancellationToken ct = default)
        => AdjustAsync(id, propertySelector, amount, ct);

    /// <summary>Decrement a numeric column by <paramref name="amount"/>.</summary>
    public Task<Result<int>> DecrementAsync<TProperty>(
        object id,
        Expression<Func<T, TProperty>> propertySelector,
        TProperty amount,
        CancellationToken ct = default)
    {
        // Negate via dynamic since TProperty is an unconstrained numeric.
        dynamic d = amount!;
        return AdjustAsync(id, propertySelector, (TProperty)(-d), ct);
    }

    /// <summary>Returns all entities matching the given LINQ predicate.</summary>
    public async Task<Result<List<T>>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.TableName;
            var (whereClause, parameters) = MySqlExpressionVisitor.Translate(predicate);
            var sql = $"SELECT * FROM `{table}` WHERE {whereClause}";

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
                var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
                var items = new List<T>();
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
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
