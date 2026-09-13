using CL.PostgreSQL.Core;

namespace CL.PostgreSQL.Services;

/// <summary>
/// Best-effort <c>EXPLAIN (FORMAT JSON)</c> capture for slow queries, gated on the
/// per-database <c>CaptureExplainOnSlowQuery</c> flag (off by default).
/// <para>
/// Rules this type exists to guarantee:
/// <list type="bullet">
///   <item>Nothing runs unless a connection actually enabled the flag — the check in front
///     is a single volatile bool read.</item>
///   <item>It never runs inside the caller's transaction scope. A plan fetched on the
///     scope's own connection would interleave with the caller's work; instead the whole
///     capture is skipped for transactional queries.</item>
///   <item>It never throws into the query path, and never delays it: the fetch happens on
///     a detached task, and <see cref="Events.SlowQueryEvent"/> is published either way —
///     with a null payload when the plan could not be obtained.</item>
///   <item>Statements <c>EXPLAIN</c> cannot accept (DDL, utility commands) and
///     parameterized statements whose parameter values were not captured are skipped
///     rather than failing.</item>
/// </list>
/// </para>
/// </summary>
internal static class SlowQueryExplain
{
    /// <summary>Upper bound on how long a plan fetch may take before it is abandoned.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Publishes the slow-query event, attaching an execution plan when capture is enabled
    /// and the statement is explainable.
    /// </summary>
    /// <param name="connectionManager">Used to fetch the plan on a separate connection.</param>
    /// <param name="connectionId">Connection the slow statement ran on.</param>
    /// <param name="sql">The statement text.</param>
    /// <param name="parameters">Its bound parameters, or null when they were not captured.</param>
    /// <param name="elapsedMs">Measured duration of the original execution.</param>
    /// <param name="insideTransaction">True when the caller holds a transaction scope.</param>
    public static void Record(
        ConnectionManager? connectionManager,
        string connectionId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        long elapsedMs,
        bool insideTransaction)
    {
        if (!ShouldCapture(connectionManager, connectionId, sql, parameters, insideTransaction))
        {
            QueryObservability.RecordSlow(connectionId, sql, elapsedMs);
            return;
        }

        // Snapshot the parameters: the caller's dictionary may be mutated (or the command
        // disposed) long before the detached fetch runs.
        var snapshot = parameters is null or { Count: 0 }
            ? null
            : new Dictionary<string, object?>(parameters);

        _ = Task.Run(async () =>
        {
            string? explainJson = null;
            try
            {
                explainJson = await FetchAsync(connectionManager!, connectionId, sql, snapshot)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Strictly best-effort: a plan we could not fetch is simply not attached.
            }

            QueryObservability.RecordSlow(connectionId, sql, elapsedMs, explainJson);
        });
    }

    private static bool ShouldCapture(
        ConnectionManager? connectionManager,
        string connectionId,
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        bool insideTransaction)
    {
        if (!PostgreSqlRuntimeOptions.AnyExplainEnabled) return false;
        if (connectionManager is null || insideTransaction) return false;
        if (!PostgreSqlRuntimeOptions.For(connectionId).CaptureExplainOnSlowQuery) return false;
        return IsExplainable(sql, parameters);
    }

    /// <summary>
    /// True when PostgreSQL will accept the statement after an <c>EXPLAIN</c> prefix.
    /// Only the optimizable statement classes qualify; a parameterized statement also
    /// needs its values, since EXPLAIN binds them like any other execution.
    /// </summary>
    internal static bool IsExplainable(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;

        var trimmed = sql.TrimStart();
        var explainable =
            trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("VALUES", StringComparison.OrdinalIgnoreCase);
        if (!explainable) return false;

        // Multi-statement batches (the batched INSERT path) cannot be explained as one unit.
        if (trimmed.TrimEnd().TrimEnd(';').Contains(';', StringComparison.Ordinal)) return false;

        // A statement with placeholders but no captured values would fail to bind.
        if ((parameters is null || parameters.Count == 0) &&
            sql.Contains('@', StringComparison.Ordinal)) return false;

        return true;
    }

    private static async Task<string?> FetchAsync(
        ConnectionManager connectionManager,
        string connectionId,
        string sql,
        Dictionary<string, object?>? parameters)
    {
        using var cts = new CancellationTokenSource(FetchTimeout);

        return await connectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand(connectionId);
            cmd.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
            if (parameters is not null)
            {
                foreach (var kv in parameters)
                    cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
            }

            var raw = await cmd.ExecuteScalarAsync(cts.Token).ConfigureAwait(false);
            return raw is null or DBNull ? null : raw.ToString();
        }, connectionId, cts.Token).ConfigureAwait(false);
    }
}
