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
    private readonly string? _smartCachePool;
    // Set when the originating query carried an EXISTS / IN subquery filter. Such a result
    // spans two tables, and the cache can only stamp one table's version onto an entry — so
    // caching is refused rather than silently serving a stale cross-table result.
    private readonly bool _hasSubqueryFilter;

    /// <summary>
    /// Enable result caching for this projected query. TTL and time-quantize behaviour come
    /// from <see cref="Configuration.CacheConfiguration"/> globally; per-call TTL wins here.
    /// <para>
    /// Ignored when the originating query carried a subquery filter (<c>WhereExists</c> /
    /// <c>WhereIn</c>): the cache stamps an entry with a single table's version, so it could
    /// not be invalidated by a mutation on the other table.
    /// </para>
    /// </summary>
    public ProjectedQuery<TSource, TResult> WithCache(TimeSpan ttl)
    {
        return new ProjectedQuery<TSource, TResult>(
            _connectionManager, _logger, _connectionId, _transactionScope,
            _slowQueryThresholdMs, _sql, _parameters, _projection, ttl, _smartCachePool,
            _hasSubqueryFilter);
    }

    /// <summary>
    /// Enable result caching using the configured <c>mysql.cache</c>
    /// <see cref="Configuration.CacheConfiguration.DefaultTtlSeconds"/> (default 60s).
    /// </summary>
    public ProjectedQuery<TSource, TResult> WithCache() => WithCache(QueryCache.DefaultTtl);

    /// <summary>
    /// Opt this projected query into a named <see cref="SmartCachePool"/>.
    /// See <c>QueryBuilder.SmartCache</c> for details.
    /// </summary>
    public ProjectedQuery<TSource, TResult> SmartCache(string poolName)
    {
        return new ProjectedQuery<TSource, TResult>(
            _connectionManager, _logger, _connectionId, _transactionScope,
            _slowQueryThresholdMs, _sql, _parameters, _projection, _cacheTtl, poolName,
            _hasSubqueryFilter);
    }

    internal ProjectedQuery(
        ConnectionManager connectionManager,
        ILogger? logger,
        string connectionId,
        TransactionScope? transactionScope,
        int slowQueryThresholdMs,
        string sql,
        Dictionary<string, object?> parameters,
        ProjectionCompiler.Compiled<TSource, TResult> projection,
        TimeSpan? cacheTtl,
        string? smartCachePool = null,
        bool hasSubqueryFilter = false)
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
        _smartCachePool = smartCachePool;
        _hasSubqueryFilter = hasSubqueryFilter;
    }

    // Plain cache-aside requires an explicit TTL. A query that asked for
    // SmartCache against an UNREGISTERED pool must fall through to truly
    // uncached execution (the logged fallback) — including _smartCachePool
    // here used to send it into the TTL branch and dereference a null
    // _cacheTtl ("Nullable object must have a value").
    private bool ShouldCache => _cacheTtl is not null && _transactionScope is null && !_hasSubqueryFilter;
    private bool ShouldSmartCache => _smartCachePool is not null && _transactionScope is null && !_hasSubqueryFilter;

    public async Task<Result<List<TResult>>> ToListAsync(CancellationToken ct = default)
    {
        try
        {
            var tableName = EntityMetadata<TSource>.TableName;

            if (ShouldSmartCache)
            {
                var pool = SmartCachePoolRegistry.Get(_smartCachePool!);
                if (pool is null)
                {
                    _logger?.Warning($"[MySQL2] SmartCache pool '{_smartCachePool}' is not registered — falling back to uncached execution.");
                }
                else
                {
                    var ttl = TimeSpan.FromMilliseconds(pool.RefreshEvery.TotalMilliseconds * 2);
                    pool.RegisterOrTouch(_connectionId, tableName, _sql, _parameters,
                        refreshFactory: async tickCt =>
                        {
                            var r = await Execute(tickCt).ConfigureAwait(false);
                            return r.IsSuccess ? (object?)r : null;
                        });

                    var cacheKey = QueryCache.BuildCacheKey(_connectionId, tableName, _sql, _parameters);
                    pool.Touch(_connectionId, tableName, _sql, _parameters);
                    return await QueryCache.GetOrSetAsync(
                        cacheKey, tableName,
                        () => Execute(ct), ttl, _connectionId).ConfigureAwait(false);
                }
            }

            if (ShouldCache)
            {
                var key = QueryCache.BuildCacheKey(_connectionId, tableName, _sql, _parameters);
                return await QueryCache.GetOrSetAsync(
                    key, tableName, () => Execute(ct), _cacheTtl!.Value, _connectionId).ConfigureAwait(false);
            }
            return await Execute(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"[MySQL2] ProjectedQuery.ToListAsync failed: {ex.Message} — query: {_sql}", ex);
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
            _connectionManager.ApplyCommandTimeout(cmd, _connectionId);
            cmd.CommandText = _sql;
            foreach (var kv in _parameters)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<TResult>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(_projection.Materializer(reader));
            return list;
        }, ct).ConfigureAwait(false);

        sw.Stop();

        QueryObservability.RecordExecuted(_connectionId, _sql, sw.ElapsedMilliseconds, items.Count, cacheHit: false);
        if (sw.ElapsedMilliseconds >= _slowQueryThresholdMs)
            QueryObservability.RecordSlow(_connectionManager, _connectionId, _sql, sw.ElapsedMilliseconds, _parameters);

        return Result<List<TResult>>.Success(items);
    }

    private async Task<T> ExecuteAsync<T>(Func<MySqlConnection, Task<T>> action, CancellationToken ct)
    {
        if (_transactionScope is not null)
            return await action(_transactionScope.Connection).ConfigureAwait(false);
        // The token has to reach ExecuteWithConnectionAsync, otherwise opening the
        // connection (and any transient-failure retry around it) ignores cancellation.
        return await _connectionManager.ExecuteWithConnectionAsync(action, _connectionId, ct).ConfigureAwait(false);
    }

    private void LogQuery(string sql)
    {
        if (CodeLogicEnvironment.IsDevelopment)
            _logger?.Debug($"[MySQL2] SQL: {sql}");
    }
}
