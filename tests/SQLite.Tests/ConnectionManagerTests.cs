using CL.SQLite.Models;
using CL.SQLite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SQLite.Tests;

/// <summary>
/// <see cref="ConnectionManager"/> is a plain public class with no dependency on the
/// CodeLogic runtime — it takes an optional logger and a data directory — so these tests
/// construct it directly against throwaway database files and do NOT join the
/// <c>codelogic</c> collection. Nothing here touches the process-wide runtime singleton.
///
/// Every configuration flag is asserted by its *observable effect* (a PRAGMA read back off
/// the connection, a rejected orphan insert, a blocked acquisition) rather than by reading
/// the property back off the config object.
/// </summary>
public sealed class ConnectionManagerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_cm_" + Guid.NewGuid().ToString("N"));

    public ConnectionManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* Windows may hold the file briefly */ }
    }

    private string Db(string name) => Path.Combine(_dir, name + ".db");

    private static async Task<string> ScalarAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToString(v) ?? "";
    }

    // ── RegisterConfiguration / GetConfiguration / GetDatabasePath / GetConnectionIds ──

    [Fact]
    public void RegisterConfiguration_exposes_the_config_path_and_id()
    {
        using var cm = new ConnectionManager(null, _dir);
        var cfg = new SQLiteDatabaseConfig { DatabasePath = Db("reg") };
        cm.RegisterConfiguration("Default", cfg);

        Assert.Same(cfg, cm.GetConfiguration());
        Assert.Same(cfg, cm.GetConfiguration("Default"));
        Assert.Equal(Db("reg"), cm.GetDatabasePath());
        Assert.Contains("Default", cm.GetConnectionIds());
    }

    [Fact]
    public void RegisterConfiguration_replaces_an_existing_registration_for_the_same_id()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("first") });
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("second") });

        Assert.Equal(Db("second"), cm.GetDatabasePath());
        Assert.Single(cm.GetConnectionIds());
    }

    [Fact]
    public void Connection_ids_are_case_insensitive_and_multiple_databases_coexist()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("a") });
        cm.RegisterConfiguration("Other", new SQLiteDatabaseConfig { DatabasePath = Db("b") });

        Assert.Equal(2, cm.GetConnectionIds().Count());
        Assert.Equal(Db("a"), cm.GetDatabasePath("DEFAULT"));
        Assert.Equal(Db("b"), cm.GetDatabasePath("other"));
    }

    [Fact]
    public void A_relative_database_path_is_resolved_against_the_data_directory()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = "nested/rel.db" });

        Assert.Equal(Path.Combine(_dir, "nested/rel.db"), cm.GetDatabasePath());
        // RegisterConfiguration also creates the containing directory eagerly.
        Assert.True(Directory.Exists(Path.Combine(_dir, "nested")));
    }

    [Fact]
    public void An_unregistered_connection_id_throws_on_every_accessor()
    {
        using var cm = new ConnectionManager(null, _dir);
        Assert.Throws<InvalidOperationException>(() => cm.GetConfiguration("nope"));
        Assert.Throws<InvalidOperationException>(() => cm.GetDatabasePath("nope"));
        Assert.Throws<InvalidOperationException>(() => cm.GetActiveConnectionCount("nope"));
        Assert.Throws<InvalidOperationException>(() => cm.GetPooledConnectionCount("nope"));
    }

    [Fact]
    public async Task GetConnectionAsync_for_an_unregistered_id_throws()
    {
        using var cm = new ConnectionManager(null, _dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cm.GetConnectionAsync("nope"));
    }

    // ── Get / Release and the active + pooled counters ────────────────────────

    [Fact]
    public async Task Acquiring_and_releasing_moves_the_active_and_pooled_counters()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("counters") });

        Assert.Equal(0, cm.GetActiveConnectionCount());
        Assert.Equal(0, cm.GetPooledConnectionCount());

        var conn = await cm.GetConnectionAsync();
        Assert.Equal(System.Data.ConnectionState.Open, conn.State);
        Assert.Equal(1, cm.GetActiveConnectionCount());
        Assert.Equal(0, cm.GetPooledConnectionCount());

        await cm.ReleaseConnectionAsync(conn);
        Assert.Equal(0, cm.GetActiveConnectionCount());
        Assert.Equal(1, cm.GetPooledConnectionCount());
    }

    [Fact]
    public async Task A_released_connection_is_handed_back_out_rather_than_reopened()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("reuse") });

        var first = await cm.GetConnectionAsync();
        await cm.ReleaseConnectionAsync(first);

        var second = await cm.GetConnectionAsync();
        Assert.Same(first, second);
        Assert.Equal(0, cm.GetPooledConnectionCount());
        await cm.ReleaseConnectionAsync(second);
    }

    [Fact]
    public async Task Releasing_a_null_connection_is_a_no_op()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("null") });

        await cm.ReleaseConnectionAsync(null!);
        Assert.Equal(0, cm.GetActiveConnectionCount());
        Assert.Equal(0, cm.GetPooledConnectionCount());
    }

    // ── MaxPoolSize actually caps live connections ────────────────────────────

    [Fact]
    public async Task MaxPoolSize_caps_the_number_of_simultaneously_live_connections()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("pool1"), MaxPoolSize = 1 });

        var held = await cm.GetConnectionAsync();
        Assert.Equal(1, cm.GetActiveConnectionCount());

        // The second acquisition must block on the gate; prove it by letting it time out.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cm.GetConnectionAsync("Default", cts.Token));

        // Releasing the first frees the slot, and the next acquisition succeeds promptly.
        await cm.ReleaseConnectionAsync(held);
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var second = await cm.GetConnectionAsync("Default", cts2.Token);
        Assert.NotNull(second);
        await cm.ReleaseConnectionAsync(second);
    }

    [Fact]
    public async Task MaxPoolSize_two_lets_two_connections_be_held_at_once()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("pool2"), MaxPoolSize = 2 });

        var a = await cm.GetConnectionAsync();
        var b = await cm.GetConnectionAsync();
        Assert.NotSame(a, b);
        Assert.Equal(2, cm.GetActiveConnectionCount());

        await cm.ReleaseConnectionAsync(a);
        await cm.ReleaseConnectionAsync(b);
        Assert.Equal(2, cm.GetPooledConnectionCount());
    }

    [Fact]
    public async Task A_cancelled_acquisition_does_not_leak_its_pool_slot()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("noleak"), MaxPoolSize = 1 });

        var held = await cm.GetConnectionAsync();
        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cm.GetConnectionAsync("Default", cts.Token));
        }
        await cm.ReleaseConnectionAsync(held);

        // If the failed acquisitions had leaked permits, this would deadlock.
        using var ok = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var again = await cm.GetConnectionAsync("Default", ok.Token);
        await cm.ReleaseConnectionAsync(again);
    }

    // ── UseWAL takes effect on the file ───────────────────────────────────────

    [Fact]
    public async Task UseWAL_true_puts_the_database_into_wal_journal_mode()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("wal_on"), UseWAL = true });

        await cm.ExecuteAsync(async conn =>
            Assert.Equal("wal", (await ScalarAsync(conn, "PRAGMA journal_mode;")).ToLowerInvariant()));
    }

    [Fact]
    public async Task UseWAL_false_leaves_the_default_delete_journal_mode()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("wal_off"), UseWAL = false });

        await cm.ExecuteAsync(async conn =>
            Assert.Equal("delete", (await ScalarAsync(conn, "PRAGMA journal_mode;")).ToLowerInvariant()));
    }

    [Fact]
    public async Task WAL_mode_is_persisted_in_the_file_so_a_sidecar_wal_appears_after_a_write()
    {
        var path = Db("wal_file");
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = path, UseWAL = true });

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        });

        Assert.True(File.Exists(path + "-wal"), "a -wal sidecar should exist while the WAL database is open");
    }

    // ── EnableForeignKeys is actually enforced ────────────────────────────────

    [Fact]
    public async Task EnableForeignKeys_true_rejects_an_orphan_insert()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("fk_on"), EnableForeignKeys = true });

        await cm.ExecuteAsync(async conn =>
        {
            Assert.Equal("1", await ScalarAsync(conn, "PRAGMA foreign_keys;"));

            await using var ddl = conn.CreateCommand();
            ddl.CommandText = """
                CREATE TABLE parent(id INTEGER PRIMARY KEY);
                CREATE TABLE child(id INTEGER PRIMARY KEY,
                                   parent_id INTEGER REFERENCES parent(id));
                """;
            await ddl.ExecuteNonQueryAsync();

            await using var bad = conn.CreateCommand();
            bad.CommandText = "INSERT INTO child(id, parent_id) VALUES (1, 999);";
            var ex = await Assert.ThrowsAsync<SqliteException>(() => bad.ExecuteNonQueryAsync());
            Assert.Contains("FOREIGN KEY", ex.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task EnableForeignKeys_false_lets_the_same_orphan_insert_through()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("fk_off"), EnableForeignKeys = false });

        await cm.ExecuteAsync(async conn =>
        {
            Assert.Equal("0", await ScalarAsync(conn, "PRAGMA foreign_keys;"));

            await using var ddl = conn.CreateCommand();
            ddl.CommandText = """
                CREATE TABLE parent(id INTEGER PRIMARY KEY);
                CREATE TABLE child(id INTEGER PRIMARY KEY,
                                   parent_id INTEGER REFERENCES parent(id));
                INSERT INTO child(id, parent_id) VALUES (1, 999);
                """;
            await ddl.ExecuteNonQueryAsync();

            Assert.Equal("1", await ScalarAsync(conn, "SELECT COUNT(*) FROM child;"));
        });
    }

    /// <summary>
    /// The default is <c>true</c>, and a default configuration still enforces constraints:
    /// <c>PRAGMA foreign_keys=ON</c> is sent explicitly, matching what Microsoft.Data.Sqlite
    /// does on its own.
    /// </summary>
    [Fact]
    public async Task Foreign_keys_are_enforced_by_default()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("fk_default") });

        await cm.ExecuteAsync(async conn =>
        {
            Assert.Equal("1", await ScalarAsync(conn, "PRAGMA foreign_keys;"));

            await using var ddl = conn.CreateCommand();
            ddl.CommandText = """
                CREATE TABLE parent(id INTEGER PRIMARY KEY);
                CREATE TABLE child(id INTEGER PRIMARY KEY,
                                   parent_id INTEGER REFERENCES parent(id));
                """;
            await ddl.ExecuteNonQueryAsync();

            await using var bad = conn.CreateCommand();
            bad.CommandText = "INSERT INTO child(id, parent_id) VALUES (1, 999);";
            await Assert.ThrowsAsync<SqliteException>(() => bad.ExecuteNonQueryAsync());
        });
    }

    [Fact]
    public async Task ON_DELETE_CASCADE_fires_when_foreign_keys_are_enabled()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("fk_cascade"), EnableForeignKeys = true });

        await cm.ExecuteAsync(async conn =>
        {
            await using var ddl = conn.CreateCommand();
            ddl.CommandText = """
                CREATE TABLE p(id INTEGER PRIMARY KEY);
                CREATE TABLE c(id INTEGER PRIMARY KEY,
                               pid INTEGER REFERENCES p(id) ON DELETE CASCADE);
                INSERT INTO p(id) VALUES (1);
                INSERT INTO c(id, pid) VALUES (1, 1);
                DELETE FROM p WHERE id = 1;
                """;
            await ddl.ExecuteNonQueryAsync();

            Assert.Equal("0", await ScalarAsync(conn, "SELECT COUNT(*) FROM c;"));
        });
    }

    // ── CacheMode ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CacheMode.Default)]
    [InlineData(CacheMode.Private)]
    [InlineData(CacheMode.Shared)]
    public async Task Every_CacheMode_still_opens_a_usable_read_write_connection(CacheMode mode)
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("cache_" + mode), CacheMode = mode });

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (7); SELECT x FROM t;";
            Assert.Equal(7L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        });
    }

    // ── ExecuteAsync overloads ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_with_a_result_returns_the_value_and_releases_the_connection()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("exec_t") });

        var answer = await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 42;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        });

        Assert.Equal(42L, answer);
        Assert.Equal(0, cm.GetActiveConnectionCount());
        Assert.Equal(1, cm.GetPooledConnectionCount());
    }

    [Fact]
    public async Task ExecuteAsync_void_overload_releases_the_connection_even_when_the_action_throws()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("exec_throw"), MaxPoolSize = 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cm.ExecuteAsync(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(0, cm.GetActiveConnectionCount());

        // With MaxPoolSize 1 this would hang forever if the slot had leaked.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var conn = await cm.GetConnectionAsync("Default", cts.Token);
        await cm.ReleaseConnectionAsync(conn);
    }

    // ── TestConnectionAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task TestConnectionAsync_is_true_for_a_registered_database()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("test_ok") });
        Assert.True(await cm.TestConnectionAsync());
    }

    [Fact]
    public async Task TestConnectionAsync_is_false_for_an_unregistered_id_rather_than_throwing()
    {
        using var cm = new ConnectionManager(null, _dir);
        Assert.False(await cm.TestConnectionAsync("missing"));
    }

    [Fact]
    public async Task TestConnectionAsync_is_false_when_the_file_cannot_be_opened()
    {
        using var cm = new ConnectionManager(null, _dir);
        // A directory is not a database file; opening it must fail rather than throw out.
        var asDirectory = Path.Combine(_dir, "iam_a_directory");
        Directory.CreateDirectory(asDirectory);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = asDirectory });

        Assert.False(await cm.TestConnectionAsync());
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_closes_pooled_connections_and_further_acquisition_throws()
    {
        var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("disposed") });

        var conn = await cm.GetConnectionAsync();
        await cm.ReleaseConnectionAsync(conn);
        cm.Dispose();

        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
        Assert.Empty(cm.GetConnectionIds());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cm.GetConnectionAsync());

        cm.Dispose(); // idempotent
    }

    // ── Concurrency against a WAL database ────────────────────────────────────

    [Fact]
    public async Task Concurrent_writers_against_a_WAL_database_all_commit()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("concurrent"), UseWAL = true, MaxPoolSize = 8 });

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE n(v INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        });

        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            for (var j = 0; j < 10; j++)
            {
                await cm.ExecuteAsync(async conn =>
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO n(v) VALUES (@v);";
                    cmd.Parameters.AddWithValue("@v", i * 100 + j);
                    await cmd.ExecuteNonQueryAsync();
                });
            }
        }));
        await Task.WhenAll(writers);

        var total = await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM n;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        });
        Assert.Equal(80L, total);
        Assert.Equal(0, cm.GetActiveConnectionCount());
    }

    [Fact]
    public async Task Concurrent_readers_and_a_writer_coexist_under_WAL()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default",
            new SQLiteDatabaseConfig { DatabasePath = Db("rw"), UseWAL = true, MaxPoolSize = 6 });

        await cm.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE n(v INTEGER); INSERT INTO n VALUES (1);";
            await cmd.ExecuteNonQueryAsync();
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var j = 0; j < 25; j++)
                await cm.ExecuteAsync(async conn =>
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM n;";
                    await cmd.ExecuteScalarAsync();
                });
        })).ToList();

        var writer = Task.Run(async () =>
        {
            for (var j = 0; j < 25; j++)
                await cm.ExecuteAsync(async conn =>
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT INTO n(v) VALUES (2);";
                    await cmd.ExecuteNonQueryAsync();
                });
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, cm.GetActiveConnectionCount());
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rolled_back_transaction_on_a_managed_connection_leaves_no_rows()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("tx") });

        await cm.ExecuteAsync(async conn =>
        {
            await using var ddl = conn.CreateCommand();
            ddl.CommandText = "CREATE TABLE t(v INTEGER);";
            await ddl.ExecuteNonQueryAsync();

            await using var tx = await conn.BeginTransactionAsync();
            await using var ins = conn.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText = "INSERT INTO t VALUES (1);";
            await ins.ExecuteNonQueryAsync();
            await tx.RollbackAsync();

            Assert.Equal("0", await ScalarAsync(conn, "SELECT COUNT(*) FROM t;"));
        });
    }

    [Fact]
    public async Task A_committed_transaction_survives_the_connection_being_recycled()
    {
        using var cm = new ConnectionManager(null, _dir);
        cm.RegisterConfiguration("Default", new SQLiteDatabaseConfig { DatabasePath = Db("tx_commit") });

        await cm.ExecuteAsync(async conn =>
        {
            await using var ddl = conn.CreateCommand();
            ddl.CommandText = "CREATE TABLE t(v INTEGER);";
            await ddl.ExecuteNonQueryAsync();

            await using var tx = await conn.BeginTransactionAsync();
            await using var ins = conn.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText = "INSERT INTO t VALUES (1), (2);";
            await ins.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        });

        var count = await cm.ExecuteAsync(async conn =>
            Convert.ToInt64(await ScalarAsync(conn, "SELECT COUNT(*) FROM t;")));
        Assert.Equal(2L, count);
    }
}
