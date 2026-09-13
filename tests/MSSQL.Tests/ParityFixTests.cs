using System.Diagnostics;
using System.Reflection;
using CL.MSSQL;
using CL.MSSQL.Configuration;
using CL.MSSQL.Events;
using CL.MSSQL.Models;
using CL.MSSQL.Services;
using Xunit;

namespace MSSQL.Tests;

// Coverage for the correctness fixes (A1-A4), the three features that were advertised but
// never wired (B1-B3) and the configuration fields that are now read (C). Everything here
// runs against the shared live fixture; nothing depends on the seeded read-only tables
// except where it only reads them.

[Table(Name = "it_fix_soft", Schema = "dbo")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class FixSoftRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "name", DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    public string Name { get; set; } = "";

    [Column(Name = "deleted_utc", DataType = DataType.DateTime2)]
    public DateTime? DeletedUtc { get; set; }
}

[Table(Name = "it_fix_batch", Schema = "dbo")]
public sealed class FixBatchRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "n", DataType = DataType.Int, NotNull = true)]
    public int N { get; set; }
}

[Table(Name = "it_fix_parent", Schema = "dbo")]
public sealed class FixParent
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "tag", DataType = DataType.NVarChar, Size = 20, NotNull = true)]
    public string Tag { get; set; } = "";
}

[Table(Name = "it_fix_child", Schema = "dbo")]
public sealed class FixChild
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "parent_id", DataType = DataType.BigInt, NotNull = true)]
    public long ParentId { get; set; }
}

public sealed class FixParentView
{
    public long Id { get; set; }
    public string Tag { get; set; } = "";
}

// Registered only AFTER CodeLogic.StartAsync() has run — which is the normal application
// flow and the exact case the retention worker used to miss entirely.
[Table(Name = "it_fix_retain", Schema = "dbo")]
[RetainDays(1, nameof(CreatedUtc), BatchSize = 2)]
public sealed class FixRetainRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "created_utc", DataType = DataType.DateTime2, NotNull = true)]
    public DateTime CreatedUtc { get; set; }
}

[Table(Name = "it_fix_n1", Schema = "dbo")]
public sealed class FixN1Row
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "n", DataType = DataType.Int, NotNull = true)]
    public int N { get; set; }
}

// No [Column] attribute on Note at all: an unsized string is the only shape whose column
// type comes from TypeConverter.InferColumn, and therefore from DefaultStringSize.
[Table(Name = "it_fix_strsize", Schema = "dbo")]
public sealed class FixStringSizeRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    public string Note { get; set; } = "";
}

[Table(Name = "it_fix_cache", Schema = "dbo")]
public sealed class FixCacheRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "n", DataType = DataType.Int, NotNull = true)]
    public int N { get; set; }
}

[Table(Name = "it_fix_lock", Schema = "dbo")]
public sealed class FixLockRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "n", DataType = DataType.Int, NotNull = true)]
    public int N { get; set; }
}

