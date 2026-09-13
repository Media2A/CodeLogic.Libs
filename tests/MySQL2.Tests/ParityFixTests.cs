using CL.MySQL2;
using CL.MySQL2.Configuration;
using CL.MySQL2.Core;
using CL.MySQL2.Events;
using CL.MySQL2.Models;
using CL.MySQL2.Services;
using CodeLogic.Core.Logging;
using Xunit;

namespace MySQL2.Tests;

// Coverage for the parity-audit fixes: the four correctness bugs (A1-A4), the three
// advertised-but-unwired features (B1-B3), and every configuration field that was
// declared-and-never-read (C). Each test owns its own it_fix_* tables so it cannot
// disturb the shared seed data.

[Table(Name = "it_fix_soft")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class FixSoft
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 40, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "deleted_utc", DataType = DataType.DateTime)] public DateTime? DeletedUtc { get; set; }
}

[Table(Name = "it_fix_parent")]
public sealed class FixParent
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "name", Size = 40, NotNull = true)] public string Name { get; set; } = "";
}

[Table(Name = "it_fix_child")]
public sealed class FixChild
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "parent_id", NotNull = true)] public long ParentId { get; set; }
}

public sealed class FixParentView
{
    [Column(Name = "id")] public long Id { get; set; }
}

[Table(Name = "it_fix_batch")]
public sealed class FixBatch
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "val", Size = 20, NotNull = true)] public string Val { get; set; } = "";
}

[Table(Name = "it_fix_retain")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class FixRetain
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "created_utc", DataType = DataType.DateTime, NotNull = true)] public DateTime CreatedUtc { get; set; }
}

// No Size and no DataType on the string column: its VARCHAR length comes from inference,
// i.e. from the configured DefaultStringSize.
[Table(Name = "it_fix_string")]
public sealed class FixString
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label")] public string Label { get; set; } = "";
}

/// <summary>Captures warnings so the IN-clause advisory can be asserted.</summary>
internal sealed class CapturingLogger : ILogger
{
    public readonly List<string> Warnings = [];
    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Debug(string message, params object?[] args) { }
    public void Info(string message) { }
    public void Info(string message, params object?[] args) { }
    public void Warning(string message) { lock (Warnings) Warnings.Add(message); }
    public void Warning(string message, params object?[] args) { lock (Warnings) Warnings.Add(message); }
    public void Error(string message, Exception? ex = null) { }
    public void Error(string message, params object?[] args) { }
    public void Critical(string message, Exception? ex = null) { }
}

[Collection("codelogic")]
public sealed class ParityFixTests
{
    private readonly MySQL2RuntimeFixture _fx;
    private MySQL2Library Mysql => _fx.Mysql;

    public ParityFixTests(MySQL2RuntimeFixture fx) => _fx = fx;

    private MySqlDatabaseConfig Cfg => Mysql.ConnectionManager.GetConfiguration("Default")!;

    // ── A1 — CountAsync applies the soft-delete filter ────────────────────────

