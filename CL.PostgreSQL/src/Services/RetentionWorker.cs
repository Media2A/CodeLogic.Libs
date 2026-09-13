using System.Reflection;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using CodeLogic.Core.Logging;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Background worker that purges old rows from entities marked with
/// <see cref="RetainDaysAttribute"/>. Runs once per 24 hours; on first start it runs
/// after a short delay so library startup isn't blocked by a potentially long delete.
/// <para>
/// The entry list is <b>live</b>: <see cref="Register"/> may add an entity at any time,
/// including while the loop is running, and the library calls it from
/// <c>SyncTableAsync</c> / <c>SyncSchemaAsync</c>. That matters because every documented
/// flow registers entities <i>after</i> <c>CodeLogic.StartAsync()</c> — a snapshot taken
/// at start time would always be empty and <c>[RetainDays]</c> would never run.
/// </para>
/// <para>
/// PostgreSQL has no <c>LIMIT</c> on <c>DELETE</c>, so each pass deletes a batch selected by
/// <c>ctid</c> — <c>DELETE … WHERE ctid IN (SELECT ctid … ORDER BY {col} LIMIT batchSize
/// FOR UPDATE SKIP LOCKED)</c> — and repeats until a batch comes back short. That keeps each
/// transaction small, which is easier on autovacuum and on concurrent writers, while still
/// converging on empty.
/// </para>
/// </summary>
public sealed class RetentionWorker : IAsyncDisposable
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;

    // Live entry list. Guarded by _entriesLock for mutation; readers take a snapshot, so
    // the loop can never observe a torn list while a new entity is being registered.
    private readonly List<(Type EntityType, RetainDaysAttribute Attr)> _entries = [];
    private readonly Lock _entriesLock = new();

    private readonly string _connectionId;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Lock _startLock = new();

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
        foreach (var t in registeredEntities) Register(t);
    }

    /// <summary>
    /// Adds an entity to the live entry list. Returns true when the type carries
    /// <see cref="RetainDaysAttribute"/> and was not already registered — i.e. when there
    /// is now work that was not there before. Safe to call while the loop is running.
    /// </summary>
    public bool Register(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        var attr = entityType.GetCustomAttribute<RetainDaysAttribute>();
        if (attr is null) return false;

        lock (_entriesLock)
        {
            if (_entries.Any(e => e.EntityType == entityType)) return false;
            _entries.Add((entityType, attr));
        }

        _logger?.Debug($"[PostgreSQL] Retention registered for {entityType.Name} ({attr.Days} day(s)).");
        return true;
    }

    /// <summary>Whether any registered entity has a retention policy to run.</summary>
    public bool HasWork
    {
        get { lock (_entriesLock) return _entries.Count > 0; }
    }

    /// <summary>Point-in-time copy of the entry list, safe to iterate without the lock.</summary>
    private List<(Type EntityType, RetainDaysAttribute Attr)> Snapshot()
    {
        lock (_entriesLock) return [.. _entries];
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
        foreach (var (entityType, attr) in Snapshot())
            removed += await PurgeEntityAsync(entityType, attr, ct).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Starts the background loop. Idempotent — a second call while the loop is running is
    /// a no-op, which is what lets the library call it every time a <c>[RetainDays]</c>
    /// entity is registered. Does nothing while there is no work.
    /// </summary>
    public void Start()
    {
        int count;
        lock (_startLock)
        {
            if (_loop is not null) return;
            count = Snapshot().Count;
            if (count == 0) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
        _logger?.Info($"[PostgreSQL] Retention worker started for {count} entit{(count == 1 ? "y" : "ies")}.");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            // Snapshot per pass: an entity registered mid-pass is picked up on the next one.
            foreach (var (entityType, attr) in Snapshot())
            {
                try
                {
                    await PurgeEntityAsync(entityType, attr, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"[PostgreSQL] Retention purge failed for {entityType.Name}: {ex.Message}");
                }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task<int> PurgeEntityAsync(Type entityType, RetainDaysAttribute attr, CancellationToken ct)
    {
        // Resolve column name via reflection — EntityMetadata<T> isn't reachable without
        // a type parameter, so we do the minimum lookup ourselves.
        var tableAttr = entityType.GetCustomAttribute<TableAttribute>();
        var tableName = !string.IsNullOrEmpty(tableAttr?.Name) ? tableAttr.Name! : entityType.Name;
        var schemaName = !string.IsNullOrEmpty(tableAttr?.Schema)
            ? tableAttr.Schema!
            : PostgreSqlRuntimeOptions.DefaultSchemaFor(_connectionId);

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
            // PostgreSQL has no LIMIT on DELETE. Select the batch by ctid (the physical row
            // pointer, the cheapest possible self-join key) and delete exactly those rows.
            // FOR UPDATE SKIP LOCKED keeps concurrent retention passes from fighting over
            // the same rows instead of one of them blocking.
            var qualified = PostgreSqlDialect.Qualify(schemaName, tableName);
            var sql =
                $"DELETE FROM {qualified} WHERE ctid IN (" +
                $"SELECT ctid FROM {qualified} WHERE {PostgreSqlDialect.Quote(colName)} < @cutoff " +
                $"ORDER BY {PostgreSqlDialect.Quote(colName)} LIMIT {attr.BatchSize} FOR UPDATE SKIP LOCKED)";
            var affected = await _connectionManager.ExecuteWithConnectionAsync(async conn =>
            {
                await using var cmd = conn.CreateCommand(_connectionId);
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
            _logger?.Info($"[PostgreSQL] Retention purge: deleted {totalDeleted} row(s) from `{tableName}` older than {attr.Days} days.");
            QueryCache.Invalidate(tableName);
        }

        return totalDeleted;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
