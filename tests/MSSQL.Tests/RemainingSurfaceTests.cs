using CL.MSSQL;
using CL.MSSQL.Events;
using CL.MSSQL.Models;
using CL.MSSQL.Services;
using Xunit;

namespace MSSQL.Tests;

// The last members with no caller anywhere in the suite: offset-paging metadata, the
// Limit/Offset aliases on a typed join, the table-sync event, the retention worker's
// background loop, and the pool registry's shutdown path.

[Table(Name = "it_page", Schema = "dbo")]
public sealed class PageRow
{
    [Column(Name = "id", DataType = DataType.Int, Primary = true, AutoIncrement = true)]
    public int Id { get; set; }

    [Column(Name = "n", DataType = DataType.Int, NotNull = true)]
    public int N { get; set; }
}

[Table(Name = "it_retain_loop", Schema = "dbo")]
[RetainDays(30, nameof(CreatedUtc))]
public sealed class RetainLoopRow
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "created_utc", DataType = DataType.DateTime2, NotNull = true)]
    public DateTime CreatedUtc { get; set; }
}

[Table(Name = "it_fk_parent", Schema = "dbo")]
public sealed class FkParent
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "name", DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    public string Name { get; set; } = "";
}

[Table(Name = "it_fk_child", Schema = "dbo")]
public sealed class FkChild
{
    [Column(Name = "id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "parent_id", DataType = DataType.BigInt, NotNull = true)]
    [ForeignKey("it_fk_parent", "id", OnDelete = ForeignKeyAction.Cascade,
                OnUpdate = ForeignKeyAction.NoAction, ConstraintName = "fk_it_child_parent")]
    public long ParentId { get; set; }

    [Column(Name = "tag", DataType = DataType.NVarChar, Size = 40, NotNull = true)]
    [Index(Name = "ix_it_fk_child_tag")]
    public string Tag { get; set; } = "";
}

[Collection("codelogic")]
public sealed class RemainingSurfaceTests
{
    private readonly MSSQLRuntimeFixture _fx;
    private MSSQLLibrary Mssql => _fx.Mysql;

    public RemainingSurfaceTests(MSSQLRuntimeFixture fx) => _fx = fx;

    [DbFact]
    public async Task Offset_paging_reports_consistent_metadata()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_page]");
        Assert.True((await Mssql.SyncTableAsync<PageRow>(createBackup: false)).IsSuccess);
        await Mssql.GetRepository<PageRow>().InsertManyAsync(
            Enumerable.Range(1, 7).Select(n => new PageRow { N = n }).ToList());

        var middle = await Mssql.Query<PageRow>().OrderBy(r => r.N).ToPagedListAsync(page: 2, pageSize: 3);
        Assert.True(middle.IsSuccess, middle.Error?.ToString());
        var p = middle.Value!;

        Assert.Equal([4, 5, 6], p.Items.Select(r => r.N));
        Assert.Equal(2, p.PageNumber);
        Assert.Equal(3, p.PageSize);
        Assert.Equal(7, p.TotalItems);
        Assert.Equal(3, p.TotalPages);          // 7 rows over pages of 3 rounds up
        Assert.True(p.HasPreviousPage);
        Assert.True(p.HasNextPage);

        var first = (await Mssql.Query<PageRow>().OrderBy(r => r.N).ToPagedListAsync(1, 3)).Value!;
        Assert.False(first.HasPreviousPage);

        var last = (await Mssql.Query<PageRow>().OrderBy(r => r.N).ToPagedListAsync(3, 3)).Value!;
        Assert.Single(last.Items);
        Assert.False(last.HasNextPage);
    }

