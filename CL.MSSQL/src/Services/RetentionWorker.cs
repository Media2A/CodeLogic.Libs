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
/// Each purge pass runs a bounded <c>DELETE TOP (@batch)</c> statement.
/// repeatedly until a pass deletes zero rows. That keeps individual transactions small
/// (friendly to SQL Server's transaction log) while still converging on empty.
/// </para>
/// </summary>
public sealed class RetentionWorker : IAsyncDisposable
{
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger? _logger;
    private readonly List<(Type EntityType, RetainDaysAttribute Attr)> _entries;
    private readonly string _connectionId;
    private CancellationTokenSource? _cts;
    private Task? _loop;

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
        _entries = registeredEntities
            .Select(t => (t, t.GetCustomAttribute<RetainDaysAttribute>()))
            .Where(x => x.Item2 is not null)
            .Select(x => (x.t, x.Item2!))
            .ToList();
    }

    /// <summary>Whether any registered entity has a retention policy to run.</summary>
    public bool HasWork => _entries.Count > 0;

    public void Start()
    {
        if (!HasWork || _loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger?.Info($"[MSSQL] Retention worker started for {_entries.Count} entit{(_entries.Count == 1 ? "y" : "ies")}.");
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
            foreach (var (entityType, attr) in _entries)
            {
                try
                {
                    await PurgeEntityAsync(entityType, attr, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Warning($"[MSSQL] Retention purge failed for {entityType.Name}: {ex.Message}");
                }
            }

            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var (entityType, attr) in _entries)
            total += await PurgeEntityAsync(entityType, attr, ct).ConfigureAwait(false);
        return total;
    }

    private async Task<int> PurgeEntityAsync(Type entityType, RetainDaysAttribute attr, CancellationToken ct)
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
            }, _connectionId, ct).ConfigureAwait(false);

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
        if (_cts is not null) _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
