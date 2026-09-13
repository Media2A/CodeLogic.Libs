using System.Reflection;
using System.Text.Json;
using CL.PostgreSQL;
using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Events;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using Xunit;

namespace PostgreSQL.Tests;

// Coverage for the parity-audit fixes: the four correctness bugs (A1-A4), the three
// advertised-but-unwired features (B1-B3), the configuration fields that are now honoured
// (C), and the per-connection DefaultSchema.
//
// Everything that needs a server runs behind the shared "codelogic" fixture and the same
// [FactRequiresEnv] gate the rest of the suite uses.

// ── Entities ─────────────────────────────────────────────────────────────────────

[Table(Name = "it_fix_soft")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class FixSoft
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 50, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "deleted_utc")] public DateTime? DeletedUtc { get; set; }
}

[Table(Name = "it_fix_batch")]
public sealed class FixBatch
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "n", NotNull = true)] public int N { get; set; }
}

[Table(Name = "it_fix_parent")]
public sealed class FixParent
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", Size = 50, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "it_fix_child")]
public sealed class FixChild
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "parent_id", NotNull = true, Index = true)] public long ParentId { get; set; }
    [Column(Name = "flag", NotNull = true)] public bool Flag { get; set; }
}

/// <summary>Registered only after the library has started — the B1 regression.</summary>
[Table(Name = "it_fix_late_retain")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class FixLateRetain
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", NotNull = true, Index = true)] public DateTime CreatedUtc { get; set; }
}

/// <summary>No <c>[Table(Schema = …)]</c>: lands in whatever the connection configured.</summary>
[Table(Name = "it_fix_unqualified")]
public sealed class FixUnqualified
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 40, NotNull = true)] public string Label { get; set; } = "";
}

/// <summary>Declares its schema explicitly: the attribute must keep winning.</summary>
[Table(Name = "it_fix_pinned", Schema = "public")]
public sealed class FixPinned
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
}

/// <summary>A string column with no explicit Size, for the DefaultStringSize test.</summary>
[Table(Name = "it_fix_strsize")]
public sealed class FixStringSize
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "free_text")] public string FreeText { get; set; } = "";
}

// ── Offline: the obsolete members still compile and still round-trip ──────────────

