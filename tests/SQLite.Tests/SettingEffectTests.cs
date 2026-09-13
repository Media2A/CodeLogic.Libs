using CL.SQLite.Models;
using CL.SQLite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SQLite.Tests;

[SQLiteTable("skip_sync_probe")]
public sealed class SkipSyncProbe
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "v", DataType = SQLiteDataType.TEXT)]
    public string V { get; set; } = "";
}

/// <summary>
/// Each configuration setting that claims to do something, checked by its observable effect.
/// <see cref="ConnectionManagerTests"/> already covers the three that work (UseWAL,
/// EnableForeignKeys in the ON direction, MaxPoolSize); this file covers the rest, including
/// the ones that turn out to have no effect at all. Standalone — no CodeLogic runtime needed.
/// </summary>
public sealed class SettingEffectTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_cfg_" + Guid.NewGuid().ToString("N"));

    public SettingEffectTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ConnectionManager Manager(SQLiteDatabaseConfig config, string name = "Default")
    {
        var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration(name, config);
        return cm;
    }

    // ── DatabasePath ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DatabasePath_decides_which_file_is_written()
    {
        var path = Path.Combine(_dir, "named.db");
        using var cm = Manager(new SQLiteDatabaseConfig { DatabasePath = path });

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Two_databases_with_different_paths_do_not_share_tables()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("A", new SQLiteDatabaseConfig { DatabasePath = Path.Combine(_dir, "a.db") });
        cm.RegisterConfiguration("B", new SQLiteDatabaseConfig { DatabasePath = Path.Combine(_dir, "b.db") });

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE only_in_a(x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }, "A");

        var existsInB = await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='only_in_a';";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }, "B");

        Assert.Equal(0L, existsInB);
    }

    // ── ConnectionTimeoutSeconds ─────────────────────────────────────────────

    /// <summary>
    /// <c>ConnectionTimeoutSeconds</c> is applied as the connection string's
    /// <c>Default Timeout</c>, which is what <c>SqliteConnection.DefaultTimeout</c> reports.
    /// </summary>
    [Fact]
    public async Task ConnectionTimeoutSeconds_reaches_the_connection()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "ct.db"),
            ConnectionTimeoutSeconds = 7
        });

        await cm.ExecuteAsync(conn =>
        {
            Assert.Equal(7, conn.DefaultTimeout);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The default is 30 seconds, which is also Microsoft.Data.Sqlite's own default, so a
    /// default configuration behaves exactly as it did before the setting was wired.
    /// </summary>
    [Fact]
    public async Task A_default_configuration_still_gets_the_providers_own_thirty_second_timeout()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "ct_default.db")
        });

        await cm.ExecuteAsync(conn =>
        {
            Assert.Equal(30, conn.DefaultTimeout);
            using var cmd = conn.CreateCommand();
            Assert.Equal(30, cmd.CommandTimeout);
            return Task.CompletedTask;
        });
    }

    // ── CommandTimeoutSeconds (no effect) ────────────────────────────────────

    /// <summary>
    /// <c>CommandTimeoutSeconds</c> is still never read, and deliberately so. Microsoft.Data.Sqlite
    /// has a single timeout knob: a command created from a connection takes its
    /// <c>CommandTimeout</c> from that connection's <c>DefaultTimeout</c>, which is the very
    /// property <c>ConnectionTimeoutSeconds</c> sets. Honouring this setting on a command the
    /// caller creates would mean driving <c>DefaultTimeout</c> from it instead, contradicting
    /// <see cref="ConnectionTimeoutSeconds_reaches_the_connection"/>. Its declared default is
    /// also 120, not the provider's 30, so wiring it would change default behaviour.
    /// </summary>
    [Fact(Skip = "blocked by SQLite having one timeout knob, not two: SqliteCommand.CommandTimeout is inherited from SqliteConnection.DefaultTimeout, which ConnectionTimeoutSeconds already owns — and this setting's default of 120 does not reproduce today's 30")]
    public async Task CommandTimeoutSeconds_reaches_the_commands()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "cmdt.db"),
            CommandTimeoutSeconds = 11
        });

        await cm.ExecuteAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            Assert.Equal(11, cmd.CommandTimeout);
            return Task.CompletedTask;
        });
    }

    /// <summary>Standing record of the above: the setting has no observable effect.</summary>
    [Fact]
    public async Task CommandTimeoutSeconds_is_currently_ignored()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "cmdt_actual.db"),
            CommandTimeoutSeconds = 11
        });

        await cm.ExecuteAsync(conn =>
        {
            using var cmd = conn.CreateCommand();
            Assert.Equal(30, cmd.CommandTimeout); // the provider default, not the configured 11
            return Task.CompletedTask;
        });
    }

    // ── CacheMode ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CacheMode_Shared_puts_Cache_Shared_into_the_connection_string()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "shared.db"),
            CacheMode = CacheMode.Shared
        });

        await cm.ExecuteAsync(conn =>
        {
            Assert.Contains("Cache=Shared", conn.ConnectionString, StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// <c>CacheMode.Private</c> now requests <c>SqliteCacheMode.Private</c>, so it is
    /// distinguishable from <c>CacheMode.Default</c>, which leaves the provider's choice alone.
    /// </summary>
    [Fact]
    public async Task CacheMode_Private_differs_from_CacheMode_Default()
    {
        using var priv = Manager(new SQLiteDatabaseConfig
        { DatabasePath = Path.Combine(_dir, "p.db"), CacheMode = CacheMode.Private }, "P");
        using var def = Manager(new SQLiteDatabaseConfig
        { DatabasePath = Path.Combine(_dir, "p.db"), CacheMode = CacheMode.Default }, "D");

        var a = await priv.ExecuteAsync(conn => Task.FromResult(conn.ConnectionString), "P");
        var b = await def.ExecuteAsync(conn => Task.FromResult(conn.ConnectionString), "D");
        Assert.NotEqual(b, a);
    }

    [Fact]
    public async Task CacheMode_Private_names_itself_in_the_connection_string_and_Default_does_not()
    {
        using var cm = new ConnectionManager(null, _dir);
        var path = Path.Combine(_dir, "same.db");
        cm.RegisterConfiguration("P", new SQLiteDatabaseConfig { DatabasePath = path, CacheMode = CacheMode.Private });
        cm.RegisterConfiguration("D", new SQLiteDatabaseConfig { DatabasePath = path, CacheMode = CacheMode.Default });

        var a = await cm.ExecuteAsync(conn => Task.FromResult(conn.ConnectionString), "P");
        var b = await cm.ExecuteAsync(conn => Task.FromResult(conn.ConnectionString), "D");

        Assert.Contains("Cache=Private", a, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cache=", b, StringComparison.OrdinalIgnoreCase);
    }

    // ── SkipTableSync ────────────────────────────────────────────────────────

    /// <summary>
    /// <c>SkipTableSync</c> turns the sync off for a database: the call succeeds, says so, and
    /// touches nothing.
    /// </summary>
    [Fact]
    public async Task SkipTableSync_stops_the_table_from_being_created()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "skip.db"),
            SkipTableSync = true
        });
        var sync = new TableSyncService(cm, _dir);

        var result = await sync.SyncTableAsync<SkipSyncProbe>();

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Contains("skipped", result.Value!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await TableExists(cm, "skip_sync_probe"));
    }

    /// <summary>The default is <c>false</c>, so an unconfigured database still syncs.</summary>
    [Fact]
    public async Task SkipTableSync_defaults_to_off_and_the_table_is_created()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "skip_default.db")
        });
        var sync = new TableSyncService(cm, _dir);

        var result = await sync.SyncTableAsync<SkipSyncProbe>();
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.True(await TableExists(cm, "skip_sync_probe"));
    }

    // ── SlowQueryThresholdMs ─────────────────────────────────────────────────

    /// <summary>
    /// <c>SlowQueryThresholdMs</c> is plumbed from configuration into
    /// <c>Repository</c>/<c>QueryBuilder</c>, but the only thing it gates is a logger warning:
    /// there is no event, no counter, and no <c>Result</c> metadata, so a threshold of 0 is
    /// observationally identical to a threshold of a minute. All that can be asserted from a
    /// test is that neither extreme changes the outcome of a query.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(60_000)]
    public async Task SlowQueryThresholdMs_never_changes_the_result_of_a_query(int threshold)
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, $"slow_{threshold}.db")
        });
        var sync = new TableSyncService(cm, _dir);
        Assert.True((await sync.SyncTableAsync<SkipSyncProbe>()).IsSuccess);

        var repo = new Repository<SkipSyncProbe>(cm, null, "Default", threshold);
        Assert.True((await repo.InsertAsync(new SkipSyncProbe { V = "x" })).IsSuccess);

        var count = await new QueryBuilder<SkipSyncProbe>(cm, null, "Default", threshold).CountAsync();
        Assert.True(count.IsSuccess, count.Error?.ToString());
        Assert.Equal(1, count.Value);
    }

    // ── MaxPoolSize interaction with the pooled-count cap ────────────────────

    [Fact]
    public async Task MaxPoolSize_also_caps_how_many_connections_are_kept_for_reuse()
    {
        using var cm = Manager(new SQLiteDatabaseConfig
        {
            DatabasePath = Path.Combine(_dir, "poolcap.db"),
            MaxPoolSize = 3
        });

        var held = new List<SqliteConnection>();
        for (var i = 0; i < 3; i++) held.Add(await cm.GetConnectionAsync());
        foreach (var c in held) await cm.ReleaseConnectionAsync(c);

        Assert.Equal(3, cm.GetPooledConnectionCount());
        Assert.Equal(0, cm.GetActiveConnectionCount());
    }

    private static Task<bool> TableExists(ConnectionManager cm, string table) =>
        cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n;";
            cmd.Parameters.AddWithValue("@n", table);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
        });
}

