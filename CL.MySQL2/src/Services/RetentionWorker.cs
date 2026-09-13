using System.Collections.Concurrent;
using System.Reflection;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using CodeLogic.Core.Logging;

namespace CL.MySQL2.Services;

/// <summary>
/// Background worker that purges old rows from entities marked with
/// <see cref="RetainDaysAttribute"/>. Runs once per 24 hours; on first start it runs
/// after a short delay so library startup isn't blocked by a potentially long delete.
/// <para>
/// The entry list is <b>live</b>: entities registered after the worker was constructed
/// (the normal case — schema sync usually runs after <c>CodeLogic.StartAsync()</c>) are
/// picked up by <see cref="TryRegister"/>, and the library starts the loop the first time
/// a <c>[RetainDays]</c> entity appears. <see cref="Start"/> is idempotent.
/// </para>
/// <para>
/// Each purge pass runs <c>DELETE FROM {table} WHERE {col} &lt; @cutoff LIMIT batchSize</c>
/// repeatedly until a pass deletes fewer rows than the batch size. The cutoff is computed
/// client-side as <c>DateTime.UtcNow.AddDays(-days)</c> and bound as a parameter — the server's
/// own clock is not consulted. That keeps individual transactions small
/// (friendly to InnoDB's undo log) while still converging on empty.
/// </para>
/// </summary>
public sealed class RetentionWorker : IAsyncDisposable
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    // Live set, safe to mutate while the loop reads it: the loop enumerates a snapshot.
    private readonly ConcurrentDictionary<Type, RetainDaysAttribute> _entries = new();
    private readonly string _connectionId;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private readonly object _startGate = new();

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
        foreach (var t in registeredEntities) TryRegister(t);
    }

    /// <summary>Whether any registered entity has a retention policy to run.</summary>
    public bool HasWork => !_entries.IsEmpty;

    /// <summary>The entity types currently covered by a retention policy.</summary>
    public IReadOnlyCollection<Type> Entities => _entries.Keys.ToArray();

    /// <summary>
    /// Adds <paramref name="entityType"/> to the live entry list if it carries
    /// <see cref="RetainDaysAttribute"/>. Returns true when it was added (i.e. it has a policy
    /// and was not already registered), which is the caller's cue to <see cref="Start"/>.
    /// Safe to call while the background loop is running.
    /// </summary>
    public bool TryRegister(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var attr = entityType.GetCustomAttribute<RetainDaysAttribute>();
        if (attr is null) return false;
        return _entries.TryAdd(entityType, attr);
    }

    /// <summary>
    /// Runs one retention pass immediately over every registered entity and returns the
    /// number of rows removed. The background loop only wakes once a day behind an initial
    /// delay, so this is the entry point for an operator-triggered purge — and the only way
    /// to exercise the pass deterministically in a test.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var (entityType, attr) in _entries.ToArray())
            removed += await PurgeEntityAsync(entityType, attr, ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Starts the background loop if there is work and it is not already running. Idempotent,
    /// and a no-op after disposal.
    /// </summary>
    public void Start()
    {
        lock (_startGate)
        {
            if (_disposed || !HasWork || _loop is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
            _logger?.Info($"[MySQL2] Retention worker started for {_entries.Count} entit{(_entries.Count == 1 ? "y" : "ies")}.");
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
            // Snapshot per pass: entities registered mid-pass are picked up on the next one.
            foreach (var (entityType, attr) in _entries.ToArray())
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    await PurgeEntityAsync(entityType, attr, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger?.Warning($"[MySQL2] Retention purge failed for {entityType.Name}: {ex.Message}");
                }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<int> PurgeEntityAsync(Type entityType, RetainDaysAttribute attr, CancellationToken ct)
    {
        // Resolve column name via reflection — EntityMetadata<T> isn't reachable without
        // a type parameter, so we do the minimum lookup ourselves.
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>();
        var tableName = !string.IsNullOrEmpty(tableAttr?.Name) ? tableAttr.Name! : entityType.Name;

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
            var sql = $"DELETE FROM {MySqlDialect.Quote(tableName)} WHERE {MySqlDialect.Quote(colName)} < @cutoff LIMIT {attr.BatchSize}";
            var affected = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand();
                _connectionManager.ApplyCommandTimeout(cmd, _connectionId);
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, _connectionId, ct).ConfigureAwait(false);

            totalDeleted += affected;

            if (affected < attr.BatchSize) break; // drained or below batch size
            // Yield briefly between batches so we don't hog the connection pool.
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }

        if (totalDeleted > 0)
        {
            _logger?.Info($"[MySQL2] Retention purge: deleted {totalDeleted} row(s) from `{tableName}` older than {attr.Days} days.");
            QueryCache.Invalidate(tableName);
        }

        return totalDeleted;
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_startGate)
        {
            _disposed = true;
            loop = _loop;
            _loop = null;
        }

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); } catch { }
        }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
        _cts = null;
    }
}
