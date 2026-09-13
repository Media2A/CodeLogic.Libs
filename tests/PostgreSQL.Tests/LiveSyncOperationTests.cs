using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// The ALTER side of schema sync. Each statement shape is written by hand in SchemaAnalyzer
// and differs from the MySQL original: PostgreSQL splits MODIFY COLUMN into separate type,
// nullability and default actions, renames without restating the type, and creates and drops
// indexes outside ALTER TABLE entirely.
//
// Each test puts the table into a deliberately wrong shape, clears the CRC sentinel so the
// diff actually runs, then asserts the library corrected it.

[Table(Name = "it_ops", Schema = "public")]
[CompositeIndex("ix_ops_a_b", "col_a", "col_b")]
[CompositeIndex("uq_ops_code", "code", Unique = true)]
public sealed class OpsRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "code", Size = 32, NotNull = true)] public string Code { get; set; } = "";
    [Column(Name = "col_a", Size = 40)] public string? ColA { get; set; }
    [Column(Name = "col_b")] public int? ColB { get; set; }
    [Column(Name = "widened", DataType = DataType.BigInt)] public long Widened { get; set; }
    [Column(Name = "must_fill", NotNull = true)] public int MustFill { get; set; }
    [Column(Name = "with_default", DefaultValue = "42")] public int WithDefault { get; set; }
    [Column(Name = "indexed", Size = 20, Index = true)] public string? Indexed { get; set; }
    [Column(Name = "uniq", Size = 20, Unique = true)] public string? Uniq { get; set; }
    [Column(Name = "renamed", Size = 20, PreviousName = "old_name")] public string? Renamed { get; set; }
}

[Table(Name = "it_ops_parent", Schema = "public")]
public sealed class OpsParent
{
    [Column(Name = "id", Primary = true)] public long Id { get; set; }
}

[Table(Name = "it_ops_child", Schema = "public")]
public sealed class OpsChild
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "parent_id")]
    [ForeignKey("it_ops_parent", "id", OnDelete = ForeignKeyAction.Cascade)]
    public long ParentId { get; set; }
}