    /// <summary>
    /// <c>Limit</c>/<c>Offset</c> are aliases for <c>Take</c>/<c>Skip</c> on a typed join —
    /// present for symmetry with the plain query builder, and never called until now.
    /// </summary>
    [DbFact]
    public async Task Join_limit_and_offset_page_the_result()
    {
        var all = await Mssql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total })
            .OrderBy((o, c) => o.Total)
            .ToListAsync();
        Assert.True(all.IsSuccess, all.Error?.ToString());
        Assert.Equal(3, all.Value!.Count);

        var page = await Mssql.Query<Order>()
            .Join<Customer, long, OrderView>(o => o.CustomerId, c => c.Id,
                (o, c) => new OrderView { OrderId = o.Id, Customer = c.Name, Total = o.Total })
            .OrderBy((o, c) => o.Total)
            .Offset(1)
            .Limit(1)
            .ToListAsync();
        Assert.True(page.IsSuccess, page.Error?.ToString());

        Assert.Single(page.Value!);
        Assert.Equal(all.Value[1].OrderId, page.Value![0].OrderId);
    }

    [DbFact]
    public async Task Syncing_a_table_publishes_a_TableSyncedEvent()
    {
        var bus = Mssql.Events ?? throw new InvalidOperationException("no event bus");
        var seen = new List<TableSyncedEvent>();
        bus.Subscribe<TableSyncedEvent>(e => { lock (seen) seen.Add(e); });

        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_page]");
        Assert.True((await Mssql.SyncTableAsync<PageRow>(createBackup: false)).IsSuccess);

        for (var i = 0; i < 40 && seen.Count == 0; i++) await Task.Delay(25);

        var evt = Assert.Single(seen, e => e.TableName.Contains("it_page", StringComparison.OrdinalIgnoreCase));
        Assert.True(evt.Created, "a dropped table is recreated, not altered");
        Assert.NotEmpty(evt.Operations);
        Assert.True(evt.Duration > TimeSpan.Zero);
    }

    /// <summary>
    /// <see cref="RetentionWorker.Start"/> runs the same purge on a timer. The loop waits
    /// out an initial delay before its first pass, so this only asserts that starting is
    /// safe, idempotent, and that stopping cancels cleanly — the purge itself is covered
    /// by the synchronous <c>RunOnceAsync</c> test.
    /// </summary>
    [DbFact]
    public async Task Retention_worker_starts_and_stops_cleanly()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_retain_loop]");
        Assert.True((await Mssql.SyncTableAsync<RetainLoopRow>(createBackup: false)).IsSuccess);

        var worker = new RetentionWorker(Mssql.ConnectionManager, null, [typeof(RetainLoopRow)]);
        Assert.True(worker.HasWork);

        worker.Start();
        worker.Start();                      // a second call must be a no-op, not a second loop
        await Task.Delay(50);
        await worker.DisposeAsync();         // cancels the loop and awaits it

        // A worker with nothing to purge never starts a loop at all.
        await using var idle = new RetentionWorker(Mssql.ConnectionManager, null, [typeof(PageRow)]);
        Assert.False(idle.HasWork);
        idle.Start();
    }

    [DbFact]
    public async Task Pool_registry_disposes_every_pool()
    {
        Mssql.RegisterCachePool($"disp-{Guid.NewGuid():N}", TimeSpan.FromMinutes(5));
        Assert.NotEmpty(Mssql.GetCachePoolStats());

        await SmartCachePoolRegistry.DisposeAllAsync();
        Assert.Empty(Mssql.GetCachePoolStats());
    }

    /// <summary>
    /// Foreign keys and named indexes had no coverage in this suite at all: the generated
    /// DDL emits the constraint name, the referential actions, and the index verbatim, so
    /// a dialect slip here only shows up against a live server.
    /// </summary>
    [DbFact]
    public async Task Foreign_key_and_named_index_are_created_and_enforced()
    {
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fk_child]");
        await _fx.Exec("DROP TABLE IF EXISTS [dbo].[it_fk_parent]");
        Assert.True((await Mssql.SyncTableAsync<FkParent>(createBackup: false)).IsSuccess);
        Assert.True((await Mssql.SyncTableAsync<FkChild>(createBackup: false)).IsSuccess);

        var deleteRule = await Mssql.SqlScalarAsync<string>(
            "SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE name = 'fk_it_child_parent'");
        Assert.Equal("CASCADE", deleteRule.Value);

        var updateRule = await Mssql.SqlScalarAsync<string>(
            "SELECT update_referential_action_desc FROM sys.foreign_keys WHERE name = 'fk_it_child_parent'");
        Assert.Equal("NO_ACTION", updateRule.Value);

        var index = await Mssql.SqlScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.indexes WHERE name = 'ix_it_fk_child_tag'");
        Assert.Equal(1, index.Value);

        var parent = new FkParent { Name = "p" };
        Assert.True((await Mssql.GetRepository<FkParent>().InsertAsync(parent)).IsSuccess);
        await Mssql.GetRepository<FkChild>().InsertAsync(new FkChild { ParentId = parent.Id, Tag = "a" });

        // An orphan is rejected by the constraint, not silently accepted.
        var orphan = await Mssql.GetRepository<FkChild>()
            .InsertAsync(new FkChild { ParentId = parent.Id + 9999, Tag = "b" });
        Assert.False(orphan.IsSuccess);

        // ON DELETE CASCADE removes the child with its parent.
        Assert.True((await Mssql.GetRepository<FkParent>().DeleteAsync(parent.Id)).IsSuccess);
        Assert.Equal(0, (await Mssql.GetRepository<FkChild>().CountAsync()).Value);
    }
}