[Collection("codelogic")]
public sealed class ParityFixTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mssql => _fx.Mysql;
    private SqlServerDatabaseConfig Db => Mssql.ConnectionManager.GetConfiguration("Default")
        ?? throw new InvalidOperationException("Default database is not registered.");

    public ParityFixTests(MSSQLRuntimeFixture fx) => _fx = fx;

    // ── A1 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>CountAsync</c> used to emit a bare <c>SELECT COUNT(*)</c> while <c>GetAllAsync</c>
    /// and <c>GetPagedAsync</c>'s own count both filtered soft-deleted rows, so the two
    /// contradicted each other for a <c>[SoftDelete]</c> entity.
    /// </summary>
    [DbFact]
    public async Task CountAsync_excludes_soft_deleted_rows_like_its_neighbours()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_soft]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_soft");
        Assert.True((await Mssql.SyncTableAsync<FixSoftRow>(createBackup: false)).IsSuccess);

        var repo = Mssql.GetRepository<FixSoftRow>();
        var a = (await repo.InsertAsync(new FixSoftRow { Name = "a" })).Value!;
        await repo.InsertAsync(new FixSoftRow { Name = "b" });
        await repo.InsertAsync(new FixSoftRow { Name = "c" });

        Assert.Equal(3, (await repo.CountAsync()).Value);

        Assert.True((await repo.DeleteAsync(a.Id)).Value);          // soft delete

        var all = (await repo.GetAllAsync()).Value!;
        var count = (await repo.CountAsync()).Value;
        var paged = (await repo.GetPagedAsync(1, 10)).Value!;

        Assert.Equal(2, all.Count);
        Assert.Equal(all.Count, count);
        Assert.Equal(all.Count, paged.TotalItems);

        // The row is still physically present — the filter, not a hard delete.
        var raw = await Mssql.SqlScalarAsync<int>("SELECT COUNT(*) FROM [dbo].[it_fix_soft]");
        Assert.Equal(3, raw.Value);

        // IncludeDeleted() is the documented way back to the unfiltered count.
        Assert.Equal(3, (await Mssql.Query<FixSoftRow>().IncludeDeleted().CountAsync()).Value);
    }

    // ── A2 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GetRepository&lt;T&gt;</c> passed <c>SlowQueryThresholdMs</c> but never
    /// <c>MaxBatchInsertSize</c>, so the constructor default of 500 always won.
    /// </summary>
    [DbFact]
    public async Task Configured_MaxBatchInsertSize_reaches_the_repository()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_batch]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_batch");
        Assert.True((await Mssql.SyncTableAsync<FixBatchRow>(createBackup: false)).IsSuccess);

        var original = Db.MaxBatchInsertSize;
        Assert.Equal(500, BatchSizeOf(Mssql.GetRepository<FixBatchRow>()));
        try
        {
            Db.MaxBatchInsertSize = 2;
            Assert.Equal(2, BatchSizeOf(Mssql.GetRepository<FixBatchRow>()));

            await using var tx = await Mssql.BeginTransactionAsync();
            Assert.Equal(2, BatchSizeOf(Mssql.GetRepository<FixBatchRow>(tx)));
            await tx.RollbackAsync();

            // Chunking at 2 still inserts every row exactly once.
            var repo = Mssql.GetRepository<FixBatchRow>();
            Assert.Equal(5, (await repo.InsertManyAsync(
                Enumerable.Range(1, 5).Select(n => new FixBatchRow { N = n }).ToList())).Value);
            Assert.Equal(5, (await repo.CountAsync()).Value);
        }
        finally
        {
            Db.MaxBatchInsertSize = original;
        }
    }

    private static int BatchSizeOf<T>(Repository<T> repository) where T : class, new() =>
        (int)typeof(Repository<T>)
            .GetField("_maxBatchInsertSize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(repository)!;

    // ── A3 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ProjectedQuery</c> and <c>JoinedQuery</c> dropped the caller's token on the floor
    /// when handing the action to the connection manager, so an already-cancelled call still
    /// opened a connection and ran.
    /// </summary>
    [DbFact]
    public async Task Cancelled_token_cancels_projected_and_joined_queries_before_connecting()
    {
        var bus = Mssql.Events ?? throw new InvalidOperationException("no event bus");
        var opened = 0;
        bus.Subscribe<DatabaseConnectedEvent>(_ => Interlocked.Increment(ref opened));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var projected = await Mssql.Query<Order>()
            .Select(o => new { o.Id, o.Total })
            .ToListAsync(cts.Token);
        Assert.True(projected.IsFailure);

        var joined = await Mssql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total })
            .ToListAsync(cts.Token);
        Assert.True(joined.IsFailure);

        var joinedCount = await Mssql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total })
            .CountAsync(cts.Token);
        Assert.True(joinedCount.IsFailure);

        // Give any published event time to land, then assert none did: a cancelled call must
        // not have reached the server at all.
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref opened));

        // The same pipelines still work with a live token.
        Assert.True((await Mssql.Query<Order>().Select(o => new { o.Id }).ToListAsync()).IsSuccess);
    }

    // ── A4 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A subquery-filtered query is not cacheable — the entry is stamped with one table's
    /// version, so a change to the EXISTS side could never invalidate it. The guard existed on
    /// the query builder's own terminals but not on the projection it handed out.
    /// </summary>
    [DbFact]
    public async Task Subquery_filtered_projection_is_not_cached_even_when_WithCache_is_asked_for()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_child]; DROP TABLE IF EXISTS [dbo].[it_fix_parent];");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_child");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_parent");
        Assert.True((await Mssql.SyncTableAsync<FixParent>(createBackup: false)).IsSuccess);
        Assert.True((await Mssql.SyncTableAsync<FixChild>(createBackup: false)).IsSuccess);

        var parents = Mssql.GetRepository<FixParent>();
        var children = Mssql.GetRepository<FixChild>();
        var one = (await parents.InsertAsync(new FixParent { Tag = "one" })).Value!;
        var two = (await parents.InsertAsync(new FixParent { Tag = "two" })).Value!;
        await children.InsertAsync(new FixChild { ParentId = one.Id });

        Task<List<FixParentView>> Run() => Cached();

        async Task<List<FixParentView>> Cached()
        {
            var result = await Mssql.Query<FixParent>()
                .WhereExists<FixChild>((p, c) => c.ParentId == p.Id)
                .Select(p => new FixParentView { Id = p.Id, Tag = p.Tag })
                .WithCache(TimeSpan.FromMinutes(10))
                .ToListAsync();
            Assert.True(result.IsSuccess, result.Error?.ToString());
            return result.Value!;
        }

        Assert.Single(await Run());

        // The OTHER table changes. A cached entry keyed on it_fix_parent's version would be
        // untouched by this and would keep reporting one row.
        await children.InsertAsync(new FixChild { ParentId = two.Id });

        Assert.Equal(2, (await Run()).Count);

        // Sanity: the same query without a subquery filter does cache, so the assertion above
        // is about the guard and not about caching being broken outright.
        var cachedBefore = QueryCache.Count;
        await Mssql.Query<FixParent>()
            .Select(p => new FixParentView { Id = p.Id, Tag = p.Tag })
            .WithCache(TimeSpan.FromMinutes(10))
            .ToListAsync();
        Assert.True(QueryCache.Count > cachedBefore);
    }

    // ── B1 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The retention worker snapshotted <c>_registeredEntities</c> at start, but every
    /// documented flow registers entities (via schema sync) after <c>StartAsync</c> — so
    /// <c>HasWork</c> was false, the loop never ran, and <c>[RetainDays]</c> was dead.
    /// </summary>
    [DbFact]
    public async Task Entity_registered_after_start_is_picked_up_by_retention()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_retain]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_retain");

        // This registration happens long after CodeLogic.StartAsync() ran in the fixture.
        Assert.True((await Mssql.SyncTableAsync<FixRetainRow>(createBackup: false)).IsSuccess);

        var repo = Mssql.GetRepository<FixRetainRow>();
        await repo.InsertManyAsync([
            new FixRetainRow { CreatedUtc = DateTime.UtcNow.AddDays(-10) },
            new FixRetainRow { CreatedUtc = DateTime.UtcNow.AddDays(-9) },
            new FixRetainRow { CreatedUtc = DateTime.UtcNow.AddDays(-8) },
            new FixRetainRow { CreatedUtc = DateTime.UtcNow }           // inside the window
        ]);
        Assert.Equal(4, (await repo.CountAsync()).Value);

        var removed = await Mssql.RunRetentionOnceAsync();
        Assert.True(removed >= 3, $"expected at least the three stale rows to go, got {removed}");

        var survivors = (await repo.GetAllAsync()).Value!;
        Assert.Single(survivors);
        Assert.True(survivors[0].CreatedUtc > DateTime.UtcNow.AddDays(-1));
    }

    /// <summary>
    /// The live entry list is the mechanism: a worker constructed empty still picks up an
    /// entity registered afterwards, and <c>Start</c> stays idempotent.
    /// </summary>
    [DbFact]
    public async Task Retention_worker_entry_list_is_live()
    {
        await using var worker = new RetentionWorker(Mssql.ConnectionManager, null, []);
        Assert.False(worker.HasWork);
        worker.Start();                                   // nothing to do yet — no loop

        Assert.False(worker.TryRegister(typeof(FixBatchRow)));   // no [RetainDays]
        Assert.True(worker.TryRegister(typeof(FixRetainRow)));
        Assert.False(worker.TryRegister(typeof(FixRetainRow)));  // already there
        Assert.True(worker.HasWork);
        Assert.Contains(typeof(FixRetainRow), worker.Entities);

        worker.Start();
        worker.Start();                                   // idempotent
        await Task.Delay(25);
    }

    // ── B2 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RecordN1</c> had no call sites and <c>N1DetectorThreshold</c> was never read, so
    /// <c>N1QueryDetectedEvent</c> could not fire. Threshold 0 (the default) must stay inert.
    /// </summary>
    [DbFact]
    public async Task N1_detector_fires_at_the_threshold_and_stays_silent_at_zero()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_n1]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_n1");
        Assert.True((await Mssql.SyncTableAsync<FixN1Row>(createBackup: false)).IsSuccess);
        await Mssql.GetRepository<FixN1Row>().InsertManyAsync(
            Enumerable.Range(1, 3).Select(n => new FixN1Row { N = n }).ToList());

        var bus = Mssql.Events ?? throw new InvalidOperationException("no event bus");
        var seen = new List<N1QueryDetectedEvent>();
        bus.Subscribe<N1QueryDetectedEvent>(e =>
        {
            if (e.QueryTemplate.Contains("it_fix_n1", StringComparison.OrdinalIgnoreCase))
                lock (seen) seen.Add(e);
        });

        // Default is 0 = disabled: hammering the same query publishes nothing.
        Assert.Equal(0, Db.N1DetectorThreshold);
        for (var i = 0; i < 10; i++)
            Assert.True((await Mssql.Query<FixN1Row>().Where(r => r.N > 0).ToListAsync()).IsSuccess);
        await Task.Delay(150);
        lock (seen) Assert.Empty(seen);

        try
        {
            QueryObservability.ConfigureN1Detection("Default", 4);
            for (var i = 0; i < 6; i++)
                Assert.True((await Mssql.Query<FixN1Row>().Where(r => r.N > 0).ToListAsync()).IsSuccess);

            for (var i = 0; i < 40; i++)
            {
                lock (seen) if (seen.Count > 0) break;
                await Task.Delay(25);
            }

            N1QueryDetectedEvent fired;
            lock (seen)
            {
                // Once per window, not once per execution past the threshold.
                Assert.Single(seen);
                fired = seen[0];
            }
            Assert.Equal("Default", fired.ConnectionId);
            Assert.True(fired.Count >= 4, $"count was {fired.Count}");
            Assert.Contains("it_fix_n1", fired.QueryTemplate, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            QueryObservability.ConfigureN1Detection("Default", 0);
        }

        // Back to disabled: still nothing new.
        int before;
        lock (seen) before = seen.Count;
        for (var i = 0; i < 10; i++)
            await Mssql.Query<FixN1Row>().Where(r => r.N > 0).ToListAsync();
        await Task.Delay(150);
        lock (seen) Assert.Equal(before, seen.Count);
    }

    [Fact]
    public void N1_template_normalization_collapses_parameter_numbering_and_whitespace()
    {
        var a = QueryObservability.NormalizeTemplate("SELECT *   FROM [t]\r\n WHERE [a] = @qb_0");
        var b = QueryObservability.NormalizeTemplate("SELECT * FROM [t] WHERE [a] = @qb_17");
        Assert.Equal(a, b);
        Assert.Equal("SELECT * FROM [t] WHERE [a] = @qb_", a);
    }

    // ── B3 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The explain payload is gated on <c>CaptureExplainOnSlowQuery</c>. With the flag off the
    /// event must carry null; with it on the capture is strictly best-effort — SQL Server's
    /// plan cache is shared and evictable, so the contract is "valid ShowPlan XML or nothing,
    /// never a throw", not "always a plan".
    /// </summary>
    [DbFact]
    public async Task Slow_query_explain_payload_follows_the_capture_flag()
    {
        var bus = Mssql.Events ?? throw new InvalidOperationException("no event bus");
        var seen = new List<SlowQueryEvent>();
        bus.Subscribe<SlowQueryEvent>(e =>
        {
            if (e.Query.Contains("it_customer", StringComparison.OrdinalIgnoreCase))
                lock (seen) seen.Add(e);
        });

        var originalThreshold = Db.SlowQueryThresholdMs;
        var originalCapture = Db.CaptureExplainOnSlowQuery;
        try
        {
            Db.SlowQueryThresholdMs = 0;        // every query counts as slow
            Db.CaptureExplainOnSlowQuery = false;

            Assert.True((await Mssql.Query<Customer>().ToListAsync()).IsSuccess);
            var off = await WaitForOne(seen);
            Assert.Null(off.ExplainJson);

            lock (seen) seen.Clear();
            Db.CaptureExplainOnSlowQuery = true;

            Assert.True((await Mssql.Query<Customer>().ToListAsync()).IsSuccess);
            var on = await WaitForOne(seen, allowExplainDelay: true);
            if (on.ExplainJson is { } plan)
            {
                Assert.Contains("ShowPlanXML", plan, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("<", plan.TrimStart(), StringComparison.Ordinal);
            }
            // A null payload is a legitimate cache miss; what matters is that the event still
            // published and nothing threw into the query path.
            Assert.True(on.ElapsedMs >= 0);
        }
        finally
        {
            Db.SlowQueryThresholdMs = originalThreshold;
            Db.CaptureExplainOnSlowQuery = originalCapture;
        }
    }

    private static async Task<SlowQueryEvent> WaitForOne(List<SlowQueryEvent> seen, bool allowExplainDelay = false)
    {
        for (var i = 0; i < (allowExplainDelay ? 80 : 40); i++)
        {
            lock (seen) if (seen.Count > 0) return seen[0];
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("No SlowQueryEvent was published.");
    }

    // ── C: QueryTimeoutMs ────────────────────────────────────────────────────

    /// <summary>
    /// <c>QueryTimeoutMs</c> is applied as the command timeout on the commands the library
    /// issues. A blocked read has to give up at that timeout rather than at the connection
    /// string's 30 s default.
    /// </summary>
    [DbFact]
    public async Task QueryTimeoutMs_bounds_a_blocked_library_query()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_lock]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_lock");
        Assert.True((await Mssql.SyncTableAsync<FixLockRow>(createBackup: false)).IsSuccess);
        var repo = Mssql.GetRepository<FixLockRow>();
        var row = (await repo.InsertAsync(new FixLockRow { N = 1 })).Value!;

        var original = Db.QueryTimeoutMs;
        try
        {
            Db.QueryTimeoutMs = 1_000;

            await using var tx = await Mssql.BeginTransactionAsync();
            // Take an exclusive lock on the row and hold it for the duration of the read.
            Assert.Equal(1, (await Mssql.Query<FixLockRow>(tx)
                .Where(r => r.Id == row.Id)
                .UpdateAsync(new Dictionary<string, object?> { [nameof(FixLockRow.N)] = 99 })).Value);

            var sw = Stopwatch.StartNew();
            var blocked = await Mssql.Query<FixLockRow>().Where(r => r.Id == row.Id).ToListAsync();
            sw.Stop();

            Assert.True(blocked.IsFailure, "a read blocked behind an uncommitted write must time out");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
                $"the 1s command timeout was not applied — the read took {sw.Elapsed}");

            await tx.RollbackAsync();
        }
        finally
        {
            Db.QueryTimeoutMs = original;
        }

        // With the lock gone the same query succeeds again.
        Assert.Single((await Mssql.Query<FixLockRow>().ToListAsync()).Value!);
    }

    // ── C: DefaultStringSize ─────────────────────────────────────────────────

    [DbFact]
    public async Task DefaultStringSize_drives_the_inferred_nvarchar_length()
    {
        var original = Db.DefaultStringSize;
        try
        {
            await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_strsize]");
            await Mssql.SchemaState.RemoveStateAsync("it_fix_strsize");
            Db.DefaultStringSize = 64;
            Assert.True((await Mssql.SyncTableAsync<FixStringSizeRow>(createBackup: false)).IsSuccess);

            // sys.columns.max_length is in BYTES for nvarchar, so 64 chars == 128.
            var maxLength = await Mssql.SqlScalarAsync<int>(
                "SELECT c.max_length FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id " +
                "WHERE t.name = 'it_fix_strsize' AND c.name = 'Note'");
            Assert.Equal(128, maxLength.Value);
        }
        finally
        {
            Db.DefaultStringSize = original;
        }

        // Back at the default the same entity reconciles to nvarchar(255).
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_strsize]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_strsize");
        Assert.True((await Mssql.SyncTableAsync<FixStringSizeRow>(createBackup: false)).IsSuccess);
        var defaultLength = await Mssql.SqlScalarAsync<int>(
            "SELECT c.max_length FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id " +
            "WHERE t.name = 'it_fix_strsize' AND c.name = 'Note'");
        Assert.Equal(510, defaultLength.Value);
    }

    // ── C: CacheEnabledOverride / PublishEvents / DefaultTtlSeconds ──────────

    [DbFact]
    public async Task CacheEnabledOverride_turns_the_cache_off_for_one_database()
    {
        await SeedCacheTableAsync();

        try
        {
            QueryCache.SetConnectionOverride("Default", false);
            var before = QueryCache.Count;
            Assert.True((await Mssql.Query<FixCacheRow>()
                .Where(r => r.N > 0)
                .WithCache(TimeSpan.FromMinutes(10))
                .ToListAsync()).IsSuccess);
            Assert.Equal(before, QueryCache.Count);
        }
        finally
        {
            QueryCache.SetConnectionOverride("Default", Db.CacheEnabledOverride);
        }

        // No override: the same query does populate the cache.
        var baseline = QueryCache.Count;
        Assert.True((await Mssql.Query<FixCacheRow>()
            .Where(r => r.N > 1)
            .WithCache(TimeSpan.FromMinutes(10))
            .ToListAsync()).IsSuccess);
        Assert.True(QueryCache.Count > baseline);
    }

    [DbFact]
    public async Task Parameterless_WithCache_uses_the_configured_default_ttl()
    {
        await SeedCacheTableAsync();

        var first = await Mssql.Query<FixCacheRow>().WithCache().CountAsync();
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.Equal(3, first.Value);

        // Raw SQL deliberately does not invalidate the result cache, so a cached query still
        // reports the old count — which is how we can see that WithCache() cached at all.
        Assert.True((await Mssql.ExecuteSqlAsync("INSERT INTO [dbo].[it_fix_cache] ([n]) VALUES (4)")).IsSuccess);

        Assert.Equal(3, (await Mssql.Query<FixCacheRow>().WithCache().CountAsync()).Value);
        Assert.Equal(4, (await Mssql.Query<FixCacheRow>().CountAsync()).Value);

        QueryCache.Invalidate("it_fix_cache");
        Assert.Equal(4, (await Mssql.Query<FixCacheRow>().WithCache().CountAsync()).Value);
    }

    [DbFact]
    public async Task PublishEvents_gates_the_cache_hit_and_miss_events()
    {
        await SeedCacheTableAsync();

        var bus = Mssql.Events ?? throw new InvalidOperationException("no event bus");
        var hits = 0;
        var misses = 0;
        bus.Subscribe<CacheHitEvent>(e => { if (e.TableName == "it_fix_cache") Interlocked.Increment(ref hits); });
        bus.Subscribe<CacheMissEvent>(e => { if (e.TableName == "it_fix_cache") Interlocked.Increment(ref misses); });

        var cacheConfig = new CacheConfiguration();
        try
        {
            QueryCache.Configure(enabled: true, maxEntries: cacheConfig.MaxEntries,
                timeQuantizeSeconds: cacheConfig.TimeQuantizeSeconds,
                defaultTtlSeconds: cacheConfig.DefaultTtlSeconds, publishEvents: false);

            QueryCache.Invalidate("it_fix_cache");
            for (var i = 0; i < 2; i++)
                Assert.True((await Mssql.Query<FixCacheRow>()
                    .Where(r => r.N >= 1)
                    .WithCache(TimeSpan.FromMinutes(10))
                    .ToListAsync()).IsSuccess);

            await Task.Delay(150);
            Assert.Equal(0, Volatile.Read(ref hits));
            Assert.Equal(0, Volatile.Read(ref misses));

            QueryCache.Configure(enabled: true, maxEntries: cacheConfig.MaxEntries,
                timeQuantizeSeconds: cacheConfig.TimeQuantizeSeconds,
                defaultTtlSeconds: cacheConfig.DefaultTtlSeconds, publishEvents: true);

            QueryCache.Invalidate("it_fix_cache");
            for (var i = 0; i < 2; i++)
                Assert.True((await Mssql.Query<FixCacheRow>()
                    .Where(r => r.N >= 2)
                    .WithCache(TimeSpan.FromMinutes(10))
                    .ToListAsync()).IsSuccess);

            for (var i = 0; i < 40 && (Volatile.Read(ref hits) == 0 || Volatile.Read(ref misses) == 0); i++)
                await Task.Delay(25);
            Assert.True(Volatile.Read(ref misses) >= 1, "expected a CacheMissEvent once publishing is on");
            Assert.True(Volatile.Read(ref hits) >= 1, "expected a CacheHitEvent once publishing is on");
        }
        finally
        {
            QueryCache.Configure(enabled: true, maxEntries: cacheConfig.MaxEntries,
                timeQuantizeSeconds: cacheConfig.TimeQuantizeSeconds,
                defaultTtlSeconds: cacheConfig.DefaultTtlSeconds, publishEvents: cacheConfig.PublishEvents);
        }
    }

    private async Task SeedCacheTableAsync()
    {
        if ((await Mssql.SqlScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'it_fix_cache'")).Value == 1)
        {
            await _fx.Exec("DELETE FROM [dbo].[it_fix_cache]");
        }
        else
        {
            await Mssql.SchemaState.RemoveStateAsync("it_fix_cache");
            Assert.True((await Mssql.SyncTableAsync<FixCacheRow>(createBackup: false)).IsSuccess);
        }

        await Mssql.GetRepository<FixCacheRow>().InsertManyAsync(
            Enumerable.Range(1, 3).Select(n => new FixCacheRow { N = n }).ToList());
        QueryCache.Invalidate("it_fix_cache");
    }

    // ── C: BackupDirectory ───────────────────────────────────────────────────

    [DbFact]
    public async Task BackupDirectory_redirects_schema_backups()
    {
        // The default location is derived from the API, never guessed: the latest backup file
        // written with no override tells us where "DataDirectory/backups" actually is.
        Assert.True((await Mssql.BackupManager.BackupTableSchemaAsync("dbo.it_customer")).Value);
        var defaultFile = Mssql.BackupManager.GetLatestBackupFile("dbo.it_customer");
        Assert.NotNull(defaultFile);
        var defaultDir = Path.GetDirectoryName(defaultFile)!;

        var redirected = Path.Combine(Path.GetTempPath(), "cl_mssql_backup_" + Guid.NewGuid().ToString("N"));
        var original = Db.BackupDirectory;
        try
        {
            Db.BackupDirectory = redirected;
            Assert.True((await Mssql.BackupManager.BackupTableSchemaAsync("dbo.it_customer")).Value);

            var moved = Mssql.BackupManager.GetLatestBackupFile("dbo.it_customer");
            Assert.NotNull(moved);
            Assert.Equal(redirected, Path.GetDirectoryName(moved));
            Assert.NotEqual(defaultDir, Path.GetDirectoryName(moved));
            Assert.True(File.Exists(moved));
        }
        finally
        {
            Db.BackupDirectory = original;
            try { if (Directory.Exists(redirected)) Directory.Delete(redirected, recursive: true); }
            catch { /* best effort */ }
        }

        // Cleared again, lookups go back to the default directory.
        Assert.Equal(defaultDir, Path.GetDirectoryName(Mssql.BackupManager.GetLatestBackupFile("dbo.it_customer")));
    }

    // ── C: MaxInClauseValues ─────────────────────────────────────────────────

    /// <summary>
    /// The cap is advisory by design: exceeding it warns but must not chunk, truncate or throw,
    /// because callers already exceed it today.
    /// </summary>
    [DbFact]
    public async Task MaxInClauseValues_warns_without_changing_the_result()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fix_batch]");
        await Mssql.SchemaState.RemoveStateAsync("it_fix_batch");
        Assert.True((await Mssql.SyncTableAsync<FixBatchRow>(createBackup: false)).IsSuccess);
        await Mssql.GetRepository<FixBatchRow>().InsertManyAsync(
            Enumerable.Range(1, 6).Select(n => new FixBatchRow { N = n }).ToList());

        var wanted = new[] { 1, 2, 3, 4 };
        var original = Db.MaxInClauseValues;
        try
        {
            Db.MaxInClauseValues = 2;                 // the list below is twice the cap

            var overCap = await Mssql.Query<FixBatchRow>().Where(r => wanted.Contains(r.N)).ToListAsync();
            Assert.True(overCap.IsSuccess, overCap.Error?.ToString());
            Assert.Equal(4, overCap.Value!.Count);    // nothing chunked away

            var viaRepository = await Mssql.GetRepository<FixBatchRow>().FindAsync(r => wanted.Contains(r.N));
            Assert.True(viaRepository.IsSuccess, viaRepository.Error?.ToString());
            Assert.Equal(4, viaRepository.Value!.Count);
        }
        finally
        {
            Db.MaxInClauseValues = original;
        }
    }

    // ── C: the two fields that stay declared but do nothing ──────────────────

    [Fact]
    public void Obsolete_configuration_fields_still_compile_and_round_trip()
    {
#pragma warning disable CS0618 // deliberately exercising the obsolete members
        var database = new SqlServerDatabaseConfig { PreparedStatementCacheSize = 512 };
        Assert.Equal(512, database.PreparedStatementCacheSize);

        var cache = new CacheConfiguration { MaxMemoryMb = 64 };
        Assert.Equal(64, cache.MaxMemoryMb);

        // Both carry an [Obsolete] message pointing at the real control.
        Assert.Contains("connection string",
            typeof(SqlServerDatabaseConfig).GetProperty(nameof(SqlServerDatabaseConfig.PreparedStatementCacheSize))!
                .GetCustomAttribute<ObsoleteAttribute>()!.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaxEntries",
            typeof(CacheConfiguration).GetProperty(nameof(CacheConfiguration.MaxMemoryMb))!
                .GetCustomAttribute<ObsoleteAttribute>()!.Message!, StringComparison.Ordinal);
#pragma warning restore CS0618
    }

    [Fact]
    public void Newly_wired_configuration_fields_are_validated()
    {
        var config = new SqlServerDatabaseConfig
        {
            Database = "db", Username = "u",
            MaxInClauseValues = 0, QueryTimeoutMs = -1, DefaultStringSize = 9000
        };
        var result = config.Validate();
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("MaxInClauseValues", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("QueryTimeoutMs", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("DefaultStringSize", StringComparison.Ordinal));
    }
}
