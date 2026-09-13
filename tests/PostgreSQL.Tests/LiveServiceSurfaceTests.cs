using CL.PostgreSQL;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// The remaining service-level entry points, plus the transient-retry path — which only
// runs when the server actually raises a serialization failure or a deadlock, so it needs
// two transactions deliberately contending.

[Table(Name = "it_svc", Schema = "public")]
public sealed class SvcRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "grp", NotNull = true)] public long Grp { get; set; }
    [Column(Name = "label", Size = 40, NotNull = true)] public string Label { get; set; } = "";
}

[Table(Name = "it_svc_side", Schema = "public")]
public sealed class SvcSide
{
    [Column(Name = "id", Primary = true)] public long Id { get; set; }
    [Column(Name = "title", Size = 40, NotNull = true)] public string Title { get; set; } = "";
}

public sealed class SvcJoined
{
    [Column(Name = "label")] public string Label { get; set; } = "";
    [Column(Name = "title")] public string Title { get; set; } = "";
}

[Collection("codelogic")]
public sealed class LiveServiceSurfaceTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveServiceSurfaceTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    private async Task<PostgreSQLLibrary> FreshAsync()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_svc\" CASCADE");
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_svc_side\" CASCADE");
        Assert.True((await lib.SyncTableAsync<SvcRow>(createBackup: false)).IsSuccess);
        Assert.True((await lib.SyncTableAsync<SvcSide>(createBackup: false)).IsSuccess);
        return lib;
    }

    // ── TableSyncService ─────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task TableSyncService_accessors_and_batch_sync()
    {
        var lib = await FreshAsync();
        var sync = lib.TableSync;

        Assert.NotNull(sync.GetBackupManager());
        Assert.NotNull(sync.GetMigrationTracker());
        Assert.NotNull(sync.GetSchemaStateStore());

        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_svc\" CASCADE");
        var result = await sync.SyncTablesAsync([typeof(SvcRow), typeof(SvcSide)]);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(2, result.Value!.Count);
        Assert.All(result.Value.Values, r => Assert.True(r.Success, string.Join("; ", r.Errors)));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task GenerateAlterStatements_reports_a_pending_change()
    {
        var lib = await FreshAsync();
        await lib.ExecuteSqlAsync("ALTER TABLE public.it_svc DROP COLUMN label");

        var analyzer = new SchemaAnalyzer();
        var statements = await lib.ConnectionManager.ExecuteWithConnectionAsync(
            conn => analyzer.GenerateAlterStatementsAsync(typeof(SvcRow), conn, SchemaSyncLevel.Safe));

        _out.WriteLine(string.Join("\n", statements));
        Assert.Contains(statements, s => s.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase)
                                      && s.Contains("label", StringComparison.OrdinalIgnoreCase));
    }

    // ── SchemaStateStore ─────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task SchemaStateStore_round_trips_a_sentinel_row()
    {
        var lib = await FreshAsync();
        var store = lib.SchemaState;

        Assert.True(await store.EnsureStateTableAsync());

        const string key = "public.it_svc_probe";
        await store.RemoveStateAsync(key);

        Assert.True(await store.UpsertStateAsync(
            key, "deadbeef", SchemaSyncStatus.Synced, "Production", "9.9.9", "model-info"));

        var record = await store.GetStateAsync(key);
        Assert.NotNull(record);
        Assert.Equal("deadbeef", record!.SchemaCrc);
        Assert.Equal(SchemaSyncStatus.Synced, record.Status);
        Assert.Equal("9.9.9", record.AppVersion);

        // Upsert must update in place rather than duplicate.
        Assert.True(await store.UpsertStateAsync(
            key, "cafebabe", SchemaSyncStatus.Synced, "Production", "9.9.9", "model-info"));
        Assert.Equal("cafebabe", (await store.GetStateAsync(key))!.SchemaCrc);

        var all = await store.GetAllAsync();
        Assert.Contains(all, r => r.TableName == key);

        Assert.True(await store.RemoveStateAsync(key));
        Assert.Null(await store.GetStateAsync(key));
    }

    // ── MigrationTracker ─────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task MigrationTracker_records_and_removes()
    {
        var lib = Lib;
        var tracker = lib.MigrationTracker;
        Assert.True(await tracker.EnsureMigrationsTableAsync());

        const string id = "it-probe-migration";
        await tracker.RemoveMigrationRecordAsync(id);
        Assert.False(await tracker.HasMigrationBeenAppliedAsync(id));

        Assert.True(await tracker.RecordMigrationAsync(id, "probe", "crc123"));
        Assert.True(await tracker.HasMigrationBeenAppliedAsync(id));

        // Recording twice must not duplicate.
        Assert.True(await tracker.RecordMigrationAsync(id, "probe", "crc123"));

        var applied = await tracker.GetAppliedMigrationsAsync();
        Assert.Single(applied.Where(m => m.MigrationId == id));

        Assert.True(await tracker.RemoveMigrationRecordAsync(id));
        Assert.False(await tracker.HasMigrationBeenAppliedAsync(id));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task MigrationRunner_register_and_plan()
    {
        var lib = Lib;
        var runner = lib.Migrations;

        var returned = runner.Register(new ProbeMigration());
        Assert.Same(runner, returned);
        Assert.Same(runner, runner.RegisterFrom(typeof(LiveServiceSurfaceTests).Assembly));

        var pending = await runner.GetPendingAsync();
        _out.WriteLine("pending: " + string.Join(", ", pending.Select(p => p.MigrationId)));
        // Registration is idempotent: the same migration must not be listed twice.
        Assert.Equal(pending.Select(p => p.MigrationId).Distinct().Count(), pending.Count);
    }

    // ── BackupManager ────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Database_wide_backup_and_cleanup()
    {
        var lib = await FreshAsync();

        // Take a table backup first purely to learn where backups land: the directory is
        // derived from the framework's data directory, not somewhere the test can guess.
        Assert.True((await lib.BackupManager.BackupTableSchemaAsync("it_svc", "public")).Value);
        var probe = lib.BackupManager.GetLatestBackupFile("it_svc", "public");
        Assert.NotNull(probe);
        var backupDir = Path.GetDirectoryName(probe!)!;

        var all = await lib.BackupManager.BackupDatabaseSchemaAsync();
        Assert.True(all.IsSuccess, all.Error?.ToString());
        Assert.True(all.Value);

        // A full-database dump must contain more than one table's DDL.
        var latest = Directory.GetFiles(backupDir, "database_*.sql")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        Assert.NotNull(latest);
        var text = await File.ReadAllTextAsync(latest!);
        Assert.Contains("it_svc", text, StringComparison.Ordinal);
        Assert.True(text.Split("CREATE TABLE").Length > 2, "expected several CREATE TABLE statements");

        // Cleanup with a zero-day window removes everything it can see.
        var cleaned = await lib.BackupManager.CleanupOldBackupsAsync(olderThanDays: 0);
        Assert.True(cleaned.IsSuccess, cleaned.Error?.ToString());
        Assert.True(cleaned.Value >= 1);
    }

    // ── JoinedQuery paging ───────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Joined_query_paging_and_first_or_default()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<SvcSide>().InsertManyAsync([
            new SvcSide { Id = 1, Title = "one" },
            new SvcSide { Id = 2, Title = "two" },
        ]);
        await lib.GetRepository<SvcRow>().InsertManyAsync(Enumerable.Range(0, 6)
            .Select(i => new SvcRow { Grp = (i % 2) + 1, Label = $"L{i}" }).ToList());

        var page = await lib.Query<SvcRow>()
            .Join<SvcSide, long, SvcJoined>(r => r.Grp, s => s.Id, (r, s) => new SvcJoined { Label = r.Label, Title = s.Title })
            .OrderBy((r, s) => r.Label)
            .Offset(2).Limit(2)
            .ToListAsync();
        Assert.True(page.IsSuccess, page.Error?.ToString());
        Assert.Equal(2, page.Value!.Count);
        Assert.Equal("L2", page.Value[0].Label);

        var take = await lib.Query<SvcRow>()
            .Join<SvcSide, long, SvcJoined>(r => r.Grp, s => s.Id, (r, s) => new SvcJoined { Label = r.Label, Title = s.Title })
            .OrderBy((r, s) => r.Label)
            .Take(3)
            .ToListAsync();
        Assert.Equal(3, take.Value!.Count);

        var first = await lib.Query<SvcRow>()
            .Join<SvcSide, long, SvcJoined>(r => r.Grp, s => s.Id, (r, s) => new SvcJoined { Label = r.Label, Title = s.Title })
            .OrderBy((r, s) => r.Label)
            .FirstOrDefaultAsync();
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.Equal("L0", first.Value!.Label);

        var none = await lib.Query<SvcRow>()
            .Join<SvcSide, long, SvcJoined>(r => r.Grp, s => s.Id, (r, s) => new SvcJoined { Label = r.Label, Title = s.Title })
            .Where((r, s) => r.Label == "nope")
            .FirstOrDefaultAsync();
        Assert.True(none.IsSuccess, none.Error?.ToString());
        Assert.Null(none.Value);
    }

    // ── Smart cache pool internals ───────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Pool_warmup_refresh_and_table_membership()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<SvcRow>().InsertAsync(new SvcRow { Grp = 1, Label = "seed" });

        var name = $"it-svc-pool-{Guid.NewGuid():N}";
        var pool = lib.RegisterCachePool(name, TimeSpan.FromMinutes(10));

        var warmed = 0;
        pool.WarmUp(() => { Interlocked.Increment(ref warmed); return Task.CompletedTask; });

        await lib.Query<SvcRow>().SmartCache(name).ToListAsync();
        Assert.True(pool.HasEntriesForTable("it_svc"), "the pool should own an entry for it_svc");
        Assert.False(pool.HasEntriesForTable("some_other_table"));

        var stats = pool.GetStats();
        Assert.Equal(name, stats.Name);
        Assert.True(stats.EntryCount > 0);

        await pool.RefreshNowAsync();
        Assert.True(pool.GetStats().TicksFired >= 1);
    }

    // ── Transient retry ──────────────────────────────────────────────────────────

    /// <summary>
    /// The retry policy only runs when PostgreSQL raises SQLSTATE 40001 or 40P01, which
    /// needs two transactions genuinely contending. Two serializable transactions each
    /// reading what the other writes produce a serialization failure on commit.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Serialization_failure_is_surfaced_as_a_transient_error()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<SvcRow>().InsertManyAsync([
            new SvcRow { Grp = 1, Label = "a" },
            new SvcRow { Grp = 2, Label = "b" },
        ]);

        var cm = lib.ConnectionManager;
        await using var c1 = await cm.OpenConnectionAsync();
        await using var c2 = await cm.OpenConnectionAsync();

        await using var t1 = await c1.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await using var t2 = await c2.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

        // Each transaction reads the whole table, then writes the row the other read:
        // the classic write-skew that serializable isolation must refuse.
        async Task ReadAll(Npgsql.NpgsqlTransaction tx, Npgsql.NpgsqlConnection conn)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT count(*) FROM public.it_svc";
            await cmd.ExecuteScalarAsync();
        }
        async Task Write(Npgsql.NpgsqlTransaction tx, Npgsql.NpgsqlConnection conn, long grp)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE public.it_svc SET label = label || 'x' WHERE grp = @g";
            cmd.Parameters.AddWithValue("@g", grp);
            await cmd.ExecuteNonQueryAsync();
        }

        await ReadAll(t1, c1);
        await ReadAll(t2, c2);
        await Write(t1, c1, 1);
        await Write(t2, c2, 2);

        await t1.CommitAsync();

        // The second commit must fail with a class-40 transaction-rollback code — exactly
        // what ConnectionManager.IsTransient treats as retryable.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () => await t2.CommitAsync());
        _out.WriteLine($"SQLSTATE {ex.SqlState}: {ex.MessageText}");
        Assert.StartsWith("40", ex.SqlState, StringComparison.Ordinal);
    }
}

public sealed class ProbeMigration : Migration
{
    public ProbeMigration() : base("0.0.1", 1, "probe") { }
    public override Task UpAsync(IMigrationContext ctx, CancellationToken ct) => Task.CompletedTask;
}