/// <summary>
/// The public model types in <c>CL.SQLite.Models.QueryModels</c> — and the internal
/// <c>WhereClauseBuilder</c> that consumes <see cref="WhereCondition"/> — have no caller
/// anywhere in CL.SQLite: the query path uses <c>SQLiteExpressionVisitor</c> instead. They are
/// still part of the published surface, so their shape is pinned here.
/// </summary>
public sealed class QueryModelTests
{
    [Fact]
    public void SQLiteQuery_carries_a_query_string_and_a_parameter_bag()
    {
        var q = new SQLiteQuery { QueryString = "SELECT 1" };
        Assert.Equal("SELECT 1", q.QueryString);
        Assert.Empty(q.Parameters);

        var withParams = q with { Parameters = new Dictionary<string, object?> { ["@a"] = 1 } };
        Assert.Single(withParams.Parameters);
        Assert.NotEqual(q, withParams);
    }

    [Fact]
    public void TableSyncResult_has_succeeded_and_failed_factories()
    {
        var ok = TableSyncResult.Succeeded("done");
        Assert.True(ok.Success);
        Assert.Equal("done", ok.Message);
        Assert.Null(ok.Exception);

        var ex = new InvalidOperationException("nope");
        var bad = TableSyncResult.Failed("broken", ex);
        Assert.False(bad.Success);
        Assert.Equal("broken", bad.Message);
        Assert.Same(ex, bad.Exception);
    }

