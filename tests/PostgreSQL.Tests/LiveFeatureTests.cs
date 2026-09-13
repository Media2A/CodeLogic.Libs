using CL.PostgreSQL;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using CL.PostgreSQL.Services;
using Xunit;

namespace PostgreSQL.Tests;

// Live coverage for the subsystems added when CL.PostgreSQL was rebuilt on the CL.MySQL2
// architecture. None of this SQL had ever reached a server before these tests: the offline
// suite verifies the SQL by construction, which catches dialect leftovers but not statements
// PostgreSQL parses differently from how they were written.

// ── Entities ─────────────────────────────────────────────────────────────────────

[Table(Name = "it_live_item", Schema = "public")]
public sealed class LiveItem
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public long Id { get; set; }

    [Column(Name = "sku", Size = 64, Unique = true, NotNull = true)]
    public string Sku { get; set; } = "";

    [Column(Name = "name", Size = 100, NotNull = true)]
    public string Name { get; set; } = "";

    [Column(Name = "qty", NotNull = true)]
    public int Qty { get; set; }

    [Column(Name = "price", DataType = DataType.Numeric, Precision = 12, Scale = 2)]
    public decimal Price { get; set; }

    [Column(Name = "external_id")]
    public Guid ExternalId { get; set; }

    [Column(Name = "created_utc", Index = true)]
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Column(Name = "touched_utc", OnUpdateCurrentTimestamp = true)]
    public DateTime TouchedUtc { get; set; } = DateTime.UtcNow;

    [Column(Name = "payload", DataType = DataType.Jsonb)]
    public string? Payload { get; set; }

    [Column(Name = "note", Size = 200)]
    public string? Note { get; set; }
}

[Table(Name = "it_live_soft", Schema = "public")]
[SoftDelete(nameof(DeletedUtc))]
public sealed class LiveSoft
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "label", Size = 50, NotNull = true)] public string Label { get; set; } = "";
    [Column(Name = "deleted_utc")] public DateTime? DeletedUtc { get; set; }
}

