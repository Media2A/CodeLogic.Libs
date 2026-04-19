using System.Diagnostics;
using CL.MySQL2.Core;
using CodeLogic;
using CodeLogic.Core.Logging;
using CodeLogic.Core.Results;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// A query pipeline whose output rows are <typeparamref name="TResult"/>, produced by
/// projecting from an underlying entity type <typeparamref name="TSource"/>.
/// Built by <c>QueryBuilder&lt;T&gt;.Select&lt;TResult&gt;</c> (and, once task #4 lands, by
/// <c>GroupedQuery&lt;TKey, TSource&gt;.Select&lt;TResult&gt;</c>).
/// </summary>
public sealed class ProjectedQuery<TSource, TResult> where TSource : class, new()
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly string _connectionId;
    private readonly TransactionScope? _transactionScope;
    private readonly int _slowQueryThresholdMs;
    private readonly string _sql;
    private readonly Dictionary<string, object?> _parameters;
    private readonly ProjectionCompiler.Compiled<TSource, TResult> _projection;
    private readonly TimeSpan? _cacheTtl;

    internal ProjectedQuery(
        ConnectionManager connectionManager,
        ILogger? logger,
        string connectionId,
        TransactionScope? transactionScope,
        int slowQueryThresholdMs,
        string sql,
        Dictionary<string, object?> parameters,
        ProjectionCompiler.Compiled<TSource, TResult> projection,
        TimeSpan? cacheTtl)
    {
        _connectionManager = connectionManager;
        _logger = logger;
        _connectionId = connectionId;
        _transactionScope = transactionScope;
        _slowQueryThresholdMs = slowQueryThresholdMs;
        _sql = sql;
        _parameters = parameters;
        _projection = projection;
        _cacheTtl = cacheTtl;
    }

    private bool ShouldCache => _cacheTtl is not null && _transactionScope is null;

    public async Task<Result<List<TResult>>> ToListAsync(CancellationToken ct = default)
    {
        try
        {
            if (ShouldCache)
            {
                var tableName = EntityMetadata<TSource>.TableName;
                var key = QueryCache.BuildCacheKey(_connectionId, tableName, _sql, _parameters);
                return await QueryCache.GetOrSetAsync(key, tableName, () => Execute(ct), _cacheTtl!.Value).ConfigureAwait(false);
            }
            return await Execute(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] ProjectedQuery.ToListAsync failed: {ex.Message}", ex);
            return Result<List<TResult>>.Failure(Error.FromException(ex, "mysql.query_failed"));
        }
    }

    public async Task<Result<TResult?>> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var list = await ToListAsync(ct).ConfigureAwait(false);
        if (list.IsFailure) return Result<TResult?>.Failure(list.Error!);
        return Result<TResult?>.Success(list.Value!.Count == 0 ? default : list.Value[0]);
    }

    private async Task<Result<List<TResult>>> Execute(CancellationToken ct)
    {
        LogQuery(_sql);
        var sw = Stopwatch.StartNew();

        var items = await ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            if (_transactionScope is not null) cmd.Transaction = _transactionScope.Transaction;
            cmd.CommandText = _sql;
            foreach (var kv in _parameters)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<TResult>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(_projection.Materializer(reader));
            return list;
        }).ConfigureAwait(false);

        sw.Stop();
        if (sw.ElapsedMilliseconds >= _slowQueryThresholdMs)
            _logger?.Warning($"[MySQL2] [{_connectionId}] Slow query ({sw.ElapsedMilliseconds}ms): {_sql}");

        return Result<List<TResult>>.Success(items);
    }

    private async Task<T> ExecuteAsync<T>(Func<MySqlConnection, Task<T>> action)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);
        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId).ConfigureAwait(false);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MySQL2] SQL: {sql}");
    }
}