    [DbFact]
    public async Task CountAsync_agrees_with_GetAllAsync_for_a_soft_delete_entity()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_fix_soft");
        Assert.True((await Mysql.SyncTableAsync<FixSoft>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<FixSoft>();
        var keep = (await repo.InsertAsync(new FixSoft { Label = "keep" })).Value!;
        var gone = (await repo.InsertAsync(new FixSoft { Label = "gone" })).Value!;
        Assert.True((await repo.DeleteAsync(gone.Id)).Value);   // soft delete

        var all = (await repo.GetAllAsync()).Value!;
        var count = (await repo.CountAsync()).Value;

        Assert.Single(all);
        Assert.Equal(keep.Id, all[0].Id);
        Assert.Equal(all.Count, count);

        // The row is still physically there — IncludeDeleted sees both.
        Assert.Equal(2, (await Mysql.Query<FixSoft>().IncludeDeleted().CountAsync()).Value);
    }

    // ── A2 — the configured MaxBatchInsertSize reaches the repository ─────────

    [DbFact]
    public async Task Configured_max_batch_insert_size_reaches_the_repository()
    {
        var original = Cfg.MaxBatchInsertSize;
        try
        {
            Cfg.MaxBatchInsertSize = 2;

            var repo = Mysql.GetRepository<FixBatch>();
            Assert.Equal(2, repo.MaxBatchInsertSize);
            Assert.Equal(2, Mysql.GetRepository<FixBatch>("Default").MaxBatchInsertSize);

            await using var tx = await Mysql.BeginTransactionAsync();
            Assert.Equal(2, Mysql.GetRepository<FixBatch>(tx).MaxBatchInsertSize);
            await tx.RollbackAsync();

            // And the chunking still produces every row.
            await _fx.Exec("DROP TABLE IF EXISTS it_fix_batch");
            Assert.True((await Mysql.SyncTableAsync<FixBatch>(createBackup: false)).IsSuccess);
            var inserted = await repo.InsertManyAsync(
                Enumerable.Range(0, 5).Select(i => new FixBatch { Val = $"v{i}" }).ToList());
            Assert.Equal(5, inserted.Value);
            Assert.Equal(5, (await repo.CountAsync()).Value);
        }
        finally
        {
            Cfg.MaxBatchInsertSize = original;
        }
    }

    // ── A3 — the terminal's cancellation token reaches the connection ─────────

    [DbFact]
    public async Task Cancelled_token_cancels_projected_and_joined_queries()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var projected = await Mysql.Query<Order>()
            .Select(o => new OrderView { OrderId = o.Id })
            .ToListAsync(cts.Token);
        Assert.True(projected.IsFailure);

        var joined = await Mysql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name })
            .ToListAsync(cts.Token);
        Assert.True(joined.IsFailure);

        var joinedCount = await Mysql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name })
            .CountAsync(cts.Token);
        Assert.True(joinedCount.IsFailure);

        // A live token still returns rows, so the failure above is the cancellation and
        // not a broken query.
        Assert.True((await Mysql.Query<Order>()
            .Select(o => new OrderView { OrderId = o.Id })
            .ToListAsync()).IsSuccess);
    }

    // ── A4 — a subquery-filtered projection is never cached ───────────────────

    [DbFact]
    public async Task Subquery_filtered_projection_is_not_served_from_cache()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_fix_child, it_fix_parent");
        Assert.True((await Mysql.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);
        Assert.True((await Mysql.SyncTableAsync<FixChild>(createBackup: false)).IsSuccess);

        var parents = Mysql.GetRepository<FixParent>();
        var children = Mysql.GetRepository<FixChild>();
        var p1 = (await parents.InsertAsync(new FixParent { Name = "p1" })).Value!;
        var p2 = (await parents.InsertAsync(new FixParent { Name = "p2" })).Value!;
        await children.InsertAsync(new FixChild { ParentId = p1.Id });

        async Task<List<long>> WithChildrenAsync() =>
            (await Mysql.Query<FixParent>()
                .WhereExists<FixChild>((p, c) => c.ParentId == p.Id)
                .Select(p => new FixParentView { Id = p.Id })
                .WithCache(TimeSpan.FromMinutes(10))
                .ToListAsync()).Value!.Select(v => v.Id).ToList();

        Assert.Equal([p1.Id], await WithChildrenAsync());

        // Mutating the OTHER table cannot bump this table's version counter, so a cached
        // entry here would still say "only p1".
        await children.InsertAsync(new FixChild { ParentId = p2.Id });

        var after = await WithChildrenAsync();
        Assert.Equal(2, after.Count);
        Assert.Contains(p2.Id, after);
    }

    // ── B1 — retention picks up an entity registered after start ──────────────

    [DbFact]
    public async Task Entity_registered_after_start_is_purged()
    {
        await _fx.Exec("DROP TABLE IF EXISTS it_fix_retain");
        // Registration happens here — long after CodeLogic.StartAsync() built the worker.
        Assert.True((await Mysql.SyncTableAsync<FixRetain>(createBackup: false)).IsSuccess);

        var repo = Mysql.GetRepository<FixRetain>();
        await repo.InsertManyAsync([
            new FixRetain { CreatedUtc = DateTime.UtcNow.AddDays(-100) },
            new FixRetain { CreatedUtc = DateTime.UtcNow.AddDays(-60) },
            new FixRetain { CreatedUtc = DateTime.UtcNow.AddDays(-1) },
        ]);

        // The library's own worker must know about the late arrival.
        var removed = await Mysql.RunRetentionOnceAsync();
        Assert.True(removed >= 2, $"expected at least the two expired rows to go, got {removed}");
        Assert.Single((await repo.GetAllAsync()).Value!);
    }

    // ── B2 — N+1 detection ────────────────────────────────────────────────────

    [DbFact]
    public async Task N1_detector_fires_at_the_threshold_and_stays_silent_at_zero()
    {
        var bus = Mysql.Events ?? throw new InvalidOperationException("no event bus");
        var seen = new List<N1QueryDetectedEvent>();
        bus.Subscribe<N1QueryDetectedEvent>(e => { lock (seen) seen.Add(e); });

        // Disabled (the default): nothing fires, however many times the template repeats.
        QueryObservability.ResetN1Detection();
        for (var i = 0; i < 10; i++)
            await Mysql.GetRepository<Customer>().GetByIdAsync(_fx.AliceId);
        await Task.Delay(250);
        lock (seen) Assert.Empty(seen);

        try
        {
            QueryObservability.ConfigureN1Detection("Default", 3);
            for (var i = 0; i < 5; i++)
                await Mysql.GetRepository<Customer>().GetByIdAsync(_fx.AliceId);

            for (var i = 0; i < 40; i++)
            {
                lock (seen) if (seen.Count > 0) break;
                await Task.Delay(25);
            }

            lock (seen)
            {
                Assert.NotEmpty(seen);
                Assert.All(seen, e => Assert.Equal("Default", e.ConnectionId));
                Assert.All(seen, e => Assert.True(e.Count >= 3));
                // Once per window, not once per execution past the threshold.
                Assert.True(seen.Count <= 2, $"fired {seen.Count} times for one window");
                Assert.Contains(seen, e => e.QueryTemplate.Contains("it_customer"));
            }
        }
        finally
        {
            QueryObservability.ResetN1Detection();
        }
    }

    [Fact]
    public void N1_templates_normalize_parameters_and_literals()
    {
        Assert.Equal(
            QueryObservability.NormalizeTemplate("SELECT * FROM `t` WHERE id = @p0 LIMIT 10"),
            QueryObservability.NormalizeTemplate("SELECT  *  FROM `t`  WHERE id = @p1  LIMIT 25"));
        Assert.NotEqual(
            QueryObservability.NormalizeTemplate("SELECT * FROM `a` WHERE id = @p0"),
            QueryObservability.NormalizeTemplate("SELECT * FROM `b` WHERE id = @p0"));
    }

    // ── B3 — slow-query EXPLAIN capture ───────────────────────────────────────

    [DbFact]
    public async Task Explain_capture_is_off_by_default_and_best_effort_when_on()
    {
        var bus = Mysql.Events ?? throw new InvalidOperationException("no event bus");
        var slow = new List<SlowQueryEvent>();
        bus.Subscribe<SlowQueryEvent>(e => { lock (slow) slow.Add(e); });

        var cm = Mysql.ConnectionManager;
        var original = Cfg.CaptureExplainOnSlowQuery;
        var parms = new Dictionary<string, object?> { ["@id"] = _fx.AliceId };
        const string sql = "SELECT * FROM `it_customer` WHERE `id` = @id";

        try
        {
            // Flag off (the shipped default): the event still publishes, with no plan.
            Cfg.CaptureExplainOnSlowQuery = false;
            QueryObservability.RecordSlow(cm, "Default", sql + " /* off */", 9999, parms);
            var off = await WaitForAsync(slow, e => e.Query.Contains("/* off */"));
            Assert.NotNull(off);
            Assert.Null(off!.ExplainJson);

            // Flag on: the same event, now carrying the plan.
            Cfg.CaptureExplainOnSlowQuery = true;
            QueryObservability.RecordSlow(cm, "Default", sql + " /* on */", 9999, parms);
            var on = await WaitForAsync(slow, e => e.Query.Contains("/* on */"));
            Assert.NotNull(on);
            Assert.NotNull(on!.ExplainJson);
            Assert.StartsWith("{", on.ExplainJson!.TrimStart());

            // Best effort: a statement that cannot be explained is skipped silently and the
            // event still publishes with a null payload.
            QueryObservability.RecordSlow(cm, "Default",
                "CREATE TABLE IF NOT EXISTS `it_fix_never` (`id` INT) /* ddl */", 9999, null);
            var ddl = await WaitForAsync(slow, e => e.Query.Contains("/* ddl */"));
            Assert.NotNull(ddl);
            Assert.Null(ddl!.ExplainJson);

            // Neither does a multi-statement batch, nor one whose parameters we don't hold.
            QueryObservability.RecordSlow(cm, "Default",
                "INSERT INTO `it_fix_never` (`id`) VALUES (1); SELECT LAST_INSERT_ID(); /* batch */", 9999, null);
            var batch = await WaitForAsync(slow, e => e.Query.Contains("/* batch */"));
            Assert.NotNull(batch);
            Assert.Null(batch!.ExplainJson);

            QueryObservability.RecordSlow(cm, "Default", sql + " /* unbound */", 9999, null);
            var unbound = await WaitForAsync(slow, e => e.Query.Contains("/* unbound */"));
            Assert.NotNull(unbound);
            Assert.Null(unbound!.ExplainJson);
        }
        finally
        {
            Cfg.CaptureExplainOnSlowQuery = original;
        }
    }

    private static async Task<T?> WaitForAsync<T>(List<T> sink, Func<T, bool> match)
    {
        for (var i = 0; i < 80; i++)
        {
            lock (sink)
            {
                var hit = sink.FirstOrDefault(match);
                if (hit is not null) return hit;
            }
            await Task.Delay(25);
        }
        return default;
    }

    // ── C — QueryTimeoutMs ────────────────────────────────────────────────────

    [DbFact]
    public async Task Query_timeout_is_applied_to_the_commands_the_library_creates()
    {
        var original = Cfg.QueryTimeoutMs;
        try
        {
            // The value reaches the command, rounded up to MySqlConnector's whole seconds.
            Cfg.QueryTimeoutMs = 1_500;
            await using (var conn = await Mysql.ConnectionManager.OpenConnectionAsync())
            {
                await using var probe = conn.CreateCommand();
                Mysql.ConnectionManager.ApplyCommandTimeout(probe, "Default");
                Assert.Equal(2, probe.CommandTimeout);
            }

            // And it really cuts a long statement short. MySQL's SLEEP() returns 1 when the
            // server kills it (which is how MySqlConnector enforces the timeout) rather than
            // 0 for a completed sleep, so the abort is visible in the value as well as the
            // wall clock.
            Cfg.QueryTimeoutMs = 1_000;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cut = await Mysql.SqlScalarAsync<int>("SELECT SLEEP(10)");
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6),
                $"a 10s sleep ran for {sw.Elapsed} under a 1s command timeout");
            Assert.True(cut.IsFailure || cut.Value == 1,
                $"expected the sleep to be interrupted; got value={cut.Value} err={cut.Error?.Message}");

            Cfg.QueryTimeoutMs = 30_000;
            var ok = await Mysql.SqlScalarAsync<int>("SELECT SLEEP(0)");
            Assert.True(ok.IsSuccess, ok.Error?.ToString());
            Assert.Equal(0, ok.Value);
        }
        finally
        {
            Cfg.QueryTimeoutMs = original;
        }
    }

    // ── C — DefaultStringSize ─────────────────────────────────────────────────

    [DbFact]
    public async Task Default_string_size_drives_inferred_varchar_columns()
    {
        // Unconfigured, the shipped default still applies.
        Assert.Equal(255, TypeConverter.InferColumn(typeof(string)).Size);

        try
        {
            SqlGenerationOptions.Configure(77, 1_000, null);

            Assert.Equal(77, TypeConverter.InferColumn(typeof(string)).Size);
            // The same value has to reach ResolveColumn, which is what DDL generation calls.
            Assert.Equal(77, TypeConverter.ResolveColumn(new ColumnAttribute(), typeof(string)).Size);
            Assert.Equal("VARCHAR(77)",
                TypeConverter.GetMySqlType(new ColumnAttribute(), StorageType.Default, typeof(string)));
            // An explicit size still wins.
            Assert.Equal(10, TypeConverter.ResolveColumn(new ColumnAttribute { Size = 10 }, typeof(string)).Size);

            await _fx.Exec("DROP TABLE IF EXISTS it_fix_string");
            Assert.True((await Mysql.SyncTableAsync<FixString>(createBackup: false)).IsSuccess);
            Assert.Equal(77, await VarcharLengthAsync("it_fix_string", "label"));
        }
        finally
        {
            SqlGenerationOptions.Reset();
            await _fx.Exec("DROP TABLE IF EXISTS it_fix_string");
        }
    }

    private Task<long> VarcharLengthAsync(string table, string column) =>
        Mysql.ConnectionManager.ExecuteWithConnectionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c";
            cmd.Parameters.AddWithValue("@t", table);
            cmd.Parameters.AddWithValue("@c", column);
            var raw = await cmd.ExecuteScalarAsync();
            return raw is null || raw is DBNull ? -1L : Convert.ToInt64(raw);
        }, "Default");

    // ── C — CacheEnabledOverride ──────────────────────────────────────────────

    [Fact]
    public async Task Cache_enabled_override_wins_over_the_global_switch()
    {
        var calls = 0;
        Task<string> Factory() { calls++; return Task.FromResult("v"); }

        var key = "override-" + Guid.NewGuid().ToString("N");
        try
        {
            // No override: the global switch (on) caches, so the factory runs once.
            await QueryCache.GetOrSetAsync(key, "ovr_tbl", Factory, TimeSpan.FromMinutes(5), "Default");
            await QueryCache.GetOrSetAsync(key, "ovr_tbl", Factory, TimeSpan.FromMinutes(5), "Default");
            Assert.Equal(1, calls);

            // Override off: every read goes to the factory, nothing is stored.
            QueryCache.SetConnectionOverride("Default", false);
            var key2 = "override-" + Guid.NewGuid().ToString("N");
            await QueryCache.GetOrSetAsync(key2, "ovr_tbl", Factory, TimeSpan.FromMinutes(5), "Default");
            await QueryCache.GetOrSetAsync(key2, "ovr_tbl", Factory, TimeSpan.FromMinutes(5), "Default");
            Assert.Equal(3, calls);

            // Another connection id is unaffected by this database's override.
            var key3 = "override-" + Guid.NewGuid().ToString("N");
            await QueryCache.GetOrSetAsync(key3, "ovr_tbl", Factory, TimeSpan.FromMinutes(5), "Other");
            await QueryCache.GetOrSetAsync(key3, "ovr_tbl", Factory, TimeSpan.FromMinutes(5), "Other");
            Assert.Equal(4, calls);
        }
        finally
        {
            QueryCache.SetConnectionOverride("Default", null);
            QueryCache.Invalidate("ovr_tbl");
        }
    }

    // ── C — CacheConfiguration.PublishEvents ──────────────────────────────────

    [DbFact]
    public async Task Publish_events_gates_the_cache_hit_and_miss_events()
    {
        var bus = Mysql.Events ?? throw new InvalidOperationException("no event bus");
        var misses = new List<CacheMissEvent>();
        bus.Subscribe<CacheMissEvent>(e => { lock (misses) misses.Add(e); });

        try
        {
            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                defaultTtlSeconds: 60, publishEvents: false);

            var quiet = "quiet-" + Guid.NewGuid().ToString("N");
            await QueryCache.GetOrSetAsync(quiet, "pub_tbl", () => Task.FromResult("v"),
                TimeSpan.FromMinutes(1), "Default");
            await Task.Delay(300);
            lock (misses) Assert.DoesNotContain(misses, e => e.CacheKey == quiet);

            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                defaultTtlSeconds: 60, publishEvents: true);

            var loud = "loud-" + Guid.NewGuid().ToString("N");
            await QueryCache.GetOrSetAsync(loud, "pub_tbl", () => Task.FromResult("v"),
                TimeSpan.FromMinutes(1), "Default");
            Assert.NotNull(await WaitForAsync(misses, e => e.CacheKey == loud));
        }
        finally
        {
            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                defaultTtlSeconds: 60, publishEvents: true);
            QueryCache.Invalidate("pub_tbl");
        }
    }

    // ── C — CacheConfiguration.DefaultTtlSeconds ──────────────────────────────

    [DbFact]
    public async Task Parameterless_WithCache_uses_the_configured_default_ttl()
    {
        try
        {
            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                defaultTtlSeconds: 42, publishEvents: true);
            Assert.Equal(TimeSpan.FromSeconds(42), QueryCache.DefaultTtl);

            QueryCache.Invalidate("it_customer");
            var first = await Mysql.Query<Customer>().WithCache().ToListAsync();
            Assert.True(first.IsSuccess, first.Error?.ToString());

            var bus = Mysql.Events!;
            var hits = new List<CacheHitEvent>();
            bus.Subscribe<CacheHitEvent>(e => { lock (hits) hits.Add(e); });

            var second = await Mysql.Query<Customer>().WithCache().ToListAsync();
            Assert.Equal(first.Value!.Count, second.Value!.Count);
            Assert.NotNull(await WaitForAsync(hits, e => e.TableName == "it_customer"));

            // The projection pipeline has the same overload.
            var projected = await Mysql.Query<Customer>()
                .Select(c => new FixParentView { Id = c.Id })
                .WithCache()
                .ToListAsync();
            Assert.True(projected.IsSuccess, projected.Error?.ToString());
        }
        finally
        {
            QueryCache.Configure(enabled: true, maxEntries: 10_000, timeQuantizeSeconds: 60,
                defaultTtlSeconds: 60, publishEvents: true);
            QueryCache.Invalidate("it_customer");
        }
    }

    // ── C — BackupDirectory ───────────────────────────────────────────────────

    [DbFact]
    public async Task Backup_directory_override_is_honoured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cl_mysql2_backup_" + Guid.NewGuid().ToString("N"));
        var original = Cfg.BackupDirectory;
        try
        {
            Cfg.BackupDirectory = dir;

            var ok = await Mysql.BackupManager.BackupTableSchemaAsync("it_customer");
            Assert.True(ok.IsSuccess && ok.Value, ok.Error?.ToString());

            Assert.True(Directory.Exists(dir));
            Assert.NotEmpty(Directory.GetFiles(dir, "it_customer_*.sql"));
            Assert.Equal(dir, Path.GetDirectoryName(Mysql.BackupManager.GetLatestBackupFile("it_customer")));
        }
        finally
        {
            Cfg.BackupDirectory = original;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── C — SslCertificatePath ────────────────────────────────────────────────

    [Fact]
    public void Ssl_certificate_path_is_written_as_the_CA_when_ssl_is_on()
    {
        var cfg = new MySqlDatabaseConfig
        {
            Host = "h", Database = "d", Username = "u",
            EnableSsl = true, SslCertificatePath = @"C:\certs\ca.pem"
        };

        var withSsl = cfg.BuildConnectionString();
        Assert.Contains("ca.pem", withSsl);
        Assert.Contains("VerifyCA", withSsl, StringComparison.OrdinalIgnoreCase);

        // With SSL off the path is ignored rather than handed to the driver.
        cfg.EnableSsl = false;
        var withoutSsl = cfg.BuildConnectionString();
        Assert.DoesNotContain("ca.pem", withoutSsl);
        Assert.Contains("None", withoutSsl, StringComparison.OrdinalIgnoreCase);

        // Unset (the default) changes nothing at all.
        cfg.EnableSsl = true;
        cfg.SslCertificatePath = null;
        Assert.DoesNotContain("VerifyCA", cfg.BuildConnectionString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── C — MaxInClauseValues ─────────────────────────────────────────────────

    [Fact]
    public void Oversized_in_clause_warns_once_and_still_translates()
    {
        var logger = new CapturingLogger();
        try
        {
            SqlGenerationOptions.Configure(255, 3, logger);

            var ids = Enumerable.Range(1, 5).Select(i => (long)i).ToArray();
            var (sql, parms) = MySqlExpressionVisitor.Translate<Customer>(c => ids.Contains(c.Id));

            // Nothing is chunked or rejected: still one parameter per value.
            Assert.Contains(" IN (", sql);
            Assert.Equal(5, parms.Count);

            Assert.Single(logger.Warnings);
            Assert.Contains("Customer", logger.Warnings[0]);
            Assert.Contains("5", logger.Warnings[0]);

            // At or below the limit, nothing is logged.
            logger.Warnings.Clear();
            var few = new long[] { 1, 2, 3 };
            MySqlExpressionVisitor.Translate<Customer>(c => few.Contains(c.Id));
            Assert.Empty(logger.Warnings);
        }
        finally
        {
            SqlGenerationOptions.Configure(255, 1_000, null);
            SqlGenerationOptions.Reset();
        }
    }

    // ── C — the two fields that were marked obsolete instead of wired ─────────

    [Fact]
    public void Obsolete_configuration_fields_still_compile_and_deserialize()
    {
#pragma warning disable CS0618 // deliberately exercising the obsolete members
        var db = new MySqlDatabaseConfig();
        Assert.Equal(256, db.PreparedStatementCacheSize);
        db.PreparedStatementCacheSize = 512;
        Assert.Equal(512, db.PreparedStatementCacheSize);

        var cache = new CacheConfiguration();
        Assert.Equal(256, cache.MaxMemoryMb);
        cache.MaxMemoryMb = 1;
        Assert.Equal(1, cache.MaxMemoryMb);
#pragma warning restore CS0618
    }
}