public sealed class ObsoleteConfigFieldTests
{
    [Fact]
    public void Obsolete_fields_are_marked_but_still_usable()
    {
#pragma warning disable CS0618 // deliberately exercising the obsolete members
        var db = new PostgreSqlDatabaseConfig { PreparedStatementCacheSize = 64 };
        Assert.Equal(64, db.PreparedStatementCacheSize);

        var cache = new CacheConfiguration { MaxMemoryMb = 32 };
        Assert.Equal(32, cache.MaxMemoryMb);

        Assert.NotNull(typeof(PostgreSqlDatabaseConfig)
            .GetProperty(nameof(PostgreSqlDatabaseConfig.PreparedStatementCacheSize))!
            .GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(typeof(CacheConfiguration)
            .GetProperty(nameof(CacheConfiguration.MaxMemoryMb))!
            .GetCustomAttribute<ObsoleteAttribute>());
#pragma warning restore CS0618
    }

    [Fact]
    public void Explain_capture_defaults_off_so_nothing_starts_running_plans()
    {
        Assert.False(new PostgreSqlDatabaseConfig().CaptureExplainOnSlowQuery);
        Assert.Equal(0, new PostgreSqlDatabaseConfig().N1DetectorThreshold);
    }

    [Fact]
    public void SslCertificatePath_reaches_the_connection_string()
    {
        var cfg = new PostgreSqlDatabaseConfig
        {
            Host = "h", Database = "d", Username = "u",
            SslCertificatePath = @"C:\certs\client.pem",
            SslKeyPath = @"C:\certs\client.key",
            SslRootCertificatePath = @"C:\certs\ca.pem"
        };

        var connStr = cfg.BuildConnectionString();
        Assert.Contains(@"SSL Certificate=C:\certs\client.pem", connStr);
        Assert.Contains(@"SSL Key=C:\certs\client.key", connStr);
        Assert.Contains(@"Root Certificate=C:\certs\ca.pem", connStr);
    }

    [Fact]
    public void DefaultSchema_with_an_illegal_identifier_is_rejected_by_validation()
    {
        var cfg = new PostgreSqlDatabaseConfig
        {
            Host = "h", Database = "d", Username = "u",
            DefaultSchema = "we\"ird"
        };

        var result = cfg.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("DefaultSchema", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultStringSize_is_threaded_into_type_inference()
    {
        // An unsized string column falls back to whatever the caller passes in; schema
        // sync passes the connection's configured DefaultStringSize.
        Assert.Equal("character varying(255)", TypeConverter.GetPostgreSqlType(
            new ColumnAttribute(), StorageType.Default, typeof(string), 255));
        Assert.Equal("character varying(64)", TypeConverter.GetPostgreSqlType(
            new ColumnAttribute(), StorageType.Default, typeof(string), 64));

        // An explicit [Column(Size = ...)] still wins over the configured default.
        Assert.Equal("character varying(10)", TypeConverter.GetPostgreSqlType(
            new ColumnAttribute { Size = 10 }, StorageType.Default, typeof(string), 64));
    }

    [Fact]
    public void Wide_IN_list_is_reported_but_never_chunked()
    {
        var ids = Enumerable.Range(1, 1500).Select(i => (long)i).ToArray();
        var (sql, parms, maxInList) = PostgreSqlExpressionVisitor
            .TranslateWithStats<FixBatch>(b => ids.Contains(b.Id));

        // Reported for the warning — and still emitted whole, which is the contract.
        Assert.Equal(1500, maxInList);
        Assert.Equal(1500, parms.Count);
        Assert.Contains(" IN (", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void N1_templates_fold_digits_and_whitespace()
    {
        Assert.Equal(
            N1Detector.Normalize("SELECT * FROM t WHERE id = @qb_0 LIMIT 10"),
            N1Detector.Normalize("SELECT *  FROM t\nWHERE id = @qb_7 LIMIT 250"));
        Assert.NotEqual(
            N1Detector.Normalize("SELECT a FROM t"),
            N1Detector.Normalize("SELECT b FROM t"));
    }

    [Fact]
    public void Explainable_statements_are_recognised_and_the_rest_skipped()
    {
        var p = new Dictionary<string, object?> { ["@p0"] = 1 };

        Assert.True(SlowQueryExplain.IsExplainable("SELECT 1", null));
        Assert.True(SlowQueryExplain.IsExplainable("SELECT * FROM t WHERE id = @p0", p));
        Assert.True(SlowQueryExplain.IsExplainable("UPDATE t SET a = 1 WHERE id = @p0", p));

        // Parameterized but no captured values → cannot bind, so not explained.
        Assert.False(SlowQueryExplain.IsExplainable("SELECT * FROM t WHERE id = @p0", null));
        // Not an optimizable statement.
        Assert.False(SlowQueryExplain.IsExplainable("CREATE TABLE t (id int)", null));
        Assert.False(SlowQueryExplain.IsExplainable("  ", null));
        // A multi-statement batch cannot be explained as a unit.
        Assert.False(SlowQueryExplain.IsExplainable("SELECT 1; SELECT 2", null));
    }
}

// ── Live ─────────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class ParityFixTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public ParityFixTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    /// <summary>
    /// A configuration pointing at the same test server as the fixture, so a connection
    /// registered at runtime is genuinely usable (the health check probes every registered
    /// id, and the suite's health test must keep passing).
    /// </summary>
    private static PostgreSqlDatabaseConfig ServerConfig() => new()
    {
        Host = Environment.GetEnvironmentVariable("CL_PG_TEST_HOST") ?? "127.0.0.1",
        Port = int.Parse(Environment.GetEnvironmentVariable("CL_PG_TEST_PORT") ?? "5432"),
        Database = Environment.GetEnvironmentVariable("CL_PG_TEST_DB") ?? "postgres",
        Username = Environment.GetEnvironmentVariable("CL_PG_TEST_USER") ?? "postgres",
        Password = Environment.GetEnvironmentVariable("CL_PG_TEST_PASS") ?? ""
    };

    private async Task DropAsync(string qualified) =>
        await Lib.ExecuteSqlAsync($"DROP TABLE IF EXISTS {qualified} CASCADE");

    private static async Task WaitForAsync(Func<bool> condition, int maxMs = 5000)
    {
        for (var waited = 0; waited < maxMs && !condition(); waited += 25)
            await Task.Delay(25);
    }

    // ── A1: CountAsync honours the soft-delete filter ────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task A1_CountAsync_and_GetAll_agree_on_a_soft_delete_entity()
    {
        var lib = Lib;
        await DropAsync("\"public\".\"it_fix_soft\"");
        Assert.True((await lib.SyncTableAsync<FixSoft>(createBackup: false)).IsSuccess);

        var repo = lib.GetRepository<FixSoft>();
        var ids = new List<long>();
        foreach (var label in new[] { "a", "b", "c" })
            ids.Add((await repo.InsertAsync(new FixSoft { Label = label })).Value!.Id);

        Assert.Equal(3L, (await repo.CountAsync()).Value);

        // Soft delete one row: it stays in the table but must leave both reads.
        Assert.True((await repo.DeleteAsync(ids[0])).Value);

        var all = await repo.GetAllAsync();
        var count = await repo.CountAsync();
        Assert.True(count.IsSuccess, count.Error?.Message);
        Assert.Equal(2, all.Value!.Count);
        Assert.Equal(2L, count.Value);

        // The row is still physically there — this is a filter, not a delete.
        var raw = await lib.SqlScalarAsync<long>("SELECT COUNT(*) FROM \"public\".\"it_fix_soft\"");
        Assert.Equal(3L, raw.Value);
    }

    // ── A2: MaxBatchInsertSize reaches the repository ────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task A2_configured_MaxBatchInsertSize_reaches_the_repository()
    {
        var lib = Lib;
        var cfg = lib.LoadedConfiguration!.Databases["Default"];
        var original = cfg.MaxBatchInsertSize;

        try
        {
            cfg.MaxBatchInsertSize = 7;

            var repo = lib.GetRepository<FixBatch>();
            Assert.Equal(7, BatchSizeOf(repo));

            await using var tx = await lib.BeginTransactionAsync();
            Assert.Equal(7, BatchSizeOf(lib.GetRepository<FixBatch>(tx)));
            await tx.RollbackAsync();

            // And a batch larger than the chunk size still inserts every row.
            await DropAsync("\"public\".\"it_fix_batch\"");
            Assert.True((await lib.SyncTableAsync<FixBatch>(createBackup: false)).IsSuccess);
            var inserted = await lib.GetRepository<FixBatch>()
                .InsertManyAsync(Enumerable.Range(0, 20).Select(i => new FixBatch { N = i }));
            Assert.True(inserted.IsSuccess, inserted.Error?.Message);
            Assert.Equal(20, inserted.Value);
            Assert.Equal(20L, (await lib.GetRepository<FixBatch>().CountAsync()).Value);
        }
        finally
        {
            cfg.MaxBatchInsertSize = original;
        }

        // The field is private by design; reading it is the only way to prove the wiring
        // without depending on how the batched INSERT happens to be logged.
        static int BatchSizeOf<T>(Repository<T> repo) where T : class, new() =>
            (int)typeof(Repository<T>)
                .GetField("_maxBatchInsertSize", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(repo)!;
    }

    // ── A3: terminals forward their cancellation token ───────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task A3_cancelled_token_surfaces_as_a_cancellation()
    {
        var lib = Lib;
        await DropAsync("\"public\".\"it_fix_parent\"");
        await DropAsync("\"public\".\"it_fix_child\"");
        Assert.True((await lib.SyncSchemaAsync(typeof(FixParent), typeof(FixChild))).IsSuccess);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var projected = await lib.Query<FixParent>()
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cts.Token);
        Assert.True(projected.IsFailure, "a cancelled projection completed instead of cancelling");

        var joined = await lib.Query<FixParent>()
            .Join<FixChild, long, object>(
                p => p.Id, c => c.ParentId, (p, c) => new { p.Id, c.Flag })
            .ToListAsync(cts.Token);
        Assert.True(joined.IsFailure, "a cancelled join completed instead of cancelling");

        var joinedCount = await lib.Query<FixParent>()
            .Join<FixChild, long, object>(
                p => p.Id, c => c.ParentId, (p, c) => new { p.Id, c.Flag })
            .CountAsync(cts.Token);
        Assert.True(joinedCount.IsFailure, "a cancelled join count completed instead of cancelling");
    }

    // ── A4: a subquery filter must not carry cache decoration into the projection ─

    [FactRequiresEnv(Gate, Reason)]
    public async Task A4_projection_of_a_subquery_filtered_query_is_not_cached()
    {
        var lib = Lib;
        await DropAsync("\"public\".\"it_fix_parent\"");
        await DropAsync("\"public\".\"it_fix_child\"");
        Assert.True((await lib.SyncSchemaAsync(typeof(FixParent), typeof(FixChild))).IsSuccess);
        QueryCache.Clear();

        var parents = lib.GetRepository<FixParent>();
        var children = lib.GetRepository<FixChild>();
        var p1 = (await parents.InsertAsync(new FixParent { Name = "p1" })).Value!.Id;
        var p2 = (await parents.InsertAsync(new FixParent { Name = "p2" })).Value!.Id;
        await children.InsertAsync(new FixChild { ParentId = p1, Flag = true });
        await children.InsertAsync(new FixChild { ParentId = p2, Flag = false });

        Task<CodeLogic.Core.Results.Result<List<string>>> Run() =>
            lib.Query<FixParent>()
               .WhereExists<FixChild>((p, c) => c.ParentId == p.Id && c.Flag)
               .Select(p => p.Name)
               .WithCache(TimeSpan.FromMinutes(5))
               .ToListAsync();

        var first = await Run();
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.Equal(["p1"], first.Value!);

        // Mutate the OTHER table. The result cache stamps entries with a single table's
        // version, so a cached cross-table projection would keep returning {p1}.
        var flip = await lib.ExecuteSqlAsync(
            "UPDATE \"public\".\"it_fix_child\" SET flag = NOT flag");
        Assert.True(flip.IsSuccess, flip.Error?.Message);

        var second = await Run();
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(["p2"], second.Value!);
    }

    // ── B1: an entity registered after StartAsync is picked up and purged ────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task B1_entity_registered_after_start_is_retained_and_purged()
    {
        var lib = Lib;
        await DropAsync("\"public\".\"it_fix_late_retain\"");

        var worker = lib.Retention;
        Assert.NotNull(worker);

        // Registration happens through the normal, documented flow — a sync call made long
        // after CodeLogic.StartAsync(). Before the fix the worker had snapshotted an empty
        // set at start and [RetainDays] was dead here.
        Assert.True((await lib.SyncTableAsync<FixLateRetain>(createBackup: false)).IsSuccess);
        Assert.True(worker!.HasWork, "the retention worker did not pick up the late registration");

        var repo = lib.GetRepository<FixLateRetain>();
        await repo.InsertManyAsync([
            new FixLateRetain { CreatedUtc = DateTime.UtcNow.AddDays(-90) },
            new FixLateRetain { CreatedUtc = DateTime.UtcNow.AddDays(-45) },
            new FixLateRetain { CreatedUtc = DateTime.UtcNow.AddDays(-2) },
        ]);

        // RunOnceAsync exercises the same pass the loop runs, without the 5-minute delay.
        var removed = await worker.RunOnceAsync();
        Assert.True(removed >= 2, $"expected the two rows past the 30-day window to go, removed {removed}");

        var remaining = await repo.GetAllAsync();
        Assert.Single(remaining.Value!);
        Assert.True(remaining.Value![0].CreatedUtc > DateTime.UtcNow.AddDays(-30));

        // Registering again is a no-op — Start() must stay idempotent.
        Assert.False(worker.Register(typeof(FixLateRetain)));
        worker.Start();
    }

    // ── B2: the N+1 detector ─────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task B2_detector_fires_at_the_threshold_and_is_silent_at_zero()
    {
        var lib = Lib;
        var bus = lib.Events ?? throw new InvalidOperationException("no event bus");
        const string id = "n1probe";

        var seen = new List<N1QueryDetectedEvent>();
        bus.Subscribe<N1QueryDetectedEvent>(e => { lock (seen) seen.Add(e); });

        // Threshold 0 (the default, and what "Default" is configured with) stays silent
        // however many times the same template runs.
        N1Detector.Reset();
        for (var i = 0; i < 10; i++)
            QueryObservability.RecordExecuted("Default", "SELECT * FROM z WHERE id = @p0", 1, 1, cacheHit: false);
        await Task.Delay(150);
        lock (seen) Assert.DoesNotContain(seen, e => e.ConnectionId == "Default");

        var cfg = ServerConfig();
        cfg.N1DetectorThreshold = 3;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);
        try
        {
            N1Detector.Reset();
            for (var i = 0; i < 5; i++)
                QueryObservability.RecordExecuted(id, "SELECT * FROM z WHERE id = @p0", 1, 1, cacheHit: false);

            await WaitForAsync(() => { lock (seen) return seen.Any(e => e.ConnectionId == id); });

            List<N1QueryDetectedEvent> fired;
            lock (seen) fired = seen.Where(e => e.ConnectionId == id).ToList();

            Assert.Single(fired);                       // once per window, not per execution
            Assert.Equal(3, fired[0].Count);            // fired exactly at the threshold
            Assert.Contains("SELECT * FROM z WHERE id = @p?", fired[0].QueryTemplate, StringComparison.Ordinal);

            // A cache hit never reached the server, so it is not part of an N+1 pattern.
            var before = fired.Count;
            for (var i = 0; i < 5; i++)
                QueryObservability.RecordExecuted(id, "SELECT * FROM cached", 1, 1, cacheHit: true);
            await Task.Delay(150);
            lock (seen) Assert.Equal(before, seen.Count(e => e.ConnectionId == id));
        }
        finally
        {
            // Put the probe connection back to "disabled" so the rest of the suite runs on
            // the zero-cost path again.
            var off = ServerConfig();
            off.N1DetectorThreshold = 0;
            lib.ConnectionManager.RegisterConfiguration(off, id);
            N1Detector.Reset();
        }
    }

    // ── B3: EXPLAIN capture on the slow path ─────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task B3_explain_is_null_when_off_and_captured_when_on()
    {
        var lib = Lib;
        var bus = lib.Events ?? throw new InvalidOperationException("no event bus");
        await DropAsync("\"public\".\"it_fix_parent\"");
        Assert.True((await lib.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);
        await lib.GetRepository<FixParent>().InsertAsync(new FixParent { Name = "x" });

        var slow = new List<SlowQueryEvent>();
        bus.Subscribe<SlowQueryEvent>(e => { lock (slow) slow.Add(e); });

        // A threshold of 0 makes every query "slow", which is the only deterministic way to
        // reach the slow path without an artificially blocked server.
        const string offId = "explain_off";
        const string onId = "explain_on";

        var offCfg = ServerConfig();
        offCfg.CaptureExplainOnSlowQuery = false;
        lib.ConnectionManager.RegisterConfiguration(offCfg, offId);

        var onCfg = ServerConfig();
        onCfg.CaptureExplainOnSlowQuery = true;
        lib.ConnectionManager.RegisterConfiguration(onCfg, onId);

        try
        {
            var offResult = await new QueryBuilder<FixParent>(lib.ConnectionManager, null, offId, 0)
                .Where(p => p.Name == "x").ToListAsync();
            Assert.True(offResult.IsSuccess, offResult.Error?.Message);

            await WaitForAsync(() => { lock (slow) return slow.Any(e => e.ConnectionId == offId); });
            SlowQueryEvent offEvent;
            lock (slow) offEvent = slow.First(e => e.ConnectionId == offId);
            Assert.Null(offEvent.ExplainJson);

            var onResult = await new QueryBuilder<FixParent>(lib.ConnectionManager, null, onId, 0)
                .Where(p => p.Name == "x").ToListAsync();
            Assert.True(onResult.IsSuccess, onResult.Error?.Message);

            await WaitForAsync(() => { lock (slow) return slow.Any(e => e.ConnectionId == onId); });
            SlowQueryEvent onEvent;
            lock (slow) onEvent = slow.First(e => e.ConnectionId == onId);

            // Best-effort by contract: the event must always publish. On a reachable server
            // running a plain SELECT the plan should also be there, and be real JSON.
            Assert.NotNull(onEvent.ExplainJson);
            using var doc = JsonDocument.Parse(onEvent.ExplainJson!);
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.True(doc.RootElement[0].TryGetProperty("Plan", out _));
        }
        finally
        {
            var reset = ServerConfig();
            reset.CaptureExplainOnSlowQuery = false;
            lib.ConnectionManager.RegisterConfiguration(reset, offId);
            lib.ConnectionManager.RegisterConfiguration(reset, onId);
        }
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task B3_explain_is_skipped_inside_a_transaction_scope()
    {
        var lib = Lib;
        var bus = lib.Events ?? throw new InvalidOperationException("no event bus");
        await DropAsync("\"public\".\"it_fix_parent\"");
        Assert.True((await lib.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);

        const string id = "explain_tx";
        var cfg = ServerConfig();
        cfg.CaptureExplainOnSlowQuery = true;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        var slow = new List<SlowQueryEvent>();
        bus.Subscribe<SlowQueryEvent>(e => { lock (slow) slow.Add(e); });

        try
        {
            await using var tx = await lib.BeginTransactionAsync(id);
            var result = await new QueryBuilder<FixParent>(lib.ConnectionManager, null, tx, 0)
                .ToListAsync();
            Assert.True(result.IsSuccess, result.Error?.Message);
            await tx.CommitAsync();

            await WaitForAsync(() => { lock (slow) return slow.Any(e => e.ConnectionId == id); });
            SlowQueryEvent ev;
            lock (slow) ev = slow.First(e => e.ConnectionId == id);

            // Never run a plan on (or alongside) the caller's transaction.
            Assert.Null(ev.ExplainJson);
        }
        finally
        {
            var reset = ServerConfig();
            reset.CaptureExplainOnSlowQuery = false;
            lib.ConnectionManager.RegisterConfiguration(reset, id);
        }
    }

    // ── C: QueryTimeoutMs ────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_QueryTimeoutMs_is_applied_as_the_command_timeout()
    {
        var lib = Lib;
        const string id = "timeout_probe";
        var cfg = ServerConfig();
        cfg.QueryTimeoutMs = 1_000;
        cfg.TransientRetryCount = 0;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        try
        {
            var result = await lib.ExecuteSqlAsync("SELECT pg_sleep(5)", connectionId: id);
            Assert.True(result.IsFailure, "a 5s statement completed under a 1s query timeout");
        }
        finally
        {
            // Leave the probe usable: the health check connects to every registered id.
            lib.ConnectionManager.RegisterConfiguration(ServerConfig(), id);
        }
    }

    // ── C: DefaultStringSize ─────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_DefaultStringSize_drives_the_generated_varchar_length()
    {
        var lib = Lib;
        const string id = "strsize_probe";
        var cfg = ServerConfig();
        cfg.DefaultStringSize = 77;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        try
        {
            await DropAsync("\"public\".\"it_fix_strsize\"");
            var sync = await lib.SyncTableAsync<FixStringSize>(createBackup: false, connectionId: id);
            Assert.True(sync.IsSuccess, sync.Error?.Message);

            var length = await lib.SqlScalarAsync<int>("""
                SELECT character_maximum_length FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'it_fix_strsize' AND column_name = 'free_text'
                """);
            Assert.Equal(77, length.Value);
        }
        finally
        {
            lib.ConnectionManager.RegisterConfiguration(ServerConfig(), id);
            await DropAsync("\"public\".\"it_fix_strsize\"");
        }
    }

    // ── C: CacheEnabledOverride ──────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_CacheEnabledOverride_wins_over_the_global_switch()
    {
        var lib = Lib;
        const string id = "cacheoff_probe";
        var cfg = ServerConfig();
        cfg.CacheEnabledOverride = false;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        try
        {
            Assert.True(QueryCache.IsEnabledFor("Default"));
            Assert.False(QueryCache.IsEnabledFor(id));

            await DropAsync("\"public\".\"it_fix_parent\"");
            Assert.True((await lib.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);
            var repo = lib.GetRepository<FixParent>();
            await repo.InsertAsync(new FixParent { Name = "one" });
            QueryCache.Clear();

            Task<CodeLogic.Core.Results.Result<long>> Count(string connectionId) =>
                new QueryBuilder<FixParent>(lib.ConnectionManager, null, connectionId)
                    .WithCache(TimeSpan.FromMinutes(5)).CountAsync();

            Assert.Equal(1L, (await Count(id)).Value);

            // Raw SQL deliberately bypasses the cache invalidation hook, so a cached read
            // would still say 1. The override must make this connection read through.
            await lib.ExecuteSqlAsync(
                "INSERT INTO \"public\".\"it_fix_parent\" (name) VALUES ('two')");

            Assert.Equal(2L, (await Count(id)).Value);
        }
        finally
        {
            lib.ConnectionManager.RegisterConfiguration(ServerConfig(), id);
            QueryCache.Clear();
        }
    }

    // ── C: BackupDirectory ───────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_BackupDirectory_override_is_where_backups_land()
    {
        var lib = Lib;
        const string id = "backupdir_probe";
        var dir = Path.Combine(Path.GetTempPath(), "cl_pg_backupdir_" + Guid.NewGuid().ToString("N"));

        var cfg = ServerConfig();
        cfg.BackupDirectory = dir;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        try
        {
            await DropAsync("\"public\".\"it_fix_parent\"");
            Assert.True((await lib.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);

            var backup = await lib.BackupManager.BackupTableSchemaAsync("it_fix_parent", "public", id);
            Assert.True(backup.IsSuccess, backup.Error?.Message);
            Assert.True(backup.Value);

            Assert.True(Directory.Exists(dir), $"configured backup directory {dir} was not created");
            Assert.NotEmpty(Directory.GetFiles(dir, "public_it_fix_parent_*.sql"));

            // The default connection keeps writing to DataDirectory/backups.
            var defaultBackup = await lib.BackupManager.BackupTableSchemaAsync("it_fix_parent", "public");
            Assert.True(defaultBackup.IsSuccess, defaultBackup.Error?.Message);
            Assert.Single(Directory.GetFiles(dir, "public_it_fix_parent_*.sql"));
        }
        finally
        {
            lib.ConnectionManager.RegisterConfiguration(ServerConfig(), id);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── C: CacheConfiguration.DefaultTtlSeconds / PublishEvents ──────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_parameterless_WithCache_uses_the_configured_default_ttl()
    {
        var lib = Lib;
        await DropAsync("\"public\".\"it_fix_parent\"");
        Assert.True((await lib.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);
        await lib.GetRepository<FixParent>().InsertAsync(new FixParent { Name = "one" });
        QueryCache.Clear();

        Assert.Equal(TimeSpan.FromSeconds(60), QueryCache.DefaultTtl);

        Task<CodeLogic.Core.Results.Result<List<FixParent>>> Cached() =>
            lib.Query<FixParent>().WithCache().ToListAsync();

        Assert.Single((await Cached()).Value!);

        // Raw SQL bypasses invalidation, so a second call inside the TTL must be served
        // from the cache — which only happens if WithCache() actually set one.
        await lib.ExecuteSqlAsync("INSERT INTO \"public\".\"it_fix_parent\" (name) VALUES ('two')");
        Assert.Single((await Cached()).Value!);

        QueryCache.Clear();
        Assert.Equal(2, (await Cached()).Value!.Count);
        QueryCache.Clear();
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_PublishEvents_false_silences_cache_hit_and_miss_events()
    {
        var lib = Lib;
        var bus = lib.Events ?? throw new InvalidOperationException("no event bus");

        var misses = new List<CacheMissEvent>();
        var hits = new List<CacheHitEvent>();
        bus.Subscribe<CacheMissEvent>(e => { lock (misses) misses.Add(e); });
        bus.Subscribe<CacheHitEvent>(e => { lock (hits) hits.Add(e); });

        Assert.True(QueryCache.PublishEvents);

        var quiet = "quiet-" + Guid.NewGuid().ToString("N");
        try
        {
            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                publishEvents: false, defaultTtlSeconds: 60);

            await QueryCache.GetOrSetAsync(quiet, "t", () => Task.FromResult(1), TimeSpan.FromMinutes(1), "Default");
            await QueryCache.GetOrSetAsync(quiet, "t", () => Task.FromResult(1), TimeSpan.FromMinutes(1), "Default");
            await Task.Delay(200);

            lock (misses) Assert.DoesNotContain(misses, e => e.CacheKey == quiet);
            lock (hits) Assert.DoesNotContain(hits, e => e.CacheKey == quiet);
        }
        finally
        {
            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                publishEvents: true, defaultTtlSeconds: 60);
            QueryCache.Clear();
        }

        // And with it back on, the same calls do publish.
        var loud = "loud-" + Guid.NewGuid().ToString("N");
        await QueryCache.GetOrSetAsync(loud, "t", () => Task.FromResult(1), TimeSpan.FromMinutes(1), "Default");
        await QueryCache.GetOrSetAsync(loud, "t", () => Task.FromResult(1), TimeSpan.FromMinutes(1), "Default");

        await WaitForAsync(() => { lock (hits) return hits.Any(e => e.CacheKey == loud); });
        lock (misses) Assert.Contains(misses, e => e.CacheKey == loud);
        lock (hits) Assert.Contains(hits, e => e.CacheKey == loud);
        QueryCache.Clear();
    }

    // ── C: MaxInClauseValues warns rather than throwing ──────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task C_wide_IN_list_still_executes_when_over_MaxInClauseValues()
    {
        var lib = Lib;
        const string id = "inclause_probe";
        var cfg = ServerConfig();
        cfg.MaxInClauseValues = 3;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        try
        {
            await DropAsync("\"public\".\"it_fix_batch\"");
            Assert.True((await lib.SyncTableAsync<FixBatch>(createBackup: false)).IsSuccess);
            var repo = lib.GetRepository<FixBatch>();
            var collected = new List<long>();
            for (var i = 0; i < 8; i++)
                collected.Add((await repo.InsertAsync(new FixBatch { N = i })).Value!.Id);
            // An array, not a List: instance List<T>.Contains(column) is translated by the
            // string LIKE branch of the visitor and is a separate, pre-existing gap.
            var ids = collected.ToArray();

            // Eight values against a ceiling of three: warned about, never chunked, never
            // thrown — breaking callers who exceed the ceiling today is not on the table.
            var result = await new QueryBuilder<FixBatch>(lib.ConnectionManager, null, id)
                .Where(b => ids.Contains(b.Id))
                .ToListAsync();

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(8, result.Value!.Count);
        }
        finally
        {
            lib.ConnectionManager.RegisterConfiguration(ServerConfig(), id);
        }
    }

    // ── DefaultSchema is honoured for unqualified entities ───────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task DefaultSchema_places_unqualified_entities_in_the_configured_schema()
    {
        var lib = Lib;
        const string id = "altschema_probe";
        const string schema = "cl_alt";

        await lib.ExecuteSqlAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        await DropAsync("\"public\".\"it_fix_unqualified\"");

        var cfg = ServerConfig();
        cfg.DefaultSchema = schema;
        lib.ConnectionManager.RegisterConfiguration(cfg, id);

        try
        {
            // The schema does not exist yet — sync has to create it.
            var sync = await lib.SyncTableAsync<FixUnqualified>(createBackup: false, connectionId: id);
            Assert.True(sync.IsSuccess, sync.Error?.Message);

            var inAlt = await lib.SqlScalarAsync<bool>(
                $"SELECT to_regclass('{schema}.it_fix_unqualified') IS NOT NULL");
            Assert.True(inAlt.Value, $"the entity was not created in the configured schema '{schema}'");

            var inPublic = await lib.SqlScalarAsync<bool>(
                "SELECT to_regclass('public.it_fix_unqualified') IS NOT NULL");
            Assert.False(inPublic.Value, "the entity was also created in public");

            // Metadata resolves per connection: the same type maps to two schemas.
            Assert.Equal(schema, EntityMetadata<FixUnqualified>.SchemaNameFor(id));
            Assert.Equal("public", EntityMetadata<FixUnqualified>.SchemaNameFor("Default"));
            Assert.Equal($"\"{schema}\".\"it_fix_unqualified\"",
                EntityMetadata<FixUnqualified>.QualifiedTableNameFor(id));

            // An explicit [Table(Schema = ...)] still wins on the same connection.
            Assert.Equal("public", EntityMetadata<FixPinned>.SchemaNameFor(id));

            // CRUD through that connection targets the configured schema.
            var repo = lib.GetRepository<FixUnqualified>(id);
            var inserted = await repo.InsertAsync(new FixUnqualified { Label = "alt" });
            Assert.True(inserted.IsSuccess, inserted.Error?.Message);
            Assert.Equal(1L, (await repo.CountAsync()).Value);

            var direct = await lib.SqlScalarAsync<long>(
                $"SELECT COUNT(*) FROM {schema}.it_fix_unqualified");
            Assert.Equal(1L, direct.Value);

            var queried = await lib.Query<FixUnqualified>(id).Where(x => x.Label == "alt").ToListAsync();
            Assert.True(queried.IsSuccess, queried.Error?.Message);
            Assert.Single(queried.Value!);
        }
        finally
        {
            lib.ConnectionManager.RegisterConfiguration(ServerConfig(), id);
            await lib.ExecuteSqlAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