    [Fact]
    public void WhereCondition_defaults_its_logical_operator_to_AND()
    {
        var c = new WhereCondition { Column = "a", Operator = "=", Value = 1 };
        Assert.Equal("AND", c.LogicalOperator);
    }

    [Fact]
    public void OrderByClause_and_AggregateFunction_hold_what_they_are_given()
    {
        var order = new OrderByClause { Column = "a", Order = SortOrder.Desc };
        Assert.Equal(SortOrder.Desc, order.Order);

        var agg = new AggregateFunction { Type = AggregateType.Sum, Column = "n", Alias = "total" };
        Assert.Equal(AggregateType.Sum, agg.Type);
        Assert.Equal("total", agg.Alias);
    }

    [Fact]
    public void The_supporting_enums_expose_the_documented_members()
    {
        Assert.Equal([SortOrder.Asc, SortOrder.Desc], Enum.GetValues<SortOrder>());
        Assert.Equal(
            [AggregateType.Sum, AggregateType.Avg, AggregateType.Min, AggregateType.Max, AggregateType.Count],
            Enum.GetValues<AggregateType>());
        Assert.Equal(
            [TransactionIsolation.Deferred, TransactionIsolation.Immediate, TransactionIsolation.Exclusive],
            Enum.GetValues<TransactionIsolation>());
        Assert.Equal([CacheMode.Default, CacheMode.Private, CacheMode.Shared], Enum.GetValues<CacheMode>());
    }

    /// <summary>
    /// <see cref="TransactionIsolation"/> is declared but no CL.SQLite API accepts it — the
    /// library exposes no transaction entry point at all, so callers must reach for
    /// <c>ConnectionManager.ExecuteAsync</c> and open a transaction on the raw connection.
    /// </summary>
    [Fact]
    public void No_public_CL_SQLite_member_accepts_a_TransactionIsolation()
    {
        var consumers = typeof(SQLiteConfig).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(TransactionIsolation)))
            .ToList();

        Assert.Empty(consumers);
    }
}