[Collection("codelogic")]
public sealed class LiveSyncOperationTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveSyncOperationTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>Forces the next sync to run a real catalog diff instead of the CRC fast-path.</summary>
    private async Task ClearSentinelAsync(string table) =>
        await Lib.ExecuteSqlAsync(
            "DELETE FROM public.__schema_state WHERE \"TableName\" = @t",
            new Dictionary<string, object?> { ["@t"] = table });

    private async Task<IReadOnlyList<string>> SyncAsync()
    {
        var sync = await Lib.SyncTableAsync<OpsRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());
        foreach (var op in sync.Value!.Operations) _out.WriteLine(op);
        return sync.Value.Operations;
    }

    private async Task<string?> ColumnTypeAsync(string column) =>
        (await Lib.SqlScalarAsync<string>("""
            SELECT pg_catalog.format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='public' AND c.relname='it_ops' AND a.attname=@c
            """, new Dictionary<string, object?> { ["@c"] = column })).Value;

    // ── Baseline ─────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Full_create_then_repeat_sync_is_stable()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        await ClearSentinelAsync("it_ops");
        var second = await SyncAsync();

        // With the sentinel gone the diff really runs; a correct diff still finds nothing.
        Assert.True(second.Count == 0, "diff over an already-correct table emitted: " + string.Join(" | ", second));
    }

    // ── Column type, nullability, default ────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Narrower_column_type_is_widened()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        await lib.ExecuteSqlAsync("ALTER TABLE public.it_ops ALTER COLUMN widened TYPE integer");
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.Contains(ops, o => o.Contains("ALTER COLUMN", StringComparison.OrdinalIgnoreCase)
                               && o.Contains("TYPE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("bigint", await ColumnTypeAsync("widened"));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Missing_not_null_is_applied()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        await lib.ExecuteSqlAsync("ALTER TABLE public.it_ops ALTER COLUMN must_fill DROP NOT NULL");
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.Contains(ops, o => o.Contains("SET NOT NULL", StringComparison.OrdinalIgnoreCase));

        var nullable = await lib.SqlScalarAsync<bool>("""
            SELECT NOT a.attnotnull FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='public' AND c.relname='it_ops' AND a.attname='must_fill'
            """);
        Assert.False(nullable.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Unwanted_not_null_is_dropped()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        await lib.ExecuteSqlAsync("UPDATE public.it_ops SET col_a = 'x' WHERE col_a IS NULL");
        await lib.ExecuteSqlAsync("ALTER TABLE public.it_ops ALTER COLUMN col_a SET NOT NULL");
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.Contains(ops, o => o.Contains("DROP NOT NULL", StringComparison.OrdinalIgnoreCase));
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Missing_default_is_restored()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        await lib.ExecuteSqlAsync("ALTER TABLE public.it_ops ALTER COLUMN with_default DROP DEFAULT");
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.Contains(ops, o => o.Contains("SET DEFAULT", StringComparison.OrdinalIgnoreCase));

        var def = await lib.SqlScalarAsync<string>("""
            SELECT pg_get_expr(d.adbin, d.adrelid) FROM pg_attrdef d
            JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
            JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='public' AND c.relname='it_ops' AND a.attname='with_default'
            """);
        Assert.Equal("42", def.Value);
    }

    /// <summary>
    /// A matching default must not be rewritten. PostgreSQL echoes defaults back with a cast
    /// ('active'::text, 42), so a naive comparison would re-issue SET DEFAULT forever.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Matching_default_is_left_alone()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.DoesNotContain(ops, o => o.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase));
    }

    // ── Rename ───────────────────────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task PreviousName_renames_in_place_and_keeps_data()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        // Put the table back to the pre-rename shape, with a row in it.
        await lib.ExecuteSqlAsync("ALTER TABLE public.it_ops RENAME COLUMN renamed TO old_name");
        var seeded = await lib.ExecuteSqlAsync(
            "INSERT INTO public.it_ops (code, widened, must_fill, old_name) VALUES ('r1', 1, 1, 'keep-me')");
        // Assert the seed landed: a silently failed insert would make the data-preservation
        // check below pass for the wrong reason.
        Assert.True(seeded.IsSuccess, seeded.Error?.ToString());
        Assert.Equal(1, seeded.Value);
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.Contains(ops, o => o.Contains("RENAME COLUMN", StringComparison.OrdinalIgnoreCase));

        // A rename, not drop-and-add: the value survived.
        var kept = await lib.SqlScalarAsync<string>(
            "SELECT renamed FROM public.it_ops WHERE code = 'r1'");
        Assert.Equal("keep-me", kept.Value);
    }

    // ── Indexes and constraints ──────────────────────────────────────────────────

    [FactRequiresEnv(Gate, Reason)]
    public async Task Missing_index_is_created()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        await lib.ExecuteSqlAsync("DROP INDEX public.idx_it_ops_indexed");
        await ClearSentinelAsync("it_ops");

        var ops = await SyncAsync();
        Assert.Contains(ops, o => o.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, (await IndexCountAsync("idx_it_ops_indexed")).Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Composite_indexes_are_created()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        Assert.Equal(1, (await IndexCountAsync("ix_ops_a_b")).Value);
        // A unique composite must be a constraint so ON CONFLICT can arbitrate on it.
        var isConstraint = await lib.SqlScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_ops_code' AND contype = 'u')
            """);
        Assert.True(isConstraint.Value);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Unique_column_becomes_a_constraint()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops\" CASCADE");
        await SyncAsync();

        var isConstraint = await lib.SqlScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_it_ops_uniq' AND contype = 'u')
            """);
        Assert.True(isConstraint.Value);

        // And it is enforced.
        await lib.ExecuteSqlAsync("INSERT INTO public.it_ops (code, widened, must_fill, uniq) VALUES ('u1', 1, 1, 'dup')");
        var clash = await lib.ExecuteSqlAsync(
            "INSERT INTO public.it_ops (code, widened, must_fill, uniq) VALUES ('u2', 1, 1, 'dup')");
        Assert.True(clash.IsFailure);
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Foreign_keys_are_created_and_enforced()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops_child\" CASCADE");
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS \"public\".\"it_ops_parent\" CASCADE");

        Assert.True((await lib.SyncTableAsync<OpsParent>(createBackup: false)).IsSuccess);
        var childSync = await lib.SyncTableAsync<OpsChild>(createBackup: false);
        Assert.True(childSync.IsSuccess, childSync.Error?.ToString());

        var fk = await lib.SqlScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.it_ops_child'::regclass
                           AND contype = 'f')
            """);
        Assert.True(fk.Value);

        // Enforced: an orphan is rejected.
        var orphan = await lib.ExecuteSqlAsync("INSERT INTO public.it_ops_child (parent_id) VALUES (9999)");
        Assert.True(orphan.IsFailure);

        // And ON DELETE CASCADE was carried across.
        await lib.ExecuteSqlAsync("INSERT INTO public.it_ops_parent (id) VALUES (1)");
        await lib.ExecuteSqlAsync("INSERT INTO public.it_ops_child (parent_id) VALUES (1)");
        await lib.ExecuteSqlAsync("DELETE FROM public.it_ops_parent WHERE id = 1");
        var remaining = await lib.SqlScalarAsync<long>("SELECT count(*) FROM public.it_ops_child");
        Assert.Equal(0, remaining.Value);
    }

    private Task<CodeLogic.Core.Results.Result<long>> IndexCountAsync(string name) =>
        Lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE schemaname='public' AND indexname=@n",
            new Dictionary<string, object?> { ["@n"] = name });
}
