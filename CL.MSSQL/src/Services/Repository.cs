using System.Diagnostics;
using System.Linq.Expressions;
using CL.MSSQL.Core;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Services;

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
            var table = EntityMetadata<T>.QualifiedTableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement && !c.IsRowVersion).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => Q(c.ColumnName)));
            var paramList  = string.Join(", ", insertCols.Select((_, i) => $"@p_{i}"));
            var pk = EntityMetadata<T>.PrimaryKey;
            var output = pk is null ? "OUTPUT 1" : $"OUTPUT INSERTED.{Q(pk.ColumnName)}";
            var sql = $"INSERT INTO {table} ({columnList}) {output} VALUES ({paramList});";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                for (var i = 0; i < insertCols.Length; i++)
                {
                    var col = insertCols[i];
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@p_{i}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute));
                }
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

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
            _logger?.Error($"[MSSQL] InsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mssql.insert_failed"));
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
            var table = EntityMetadata<T>.QualifiedTableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement && !c.IsRowVersion).ToArray();
            var columnList = string.Join(", ", insertCols.Select(c => Q(c.ColumnName)));
            var batchSize = Math.Min(_maxBatchInsertSize, SqlServerDialect.MaxBatchRows(insertCols.Length));

            var inserted = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += batchSize)
                {
                    var end = Math.Min(start + batchSize, list.Count);
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
                            cmd.Parameters.Add(TypeConverter.CreateParameter(paramName, TypeConverter.ToDbValue(insertCols[j].Get(entity!), insertCols[j].EffectiveStorageType), insertCols[j].Attribute));
                        }
                        valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
                    }

                    cmd.CommandText = $"INSERT INTO {table} ({columnList}) VALUES {string.Join(", ", valueTuples)};";
                    LogQuery(cmd.CommandText);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    inserted += count;
                }
                return inserted;
            }).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[MSSQL] Bulk-inserted {inserted} records into {table} in {sw.ElapsedMilliseconds}ms");
            QueryCache.Invalidate(table);
            return Result<int>.Success(inserted);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] InsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.bulk_insert_failed"));
        }
    }

    /// <summary>
    /// Inserts a single entity, or updates all non-auto-PK columns to the entity's values
    /// if a UNIQUE/PRIMARY-KEY conflict occurs (set semantics). Issues
    /// <c>INSERT ... AS new locked source-table upsert</c> (SQL Server 2019+ alias syntax).
    /// On a new insert the auto-PK is refreshed from <c>OUTPUT INSERTED</c>; on a pure
    /// update the entity's existing PK value is preserved.
    /// </summary>
    public async Task<Result<T>> UpsertAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement && !c.IsRowVersion).ToArray();
            // The table-variable source supplies both match and update values while the target
            // is protected by serializable update/hold locks.
            var matchGroups = GetUpsertMatchGroups(insertCols);
            var sql = BuildUpsertSql(table, insertCols, matchGroups, 1, incrementColumns: null, returnIdentity: true);

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var lastId = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                for (var i = 0; i < insertCols.Length; i++)
                {
                    var col = insertCols[i];
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@p_0_{i}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute));
                }
                return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);

            var pk = EntityMetadata<T>.PrimaryKey;
            if (pk is not null && pk.IsAutoIncrement && lastId is not null && lastId is not DBNull)
            {
                // OUTPUT INSERTED is 0 on a pure update; only refresh on a new insert.
                var converted = Convert.ChangeType(lastId, pk.Property.PropertyType);
                pk.Set(entity, converted);
            }

            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] UpsertAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mssql.upsert_failed"));
        }
    }

    /// <summary>
    /// Bulk-upserts a collection of entities using batched
    /// <c>INSERT ... locked source-table upsert</c> statements (set semantics).
    /// Batches of up to <c>maxBatchInsertSize</c> (default 500) are sent per round-trip.
    /// Returns the total rows-affected count (SQL Server counts 1 for each insert and 2 for each
    /// update, so this is not equal to <c>entities.Count</c>).
    /// </summary>
    public async Task<Result<int>> UpsertManyAsync(IEnumerable<T> entities, CancellationToken ct = default)
    {
        var list = entities as IList<T> ?? entities.ToList();
        if (list.Count == 0) return Result<int>.Success(0);

        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement && !c.IsRowVersion).ToArray();
            // Use the same locked table-variable source as the single-row upsert.
            var matchGroups = GetUpsertMatchGroups(insertCols);
            var batchSize = Math.Min(_maxBatchInsertSize, SqlServerDialect.MaxBatchRows(insertCols.Length));

            var affected = 0;
            var sw = Stopwatch.StartNew();

            await ExecuteAsync<int>(async conn =>
            {
                for (var start = 0; start < list.Count; start += batchSize)
                {
                    var end = Math.Min(start + batchSize, list.Count);
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
                            cmd.Parameters.Add(TypeConverter.CreateParameter(paramName, TypeConverter.ToDbValue(insertCols[j].Get(entity!), insertCols[j].EffectiveStorageType), insertCols[j].Attribute));
                        }
                        valueTuples[i] = "(" + string.Join(", ", tupleParts) + ")";
                    }

                    cmd.CommandText = BuildUpsertSql(table, insertCols, matchGroups, count, incrementColumns: null, returnIdentity: false);
                    LogQuery(cmd.CommandText);
                    affected += Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                }
                return affected;
            }).ConfigureAwait(false);

            sw.Stop();
            _logger?.Debug($"[MSSQL] Bulk-upserted {list.Count} records into {table} in {sw.ElapsedMilliseconds}ms (rows affected: {affected})");
            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] UpsertManyAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.bulk_upsert_failed"));
        }
    }

    /// <summary>
    /// Inserts <paramref name="insertSeed"/> if no UNIQUE/PRIMARY-KEY conflict occurs;
    /// otherwise applies increment / set semantics to the listed properties on conflict.
    /// Properties NOT listed in either array are insert-only — present in the
    /// <c>VALUES</c> clause but absent from <c>locked source-table upsert</c> (so they don't
    /// change on conflict — useful for <c>created_utc</c> style columns). Property names
    /// resolve through <see cref="EntityMetadata{T}"/> so callers can use
    /// <c>nameof(...)</c> for compile-time-safe column references.
    /// </summary>
    /// <param name="insertSeed">Row to INSERT if no conflict. All non-auto-PK columns
    /// go into the <c>VALUES</c> clause.</param>
    /// <param name="incrementProperties">C# property names whose columns should
    /// <c>col = col + new.col</c> on conflict.</param>
    /// <param name="setProperties">C# property names whose columns should <c>col = new.col</c>
    /// on conflict. Defaults to empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rows affected (SQL Server: 1 = insert, 2 = update with changes, 0 = update with
    /// no change).</returns>
    /// <exception cref="ArgumentException">
    /// A name in <paramref name="incrementProperties"/> or <paramref name="setProperties"/>
    /// does not resolve to a property on <typeparamref name="T"/>, refers to an
    /// auto-increment PK, or appears in both arrays.
    /// </exception>
    public async Task<Result<int>> UpsertWithIncrementsAsync(
        T insertSeed,
        IReadOnlyList<string> incrementProperties,
        IReadOnlyList<string>? setProperties = null,
        CancellationToken ct = default)
    {
        if (insertSeed is null) throw new ArgumentNullException(nameof(insertSeed));
        if (incrementProperties is null) throw new ArgumentNullException(nameof(incrementProperties));
        setProperties ??= Array.Empty<string>();

        var incrementCols = ResolveUpsertColumns(incrementProperties, nameof(incrementProperties));
        var setCols       = ResolveUpsertColumns(setProperties, nameof(setProperties));

        // Reject overlap: a property listed in both arrays would produce ambiguous SQL.
        if (incrementCols.Length > 0 && setCols.Length > 0)
        {
            var incNames = new HashSet<string>(incrementProperties, StringComparer.Ordinal);
            foreach (var name in setProperties)
            {
                if (incNames.Contains(name))
                    throw new ArgumentException(
                        $"Property '{name}' appears in both incrementProperties and setProperties.",
                        nameof(setProperties));
            }
        }

        if (incrementCols.Length == 0 && setCols.Length == 0)
            throw new ArgumentException(
                "At least one of incrementProperties or setProperties must contain entries.",
                nameof(incrementProperties));

        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var insertCols = EntityMetadata<T>.Columns.Where(c => !c.IsAutoIncrement && !c.IsRowVersion).ToArray();

            // Increment expressions combine the locked target value with the source value.
            var incrementNames = incrementCols.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var updateNames = incrementCols.Concat(setCols).Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sql = BuildUpsertSql(table, insertCols, GetUpsertMatchGroups(insertCols), 1, incrementNames, returnIdentity: false, updateColumns: updateNames);

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                for (var i = 0; i < insertCols.Length; i++)
                {
                    var col = insertCols[i];
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@p_0_{i}", TypeConverter.ToDbValue(col.Get(insertSeed), col.EffectiveStorageType), col.Attribute));
                }
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            QueryCache.Invalidate(table);
            return Result<int>.Success(affected);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] UpsertWithIncrementsAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.upsert_increment_failed"));
        }
    }

    private static ColumnMetadata[] ResolveUpsertColumns(IReadOnlyList<string> propertyNames, string paramName)
    {
        var cols = new ColumnMetadata[propertyNames.Count];
        for (var i = 0; i < propertyNames.Count; i++)
        {
            var name = propertyNames[i];
            if (!EntityMetadata<T>.ColumnsByPropertyName.TryGetValue(name, out var col))
                throw new ArgumentException($"Property '{name}' not found on type {typeof(T).Name}", paramName);
            if (col.IsAutoIncrement || col.IsRowVersion)
                throw new ArgumentException(
                    $"Property '{name}' is database-generated and cannot appear in {paramName}.",
                    paramName);
            cols[i] = col;
        }
        return cols;
    }

    /// <summary>Retrieves an entity by its primary key value.</summary>
    public async Task<Result<T?>> GetByIdAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"SELECT * FROM {table} WHERE {Q(pk.ColumnName)} = @id{SoftAnd()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var result = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType) ?? DBNull.Value);
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
            _logger?.Error($"[MSSQL] GetByIdAsync failed: {ex.Message}", ex);
            return Result<T?>.Failure(Error.FromException(ex, "mssql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities where the specified column equals the given value.</summary>
    public async Task<Result<List<T>>> GetByColumnAsync(string column, object value, CancellationToken ct = default)
    {
        try
        {
            var col = EntityMetadata<T>.RequireColumn(column);
            var table = EntityMetadata<T>.QualifiedTableName;
            var sql = $"SELECT * FROM {table} WHERE {Q(col.ColumnName)} = @val{SoftAnd()}";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            var list = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@val", TypeConverter.ToDbValue(value, col.EffectiveStorageType) ?? DBNull.Value);
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
            _logger?.Error($"[MSSQL] GetByColumnAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mssql.get_failed"));
        }
    }

    /// <summary>Retrieves all entities in the table.</summary>
    public async Task<Result<List<T>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var sql = $"SELECT * FROM {table}{SoftWhere()}";

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
            _logger?.Error($"[MSSQL] GetAllAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mssql.get_failed"));
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

            var table = EntityMetadata<T>.QualifiedTableName;
            var offset = (page - 1) * pageSize;
            var fallbackOrder = EntityMetadata<T>.PrimaryKey?.ColumnName;
            var orderClause = orderCol is not null
                ? $" ORDER BY {Q(orderCol)} {(descending ? "DESC" : "ASC")}"
                : fallbackOrder is not null ? $" ORDER BY {Q(fallbackOrder)}" : " ORDER BY (SELECT 1)";

            var countSql = $"SELECT COUNT(*) FROM {table}{SoftWhere()}";
            var dataSql = $"SELECT * FROM {table}{SoftWhere()}{orderClause} OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

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
            _logger?.Error($"[MSSQL] GetPagedAsync failed: {ex.Message}", ex);
            return Result<Models.PagedResult<T>>.Failure(Error.FromException(ex, "mssql.get_failed"));
        }
    }

    /// <summary>Returns the total row count for the table.</summary>
    public async Task<Result<long>> CountAsync(CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var sql = $"SELECT COUNT(*) FROM {table}";
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
            _logger?.Error($"[MSSQL] CountAsync failed: {ex.Message}", ex);
            return Result<long>.Failure(Error.FromException(ex, "mssql.count_failed"));
        }
    }

    /// <summary>Updates an existing entity by PK.</summary>
    public async Task<Result<T>> UpdateAsync(T entity, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var setCols = EntityMetadata<T>.Columns
                .Where(c => c != pk && !c.IsAutoIncrement && !c.IsRowVersion).ToArray();
            var setClauses = string.Join(", ", setCols.Select((c, i) => $"{Q(c.ColumnName)} = @p_{i}"));
            var sql = $"UPDATE {table} SET {setClauses} WHERE {Q(pk.ColumnName)} = @__pk";

            LogQuery(sql);
            var sw = Stopwatch.StartNew();

            await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                for (var i = 0; i < setCols.Length; i++)
                {
                    var col = setCols[i];
                    cmd.Parameters.Add(TypeConverter.CreateParameter($"@p_{i}", TypeConverter.ToDbValue(col.Get(entity), col.EffectiveStorageType), col.Attribute));
                }
                cmd.Parameters.AddWithValue("@__pk", TypeConverter.ToDbValue(pk.Get(entity), pk.EffectiveStorageType) ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            sw.Stop();
            LogSlowQuery(sql, sw.ElapsedMilliseconds);
            QueryCache.Invalidate(table);
            return Result<T>.Success(entity);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] UpdateAsync failed: {ex.Message}", ex);
            return Result<T>.Failure(Error.FromException(ex, "mssql.update_failed"));
        }
    }

    /// <summary>
    /// Deletes an entity by its primary key. For a <see cref="Models.SoftDeleteAttribute"/>
    /// entity this is a <b>soft delete</b> — it sets the timestamp column to UtcNow instead of
    /// removing the row (no-op if already deleted). Use <see cref="HardDeleteAsync"/> to remove
    /// the row physically. Returns true when a row was affected.
    /// </summary>
    public async Task<Result<bool>> DeleteAsync(object id, CancellationToken ct = default)
    {
        var soft = EntityMetadata<T>.SoftDeleteColumn;
        return soft is null
            ? await HardDeleteAsync(id, ct).ConfigureAwait(false)
            : await SoftDeleteAsync(id, soft, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Physically removes the row by primary key, even for soft-delete entities.
    /// </summary>
    public async Task<Result<bool>> HardDeleteAsync(object id, CancellationToken ct = default)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"DELETE FROM {table} WHERE {Q(pk.ColumnName)} = @id";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType) ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] HardDeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.delete_failed"));
        }
    }

    private async Task<Result<bool>> SoftDeleteAsync(object id, ColumnMetadata soft, CancellationToken ct)
    {
        try
        {
            var table = EntityMetadata<T>.QualifiedTableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var sql = $"UPDATE {table} SET {Q(soft.ColumnName)} = @now " +
                      $"WHERE {Q(pk.ColumnName)} = @id AND {Q(soft.ColumnName)} IS NULL";

            LogQuery(sql);
            var affected = await ExecuteAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@id", TypeConverter.ToDbValue(id, pk.EffectiveStorageType) ?? DBNull.Value);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            QueryCache.Invalidate(table);
            return Result<bool>.Success(affected > 0);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MSSQL] Soft DeleteAsync failed: {ex.Message}", ex);
            return Result<bool>.Failure(Error.FromException(ex, "mssql.delete_failed"));
        }
    }

    // Soft-delete read-filter fragments — empty when the entity has no [SoftDelete].
    private static string SoftAnd()
        => EntityMetadata<T>.SoftDeleteColumn is { } c ? $" AND {Q(c.ColumnName)} IS NULL" : string.Empty;

    private static string SoftWhere()
        => EntityMetadata<T>.SoftDeleteColumn is { } c ? $" WHERE {Q(c.ColumnName)} IS NULL" : string.Empty;

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
            var table = EntityMetadata<T>.QualifiedTableName;
            var pk = EntityMetadata<T>.RequirePrimaryKey();
            var colName = SqlServerExpressionVisitor.TranslateSelector(propertySelector);
            var sql = $"UPDATE {table} SET {Q(colName)} = {Q(colName)} + @delta WHERE {Q(pk.ColumnName)} = @id";

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
            _logger?.Error($"[MSSQL] AdjustAsync failed: {ex.Message}", ex);
            return Result<int>.Failure(Error.FromException(ex, "mssql.update_failed"));
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
            var table = EntityMetadata<T>.QualifiedTableName;
            var (whereClause, parameters) = SqlServerExpressionVisitor.Translate(predicate);
            var sql = $"SELECT * FROM {table} WHERE {whereClause}{SoftAnd()}";

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
            _logger?.Error($"[MSSQL] FindAsync failed: {ex.Message}", ex);
            return Result<List<T>>.Failure(Error.FromException(ex, "mssql.find_failed"));
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static IReadOnlyList<IReadOnlyList<ColumnMetadata>> GetUpsertMatchGroups(IReadOnlyList<ColumnMetadata> sourceColumns)
    {
        var available = sourceColumns.ToHashSet();
        var groups = new List<IReadOnlyList<ColumnMetadata>>();
        var primary = EntityMetadata<T>.Columns.Where(c => c.IsPrimary && available.Contains(c)).ToArray();
        if (primary.Length > 0) groups.Add(primary);
        groups.AddRange(EntityMetadata<T>.Columns.Where(c => c.Attribute?.Unique == true && available.Contains(c))
            .Select(c => (IReadOnlyList<ColumnMetadata>)new[] { c }));
        foreach (var index in EntityMetadata<T>.CompositeIndexes.Where(index => index.Unique))
        {
            var columns = index.ColumnNames.Select(EntityMetadata<T>.RequireColumn).ToArray();
            if (columns.All(available.Contains)) groups.Add(columns);
        }
        if (groups.Count == 0)
            throw new InvalidOperationException($"Entity '{typeof(T).Name}' needs a non-identity primary key or a unique column for upsert.");
        return groups;
    }

    private static string BuildUpsertSql(string table, IReadOnlyList<ColumnMetadata> columns,
        IReadOnlyList<IReadOnlyList<ColumnMetadata>> matchGroups, int rowCount, IReadOnlySet<string>? incrementColumns,
        bool returnIdentity, IReadOnlySet<string>? updateColumns = null)
    {
        var declarations = string.Join(", ", columns.Select(c =>
        {
            var attr = c.Attribute ?? TypeConverter.InferColumn(c.Property.PropertyType);
            return $"{Q(c.ColumnName)} {TypeConverter.GetSqlServerType(attr, attr.StorageType, c.Property.PropertyType)} NULL";
        }));
        var values = string.Join(", ", Enumerable.Range(0, rowCount).Select(i =>
            $"({i}, {string.Join(", ", columns.Select((_, j) => $"@p_{i}_{j}"))})"));
        var sourceColumns = string.Join(", ", columns.Select(c => Q(c.ColumnName)));
        var match = string.Join(" OR ", matchGroups.Select(group => "(" + string.Join(" AND ", group.Select(c =>
            $"(target.{Q(c.ColumnName)} = source.{Q(c.ColumnName)} OR (target.{Q(c.ColumnName)} IS NULL AND source.{Q(c.ColumnName)} IS NULL))")) + ")"));
        var updateCandidates = columns.Where(c => !c.IsPrimary && (updateColumns is null || updateColumns.Contains(c.ColumnName))).ToArray();
        var updates = string.Join(", ", updateCandidates.Select(c =>
            $"target.{Q(c.ColumnName)} = " + (incrementColumns?.Contains(c.ColumnName) == true
                ? $"target.{Q(c.ColumnName)} + source.{Q(c.ColumnName)}" : $"source.{Q(c.ColumnName)}")));
        if (updates.Length == 0)
            updates = $"target.{Q(matchGroups[0][0].ColumnName)} = target.{Q(matchGroups[0][0].ColumnName)}";
        var pk = EntityMetadata<T>.PrimaryKey;
        var output = returnIdentity ? (pk is null ? "OUTPUT 1" : $"OUTPUT INSERTED.{Q(pk.ColumnName)}") : "";
        return $"""
            SET XACT_ABORT ON;
            DECLARE @source TABLE ([__row] int NOT NULL, {declarations});
            INSERT INTO @source ([__row], {sourceColumns}) VALUES {values};
            DECLARE @own bit = 0;
            IF @@TRANCOUNT = 0 BEGIN SET @own = 1; SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; BEGIN TRANSACTION; END;
            BEGIN TRY
                DECLARE @affected int = 0;
                IF EXISTS (SELECT 1 FROM @source source JOIN {table} target WITH (UPDLOCK, HOLDLOCK) ON {match} GROUP BY source.[__row] HAVING COUNT_BIG(*) > 1)
                    THROW 50001, 'Ambiguous upsert: multiple unique keys matched different rows.', 1;
                UPDATE target WITH (UPDLOCK, HOLDLOCK) SET {updates} {output}
                FROM {table} target JOIN @source source ON {match};
                SET @affected += @@ROWCOUNT;
                INSERT INTO {table} ({sourceColumns}) {output}
                SELECT {string.Join(", ", columns.Select(c => $"source.{Q(c.ColumnName)}"))} FROM @source source
                WHERE NOT EXISTS (SELECT 1 FROM {table} target WITH (UPDLOCK, HOLDLOCK) WHERE {match});
                SET @affected += @@ROWCOUNT;
                IF @own = 1 COMMIT TRANSACTION;
                {(returnIdentity ? "" : "SELECT @affected;")}
            END TRY
            BEGIN CATCH
                IF @own = 1 AND XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                THROW;
            END CATCH;
            """;
    }

    private static string Q(string identifier) => SqlServerDialect.Quote(identifier);

    private async Task<TResult> ExecuteAsync<TResult>(Func<SqlConnection, Task<TResult>> action)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);

        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId).ConfigureAwait(false);
    }

    private void LogSlowQuery(string sql, long elapsedMs)
    {
        QueryObservability.RecordExecuted(_connectionId, sql, elapsedMs, rowCount: -1, cacheHit: false);
        if (elapsedMs >= _slowQueryThresholdMs)
            QueryObservability.RecordSlow(_connectionId, sql, elapsedMs);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MSSQL] SQL: {sql}");
    }
}