// ── Tests ────────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class LiveFeatureTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveFeatureTests(PostgreSQLRuntimeFixture fx) => _fx = fx;

    private async Task<PostgreSQLLibrary> FreshAsync()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_live_item\" CASCADE");
        var sync = await lib.SyncTableAsync<LiveItem>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);
        return lib;
    }

    // ── Schema sync ──────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Sync_creates_native_postgres_types()
    {
        var lib = await FreshAsync();

        var types = await lib.SqlQueryAsync<ColRow>("""
            SELECT a.attname AS "Name", pg_catalog.format_type(a.atttypid, a.atttypmod) AS "Type"
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = 'it_live_item'
              AND a.attnum > 0 AND NOT a.attisdropped
            """);
        Assert.True(types.IsSuccess, types.Error?.Message);

        var map = types.Value!.ToDictionary(r => r.Name, r => r.Type, StringComparer.Ordinal);
        Assert.Equal("uuid", map["external_id"]);
        Assert.Equal("timestamp with time zone", map["created_utc"]);
        Assert.Equal("jsonb", map["payload"]);
        Assert.Equal("numeric(12,2)", map["price"]);
        Assert.Equal("character varying(64)", map["sku"]);
    }

    /// <summary>
    /// The regression that matters most. The pre-rebuild analyzer compared database types
    /// against generated DDL as raw strings, so it re-issued ALTER COLUMN ... TYPE for nearly
    /// every column on every sync, rewriting the table each boot. A second sync over an
    /// unchanged model must do nothing at all.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Second_sync_of_an_unchanged_model_is_a_no_op()
    {
        var lib = await FreshAsync();

        var second = await lib.SyncTableAsync<LiveItem>(createBackup: false);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.True(second.Value!.Success, string.Join("; ", second.Value.Errors));

        var ddl = second.Value.Operations
            .Where(op => op.Contains("ALTER", StringComparison.OrdinalIgnoreCase)
                      || op.Contains("CREATE", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(ddl.Length == 0,
            "Second sync of an unchanged model emitted DDL: " + string.Join(" | ", ddl));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Third_sync_is_also_a_no_op()
    {
        // Guards against a two-cycle oscillation that a single repeat would miss.
        var lib = await FreshAsync();
        await lib.SyncTableAsync<LiveItem>(createBackup: false);
        var third = await lib.SyncTableAsync<LiveItem>(createBackup: false);

        Assert.True(third.IsSuccess, third.Error?.Message);
        var ddl = third.Value!.Operations
            .Where(op => op.Contains("ALTER", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(ddl.Length == 0, "Third sync emitted DDL: " + string.Join(" | ", ddl));
    }

    /// <summary>
    /// OnUpdateCurrentTimestamp has no PostgreSQL column clause, so sync emits a plpgsql
    /// trigger function in a dollar-quoted body. Dollar quoting is the part most likely to
    /// be mangled by a driver that rewrites parameter placeholders.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Touch_trigger_is_created_and_fires_on_update()
    {
        var lib = await FreshAsync();

        var triggers = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            WHERE c.relname = 'it_live_item' AND NOT t.tgisinternal
            """);
        Assert.True(triggers.IsSuccess, triggers.Error?.Message);
        Assert.Equal(1, triggers.Value);

        var repo = lib.GetRepository<LiveItem>();
        var created = await repo.InsertAsync(new LiveItem { Sku = "T1", Name = "trig", Qty = 1 });
        Assert.True(created.IsSuccess, created.Error?.Message);

        var before = await lib.SqlScalarAsync<DateTime>(
            "SELECT touched_utc FROM public.it_live_item WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = created.Value!.Id });

        await Task.Delay(50);
        await lib.ExecuteSqlAsync("UPDATE public.it_live_item SET qty = 99 WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = created.Value.Id });

        var after = await lib.SqlScalarAsync<DateTime>(
            "SELECT touched_utc FROM public.it_live_item WHERE id = @id",
            new Dictionary<string, object?> { ["@id"] = created.Value.Id });

        Assert.True(after.Value > before.Value,
            $"trigger did not advance touched_utc ({before.Value:O} -> {after.Value:O})");
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Adding_a_missing_column_alters_rather_than_recreates()
    {
        var lib = await FreshAsync();
        await lib.ExecuteSqlAsync("ALTER TABLE public.it_live_item DROP COLUMN note");
        // Sync is CRC-gated on the model, so an unchanged model short-circuits before the
        // catalog is read. Clearing the sentinel is what a real model change would do, and
        // is what forces the diff path under test here.
        await lib.ExecuteSqlAsync(
            "DELETE FROM public.__schema_state WHERE \"TableName\" = 'it_live_item'");

        var sync = await lib.SyncTableAsync<LiveItem>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());
        Assert.Contains(sync.Value!.Operations, op => op.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase));
        // An ADD, not a rebuild: no table was dropped.
        Assert.DoesNotContain(sync.Value.Operations, op => op.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase));

        var exists = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='public' AND table_name='it_live_item' AND column_name='note'
            """);
        Assert.Equal(1, exists.Value);
    }

    /// <summary>
    /// Documents the deliberate limitation the previous test works around: while a model's
    /// CRC still matches the stored sentinel, schema drift introduced outside the library
    /// is not detected. This is the trade that makes an unchanged model cost nothing at
    /// startup, and it is worth pinning so the behaviour is not "fixed" by accident.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Out_of_band_drift_is_not_detected_while_the_crc_matches()
    {
        var lib = await FreshAsync();
        await lib.ExecuteSqlAsync("ALTER TABLE public.it_live_item DROP COLUMN note");

        var sync = await lib.SyncTableAsync<LiveItem>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);
        Assert.Empty(sync.Value!.Operations);
    }

    // ── Upserts ──────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Upsert_inserts_then_updates_on_the_unique_key()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();

        var first = await repo.UpsertAsync(new LiveItem { Sku = "U1", Name = "first", Qty = 1 });
        Assert.True(first.IsSuccess, first.Error?.Message);

        var second = await repo.UpsertAsync(new LiveItem { Sku = "U1", Name = "second", Qty = 7 });
        Assert.True(second.IsSuccess, second.Error?.Message);

        var rows = await repo.GetAllAsync();
        Assert.Single(rows.Value!);
        Assert.Equal("second", rows.Value![0].Name);
        Assert.Equal(7, rows.Value![0].Qty);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task UpsertMany_merges_a_batch()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();

        await repo.UpsertManyAsync([
            new LiveItem { Sku = "B1", Name = "one", Qty = 1 },
            new LiveItem { Sku = "B2", Name = "two", Qty = 2 },
        ]);
        await repo.UpsertManyAsync([
            new LiveItem { Sku = "B2", Name = "two-updated", Qty = 22 },
            new LiveItem { Sku = "B3", Name = "three", Qty = 3 },
        ]);

        var rows = await repo.GetAllAsync();
        Assert.Equal(3, rows.Value!.Count);
        Assert.Equal("two-updated", rows.Value!.Single(r => r.Sku == "B2").Name);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task UpsertWithIncrements_accumulates()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();

        var seed = new LiveItem { Sku = "INC", Name = "counter", Qty = 5 };
        await repo.UpsertWithIncrementsAsync(seed, [nameof(LiveItem.Qty)]);
        await repo.UpsertWithIncrementsAsync(seed, [nameof(LiveItem.Qty)]);
        await repo.UpsertWithIncrementsAsync(seed, [nameof(LiveItem.Qty)]);

        var rows = await repo.GetAllAsync();
        Assert.Single(rows.Value!);
        Assert.Equal(15, rows.Value![0].Qty);   // 5 inserted, then +5 and +5
    }

    // ── Bulk insert ──────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task InsertMany_batches_a_large_set()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();

        var items = Enumerable.Range(0, 1500)
            .Select(i => new LiveItem { Sku = $"S{i}", Name = $"n{i}", Qty = i })
            .ToList();

        var inserted = await repo.InsertManyAsync(items);
        Assert.True(inserted.IsSuccess, inserted.Error?.Message);

        var count = await repo.CountAsync();
        Assert.Equal(1500, count.Value);
    }

    // ── Query surface ────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Projection_grouping_and_aggregates_execute()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();
        await repo.InsertManyAsync([
            new LiveItem { Sku = "G1", Name = "a", Qty = 1, Price = 10m },
            new LiveItem { Sku = "G2", Name = "a", Qty = 2, Price = 20m },
            new LiveItem { Sku = "G3", Name = "b", Qty = 3, Price = 30m },
        ]);

        var projected = await lib.Query<LiveItem>().Select(i => new { i.Sku, i.Qty }).ToListAsync();
        Assert.Equal(3, projected.Value!.Count);

        var sum = await lib.Query<LiveItem>().SumAsync(i => i.Qty);
        Assert.Equal(6, sum.Value);

        var avg = await lib.Query<LiveItem>().AverageAsync(i => i.Qty);
        Assert.Equal(2.0, avg.Value, 3);

        var max = await lib.Query<LiveItem>().MaxAsync(i => i.Price);
        Assert.Equal(30m, max.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Empty_and_populated_in_clauses_both_execute()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();
        await repo.InsertManyAsync([
            new LiveItem { Sku = "I1", Name = "x", Qty = 1 },
            new LiveItem { Sku = "I2", Name = "y", Qty = 2 },
        ]);

        var wanted = new[] { "I1" };
        var hit = await lib.Query<LiveItem>().Where(i => wanted.Contains(i.Sku)).ToListAsync();
        Assert.True(hit.IsSuccess, hit.Error?.Message);
        Assert.Single(hit.Value!);

        // "IN ()" is a syntax error; an empty set must become a false literal.
        var none = Array.Empty<string>();
        var miss = await lib.Query<LiveItem>().Where(i => none.Contains(i.Sku)).ToListAsync();
        Assert.True(miss.IsSuccess, miss.Error?.Message);
        Assert.Empty(miss.Value!);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Like_filters_treat_wildcards_in_input_literally()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();
        await repo.InsertManyAsync([
            new LiveItem { Sku = "L1", Name = "100% cotton", Qty = 1 },
            new LiveItem { Sku = "L2", Name = "100 percent", Qty = 2 },
        ]);

        var term = "100%";
        var rows = await lib.Query<LiveItem>().Where(i => i.Name.StartsWith(term)).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Single(rows.Value!);          // the '%' is data, not a wildcard
        Assert.Equal("L1", rows.Value![0].Sku);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Sql_functions_translate_and_run_in_a_grouped_projection()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();
        await repo.InsertManyAsync([
            new LiveItem { Sku = "F1", Name = "fn", Qty = 1, CreatedUtc = new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc) },
            new LiveItem { Sku = "F2", Name = "fn", Qty = 2, CreatedUtc = new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc) },
            new LiveItem { Sku = "F3", Name = "fn", Qty = 4, CreatedUtc = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc) },
        ]);

        // SqlFn is translated in grouped projections; EXTRACT(YEAR FROM ...) must both
        // parse and bucket correctly.
        var rows = await lib.Query<LiveItem>()
            .GroupBy(i => SqlFn.Year(i.CreatedUtc))
            .Select(g => new { Year = g.Key, Total = g.Sum(i => i.Qty) })
            .ToListAsync();

        Assert.True(rows.IsSuccess, rows.Error?.Message);
        var byYear = rows.Value!.ToDictionary(r => r.Year, r => r.Total);
        Assert.Equal(3, byYear[2026]);
        Assert.Equal(4, byYear[2025]);
    }

    /// <summary>
    /// SqlFn in a plain (ungrouped) Select is not supported by the projection compiler.
    /// Pinned so the documented limitation and the actual behaviour stay in step.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Sql_functions_are_not_supported_in_an_ungrouped_projection()
    {
        var lib = await FreshAsync();
        Assert.Throws<NotSupportedException>(() =>
            lib.Query<LiveItem>().Select(i => new { Year = SqlFn.Year(i.CreatedUtc) }));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Cursor_paging_walks_every_row_exactly_once()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();
        await repo.InsertManyAsync(Enumerable.Range(0, 25)
            .Select(i => new LiveItem { Sku = $"C{i:D3}", Name = "c", Qty = i })
            .ToList());

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var q = lib.Query<LiveItem>().OrderBy(i => i.Sku);
            if (cursor is not null) q = q.After(cursor);
            var result = await q.ToCursorPagedListAsync(pageSize: 7);
            Assert.True(result.IsSuccess, result.Error?.Message);

            seen.AddRange(result.Value!.Items.Select(i => i.Sku));
            cursor = result.Value.NextCursor;
            if (cursor is null) break;
        }

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        Assert.Equal(seen.OrderBy(s => s, StringComparer.Ordinal), seen);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Bulk_update_and_delete_execute()
    {
        var lib = await FreshAsync();
        var repo = lib.GetRepository<LiveItem>();
        await repo.InsertManyAsync([
            new LiveItem { Sku = "M1", Name = "m", Qty = 1 },
            new LiveItem { Sku = "M2", Name = "m", Qty = 2 },
        ]);

        var updated = await lib.Query<LiveItem>().Where(i => i.Qty >= 1)
            .UpdateAsync(i => new LiveItem { Qty = i.Qty + 10 });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(2, updated.Value);

        var sum = await lib.Query<LiveItem>().SumAsync(i => i.Qty);
        Assert.Equal(23, sum.Value);

        var deleted = await lib.Query<LiveItem>().Where(i => i.Sku == "M1").DeleteAsync();
        Assert.Equal(1, deleted.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Dictionary_update_rejects_an_unknown_column()
    {
        var lib = await FreshAsync();
        await lib.GetRepository<LiveItem>().InsertAsync(new LiveItem { Sku = "D1", Name = "d", Qty = 1 });

        // The allow-list stops a caller-supplied key reaching the SQL text. It surfaces as a
        // failed Result, not an exception -- the library reserves exceptions for the
        // unexpected.
        var result = await lib.Query<LiveItem>()
            .UpdateAsync(new Dictionary<string, object?> { ["qty\"; DROP TABLE public.it_live_item --"] = 1 });

        Assert.True(result.IsFailure);

        // And the injection attempt did nothing: the table and its row are intact.
        var still = await lib.GetRepository<LiveItem>().CountAsync();
        Assert.Equal(1, still.Value);
    }

    // ── Transactions ─────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Transaction_rolls_back_when_not_committed()
    {
        var lib = await FreshAsync();

        await using (var tx = await lib.BeginTransactionAsync())
        {
            var repo = lib.GetRepository<LiveItem>(tx);
            await repo.InsertAsync(new LiveItem { Sku = "TX1", Name = "rollback", Qty = 1 });
            // no commit
        }

        var count = await lib.GetRepository<LiveItem>().CountAsync();
        Assert.Equal(0, count.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Transaction_persists_on_commit()
    {
        var lib = await FreshAsync();

        await using (var tx = await lib.BeginTransactionAsync())
        {
            var repo = lib.GetRepository<LiveItem>(tx);
            await repo.InsertAsync(new LiveItem { Sku = "TX2", Name = "commit", Qty = 1 });
            await tx.CommitAsync();
        }

        var count = await lib.GetRepository<LiveItem>().CountAsync();
        Assert.Equal(1, count.Value);
    }

    // ── Soft delete ──────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Soft_delete_hides_rows_but_keeps_them()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_live_soft\" CASCADE");
        var sync = await lib.SyncTableAsync<LiveSoft>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.Message);

        var repo = lib.GetRepository<LiveSoft>();
        var row = await repo.InsertAsync(new LiveSoft { Label = "gone" });
        await repo.DeleteAsync(row.Value!.Id);

        var visible = await repo.GetAllAsync();
        Assert.Empty(visible.Value!);

        var raw = await lib.SqlScalarAsync<long>("SELECT count(*) FROM public.it_live_soft");
        Assert.Equal(1, raw.Value);          // still physically present

        var including = await lib.Query<LiveSoft>().IncludeDeleted().ToListAsync();
        Assert.Single(including.Value!);
    }

    // ── Advisory lock ────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Advisory_lock_is_exclusive_then_released()
    {
        var cm = Lib.ConnectionManager;

        await using (var first = await SchemaSyncLock.AcquireAsync(cm, timeoutSeconds: 5))
        {
            Assert.True(first.Acquired);

            await using var second = await SchemaSyncLock.AcquireAsync(cm, timeoutSeconds: 1);
            Assert.False(second.Acquired);   // the timeout must expire, not hang
        }

        await using var third = await SchemaSyncLock.AcquireAsync(cm, timeoutSeconds: 5);
        Assert.True(third.Acquired);         // released with the first scope
    }

    // ── Schema backup ────────────────────────────────────────────────────────────

    /// <summary>
    /// PostgreSQL has no SHOW CREATE TABLE, so BackupManager rebuilds the DDL from
    /// pg_catalog. Exercised here because that reconstruction is entirely hand-written and
    /// shares the attidentity read that broke the analyzer.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Backup_reconstructs_replayable_ddl()
    {
        var lib = await FreshAsync();

        var backup = await lib.BackupManager.BackupTableSchemaAsync("it_live_item", "public");
        Assert.True(backup.IsSuccess, backup.Error?.ToString());
        Assert.True(backup.Value);

        var file = lib.BackupManager.GetLatestBackupFile("it_live_item", "public");
        Assert.NotNull(file);
        var ddl = await File.ReadAllTextAsync(file!);

        // The reconstruction must capture the real shape, not a stub.
        Assert.Contains("CREATE TABLE \"public\".\"it_live_item\"", ddl);
        Assert.Contains("GENERATED BY DEFAULT AS IDENTITY", ddl);
        Assert.Contains("\"external_id\" uuid", ddl);
        Assert.Contains("\"price\" numeric(12,2)", ddl);
        Assert.Contains("PRIMARY KEY", ddl);
        Assert.Contains("UNIQUE", ddl);

        // And it must actually replay: drop the table and rebuild it from the backup.
        var restored = await lib.BackupManager.RestoreTableSchemaAsync("it_live_item", "public");
        Assert.True(restored.IsSuccess, restored.Error?.ToString());

        var cols = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'it_live_item'
            """);
        Assert.Equal(10, cols.Value);   // id, sku, name, qty, price, external_id, created_utc, touched_utc, payload, note
    }

    public sealed class ColRow
    {
        [Column(Name = "Name")] public string Name { get; set; } = "";
        [Column(Name = "Type")] public string Type { get; set; } = "";
    }
}
