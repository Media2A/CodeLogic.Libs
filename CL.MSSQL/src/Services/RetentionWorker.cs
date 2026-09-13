using System.Collections.Concurrent;
using System.Reflection;
using CL.MSSQL.Core;
using CL.MSSQL.Models;
using Microsoft.Data.SqlClient;
using CodeLogic.Core.Logging;

namespace CL.MSSQL.Services;

/// <summary>
/// Background worker that purges old rows from entities marked with
/// <see cref="RetainDaysAttribute"/>. Runs once per 24 hours; on first start it runs
/// after a short delay so library startup isn't blocked by a potentially long delete.
/// <para>
/// The entity set is <b>live</b>: <see cref="TryRegister(Type, string)"/> may be called at any
/// time — including long after <see cref="Start"/> — and the running loop picks the new
/// entity up on its next pass. The library registers every entity passed to
/// <c>SyncTableAsync</c> / <c>SyncSchemaAsync</c>, which normally happens after
/// <c>CodeLogic.StartAsync()</c>, and starts the loop the first time a
/// <c>[RetainDays]</c> entity appears.
/// </para>
/// <para>
/// Each purge pass runs a bounded <c>DELETE TOP (@batch)</c> statement repeatedly until one
/// batch deletes fewer rows than <see cref="RetainDaysAttribute.BatchSize"/> (the table is
/// drained). That keeps individual transactions small (friendly to SQL Server's transaction
/// log) while still converging on empty.
/// </para>
/// </summary>
public sealed class RetentionWorker : IAsyncDisposable
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    // Live entry set: safe to mutate while LoopAsync reads it. The loop enumerates a
    // snapshot of the values each pass, so a registration mid-pass lands on the next one.
    // Keyed by (entity, connection): the same entity can be synced against more than one
    // named connection, and each must be purged on the connection it was registered with.
    private readonly ConcurrentDictionary<(Type Type, string ConnectionId), RetainDaysAttribute> _entries = new();
    private readonly string _connectionId;
    private readonly object _startLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval     = TimeSpan.FromHours(24);

    public RetentionWorker(
        ConnectionManager connectionManager,
        ILogger? logger,
        IEnumerable<Type> registeredEntities,
        string connectionId = "Default")
    {
        _connectionManager = connectionManager;
        _logger = logger;
        _connectionId = connectionId;
        foreach (var entityType in registeredEntities) TryRegister(entityType);
    }

    /// <summary>
    /// Adds <paramref name="entityType"/> to the live entry list if it carries
    /// <see cref="RetainDaysAttribute"/>. Returns true when it was added (it has a policy and
    /// was not already registered), which is the caller's cue to <see cref="Start"/>. Safe to
    /// call while the background loop is running.
    /// </summary>
    public bool TryRegister(Type entityType) => TryRegister(entityType, _connectionId);

    /// <summary>
    /// As <see cref="TryRegister(Type)"/>, but records the connection the entity was
    /// registered against so the purge runs against that database rather than the default.
    /// </summary>
    public bool TryRegister(Type entityType, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var attr = entityType.GetCustomAttribute<RetainDaysAttribute>();
        if (attr is null) return false;
        return _entries.TryAdd((entityType, connectionId), attr);
    }

    /// <summary>The entity types currently covered by a retention policy.</summary>
    public IReadOnlyCollection<Type> Entities => _entries.Keys.Select(k => k.Type).Distinct().ToArray();

    /// <summary>The (entity, connection) pairs currently covered by a retention policy.</summary>
    public IReadOnlyCollection<(Type Type, string ConnectionId)> Registrations => _entries.Keys.ToArray();

    /// <summary>Whether any registered entity has a retention policy to run.</summary>
    public bool HasWork => !_entries.IsEmpty;

    /// <summary>
    /// Starts the background loop. Idempotent: a second call while the loop runs is a no-op,
    /// and a call with nothing to purge does nothing (call it again after registering one).
    /// </summary>
    public void Start()
    {
        lock (_startLock)
        {
            if (_disposed || !HasWork || _loop is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
            _logger?.Info($"[MSSQL] Retention worker started for {_entries.Count} entit{(_entries.Count == 1 ? "y" : "ies")}.");
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            // Snapshot per pass so registrations made while the pass runs are picked up on
            // the next one rather than invalidating the enumeration.
            foreach (var (key, attr) in _entries.ToArray())
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await PurgeEntityAsync(key.Type, attr, key.ConnectionId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger?.Warning($"[MSSQL] Retention purge failed for {key.Type.Name} on connection '{key.ConnectionId}': {ex.Message}");
                }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Runs one retention pass immediately over every registered entity and returns the
    /// number of rows removed. The background loop only wakes once a day behind an initial
    /// delay, so this is the entry point for an operator-triggered purge.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var (key, attr) in _entries.ToArray())
            total += await PurgeEntityAsync(key.Type, attr, key.ConnectionId, ct).ConfigureAwait(false);
        return total;
    }

    private async Task<int> PurgeEntityAsync(
        Type entityType, RetainDaysAttribute attr, string connectionId, CancellationToken ct)
    {
        // Resolve column name via reflection — EntityMetadata<T> isn't reachable without
        // a type parameter, so we do the minimum lookup ourselves.
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>();
        var tableName = !string.IsNullOrEmpty(tableAttr?.Name) ? tableAttr.Name! : entityType.Name;
        var tableSql = SqlServerDialect.Qualify(tableAttr?.Schema ?? "dbo", tableName);

        var prop = entityType.GetProperty(attr.TimestampColumn,
                       BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new InvalidOperationException(
                       $"[RetainDays] on {entityType.Name} names '{attr.TimestampColumn}' which is not a public instance property.");
        var colAttr = prop.GetCustomAttribute<ColumnAttribute>();
        var colName = !string.IsNullOrEmpty(colAttr?.Name) ? colAttr.Name! : prop.Name;

        var cutoff = DateTime.UtcNow.AddDays(-attr.Days);
        var totalDeleted = 0;

        while (!ct.IsCancellationRequested)
        {
            var sql = $"DELETE TOP (@batch) FROM {tableSql} WHERE {SqlServerDialect.Quote(colName)} < @cutoff";
            var affected = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.Add(new SqlParameter("@cutoff", System.Data.SqlDbType.DateTime2) { Value = cutoff });
                cmd.Parameters.Add(new SqlParameter("@batch", System.Data.SqlDbType.Int) { Value = attr.BatchSize });
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, connectionId, ct).ConfigureAwait(false);

            totalDeleted += affected;

            if (affected < attr.BatchSize) break; // drained or below batch size
            // Yield briefly between batches so we don't hog the connection pool.
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        if (totalDeleted > 0)
        {
            _logger?.Info($"[MSSQL] Retention purge: deleted {totalDeleted} row(s) from [{tableName}] older than {attr.Days} days.");
            QueryCache.Invalidate(tableName);
        }
        return totalDeleted;
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_startLock)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop;
            cts = _cts;
            _loop = null;
            _cts = null;
        }

        if (cts is not null) await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        cts?.Dispose();
    }
}
