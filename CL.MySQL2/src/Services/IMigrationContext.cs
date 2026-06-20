using CL.MySQL2.Core;
using CodeLogic.Core.Logging;
using MySqlConnector;

namespace CL.MySQL2.Services;

/// <summary>
/// The surface available to an <see cref="Models.IMigration"/> while it runs. Everything here
/// executes on the migration's own connection and transaction (DDL excepted — MySQL auto-commits
/// it). Provides raw SQL helpers plus a bridge into the declarative schema sync so a migration can
/// bring a table to its current model shape and then transform data in the same step.
/// </summary>
public interface IMigrationContext
{
    /// <summary>The migration's open connection.</summary>
    MySqlConnection Connection { get; }

    /// <summary>The migration's open transaction.</summary>
    MySqlTransaction Transaction { get; }

    /// <summary>Runs a non-query statement (INSERT/UPDATE/DELETE/DDL) and returns affected rows.</summary>
    Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default);

    /// <summary>Runs a query and materializes each row into <typeparamref name="T"/>.</summary>
    Task<List<T>> QueryAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default) where T : class, new();

    /// <summary>Runs a query returning a single scalar (first column of the first row).</summary>
    Task<T?> ScalarAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Brings the table for <typeparamref name="T"/> to its current model shape via the schema
    /// analyzer (CREATE if missing, otherwise additive ALTERs). Lets a migration add only the data
    /// transform around it. Note: this issues DDL, which MySQL commits immediately.
    /// </summary>
    Task SyncTableAsync<T>(CancellationToken ct = default) where T : class;
}

/// <summary>
/// Default <see cref="IMigrationContext"/> backed by a <see cref="TransactionScope"/> and a
/// <see cref="SchemaAnalyzer"/>.
/// </summary>
internal sealed class MigrationContext : IMigrationContext
{
    private readonly TransactionScope _scope;
    private readonly SchemaAnalyzer _analyzer;
    private readonly ILogger? _logger;

    public MigrationContext(TransactionScope scope, SchemaAnalyzer analyzer, ILogger? logger)
    {
        _scope = scope;
        _analyzer = analyzer;
        _logger = logger;
    }

    public MySqlConnection Connection => _scope.Connection;
    public MySqlTransaction Transaction => _scope.Transaction;

    public async Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        await using var cmd = NewCommand(sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<T>> QueryAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default) where T : class, new()
    {
        await using var cmd = NewCommand(sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var map = EntityMetadata<T>.Materializer.CompileForReader(reader);
        var items = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) items.Add(map(reader));
        return items;
    }

    public async Task<T?> ScalarAsync<T>(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        await using var cmd = NewCommand(sql, parameters);
        var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (raw is null or DBNull) return default;
        return (T)Convert.ChangeType(raw, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T))!;
    }

    public async Task SyncTableAsync<T>(CancellationToken ct = default) where T : class
    {
        var entityType = typeof(T);
        var tableName = SchemaAnalyzer.GetTableName(entityType);

        var exists = await TableExistsAsync(tableName, ct).ConfigureAwait(false);
        if (!exists)
        {
            await ExecuteAsync(_analyzer.GenerateCreateTable(entityType), ct: ct).ConfigureAwait(false);
            _logger?.Info($"[MySQL2] (migration) created table `{tableName}`");
            return;
        }

        var alters = await _analyzer
            .GenerateAlterStatementsAsync(entityType, Connection, Models.SchemaSyncLevel.Safe, ct)
            .ConfigureAwait(false);
        foreach (var stmt in alters)
        {
            await ExecuteAsync(stmt, ct: ct).ConfigureAwait(false);
            _logger?.Debug($"[MySQL2] (migration) {stmt}");
        }
    }

    private async Task<bool> TableExistsAsync(string tableName, CancellationToken ct)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = @"SELECT COUNT(*) FROM information_schema.TABLES
                            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tbl";
        cmd.Parameters.AddWithValue("@tbl", tableName);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private MySqlCommand NewCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = sql;
        if (parameters is not null)
            foreach (var kv in parameters)
                cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        return cmd;
    }
}
