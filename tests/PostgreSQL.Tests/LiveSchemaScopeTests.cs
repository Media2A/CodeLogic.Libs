using CL.PostgreSQL;
using CL.PostgreSQL.Models;
using Xunit;
using Xunit.Abstractions;

namespace PostgreSQL.Tests;

// Everything live so far has lived in "public". Schemas are the one structural feature
// PostgreSQL has that the MySQL original does not, so an entity in a non-default schema is
// the case most likely to have been written but never run.

[Table(Name = "scoped", Schema = "cl_alt")]
public sealed class ScopedRow
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "code", Size = 32, Unique = true, NotNull = true)] public string Code { get; set; } = "";
    [Column(Name = "touched", OnUpdateCurrentTimestamp = true)] public DateTime Touched { get; set; }
    [Column(Name = "tag", Size = 20, Index = true)] public string? Tag { get; set; }
}

// Same table name, different schema: proves qualification is real and not incidental.
[Table(Name = "scoped", Schema = "public")]
public sealed class ScopedRowPublic
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "code", Size = 32, NotNull = true)] public string Code { get; set; } = "";
}

[Collection("codelogic")]
public sealed class LiveSchemaScopeTests
{
    private const string Gate = "CL_PG_TEST_HOST";
    private const string Reason = "set CL_PG_TEST_HOST (+ _PORT/_DB/_USER/_PASS) to run live PostgreSQL tests";

    private readonly PostgreSQLRuntimeFixture _fx;
    private readonly ITestOutputHelper _out;
    private PostgreSQLLibrary Lib => _fx.Library ?? throw new InvalidOperationException("Runtime not booted.");

    public LiveSchemaScopeTests(PostgreSQLRuntimeFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>
    /// Sync creates the schema it needs. The library already creates tables, indexes,
    /// constraints and triggers, so requiring the schema to pre-exist would be the one
    /// gap in an otherwise declarative story — and CL.MSSQL has always created its own.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Sync_creates_a_missing_schema()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP SCHEMA IF EXISTS cl_alt CASCADE");

        var absent = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace WHERE nspname = 'cl_alt'");
        Assert.Equal(0, absent.Value);

        var sync = await lib.SyncTableAsync<ScopedRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        var created = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace WHERE nspname = 'cl_alt'");
        Assert.Equal(1, created.Value);

        var placed = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM pg_tables WHERE schemaname='cl_alt' AND tablename='scoped'");
        Assert.Equal(1, placed.Value);
    }

    /// <summary>Creating the schema must stay idempotent across repeated syncs.</summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Schema_creation_is_idempotent()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP SCHEMA IF EXISTS cl_alt CASCADE");
        Assert.True((await lib.SyncTableAsync<ScopedRow>(createBackup: false)).IsSuccess);

        await lib.ExecuteSqlAsync(
            "DELETE FROM public.__schema_state WHERE \"TableName\" = 'cl_alt.scoped'");
        var again = await lib.SyncTableAsync<ScopedRow>(createBackup: false);
        Assert.True(again.IsSuccess, again.Error?.ToString());
    }

    [FactRequiresEnv(Gate, Reason)]
    public async Task Full_lifecycle_works_inside_a_non_public_schema()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP SCHEMA IF EXISTS cl_alt CASCADE");
        await lib.ExecuteSqlAsync("CREATE SCHEMA cl_alt");

        var sync = await lib.SyncTableAsync<ScopedRow>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        // The table landed in cl_alt, not public.
        var placed = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM pg_tables WHERE schemaname = 'cl_alt' AND tablename = 'scoped'
            """);
        Assert.Equal(1, placed.Value);

        // Index and trigger are schema-qualified too.
        var idx = await lib.SqlScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE schemaname='cl_alt' AND tablename='scoped'");
        Assert.True(idx.Value >= 2, $"expected pk + tag index in cl_alt, saw {idx.Value}");

        var trig = await lib.SqlScalarAsync<long>("""
            SELECT count(*) FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'cl_alt' AND c.relname = 'scoped' AND NOT t.tgisinternal
            """);
        Assert.Equal(1, trig.Value);

        // CRUD, upsert and query all work against the scoped table.
        var repo = lib.GetRepository<ScopedRow>();
        var inserted = await repo.InsertAsync(new ScopedRow { Code = "s1", Tag = "t" });
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());

        var upserted = await repo.UpsertAsync(new ScopedRow { Code = "s1", Tag = "changed" });
        Assert.True(upserted.IsSuccess, upserted.Error?.ToString());

        var all = await repo.GetAllAsync();
        Assert.Single(all.Value!);
        Assert.Equal("changed", all.Value![0].Tag);

        var filtered = await lib.Query<ScopedRow>().Where(r => r.Tag == "changed").ToListAsync();
        Assert.True(filtered.IsSuccess, filtered.Error?.ToString());
        Assert.Single(filtered.Value!);
    }

    /// <summary>
    /// Two entities with the same table name in different schemas must stay distinct. If
    /// qualification were dropped anywhere, one would shadow the other.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Same_table_name_in_two_schemas_stays_separate()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP SCHEMA IF EXISTS cl_alt CASCADE");
        await lib.ExecuteSqlAsync("CREATE SCHEMA cl_alt");
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS public.scoped CASCADE");

        Assert.True((await lib.SyncTableAsync<ScopedRow>(createBackup: false)).IsSuccess);
        Assert.True((await lib.SyncTableAsync<ScopedRowPublic>(createBackup: false)).IsSuccess);

        await lib.GetRepository<ScopedRow>().InsertAsync(new ScopedRow { Code = "in-alt" });
        await lib.GetRepository<ScopedRowPublic>().InsertAsync(new ScopedRowPublic { Code = "in-public" });
        await lib.GetRepository<ScopedRowPublic>().InsertAsync(new ScopedRowPublic { Code = "in-public-2" });

        var alt = await lib.GetRepository<ScopedRow>().CountAsync();
        var pub = await lib.GetRepository<ScopedRowPublic>().CountAsync();
        Assert.Equal(1, alt.Value);
        Assert.Equal(2, pub.Value);

        // The schema-state sentinel must key on schema.table. Keyed on the bare name, these
        // two entities shared one row: whichever synced last owned the CRC, and a later
        // model change to the other was skipped by the fast path.
        var keys = await lib.SqlQueryAsync<StateKeyRow>("""
            SELECT "TableName" AS "TableName" FROM public.__schema_state
            WHERE "TableName" LIKE '%scoped%' ORDER BY 1
            """);
        Assert.True(keys.IsSuccess, keys.Error?.ToString());
        var names = keys.Value!.Select(k => k.TableName).ToArray();
        _out.WriteLine("sentinel keys: " + string.Join(", ", names));

        Assert.Equal(2, names.Length);
        Assert.Contains("cl_alt.scoped", names);
        Assert.Contains("public.scoped", names);
    }

    /// <summary>
    /// A foreign key that names a bare table must resolve inside the referencing table's own
    /// schema, not against search_path.
    /// </summary>
    [FactRequiresEnv(Gate, Reason)]
    public async Task Foreign_key_resolves_within_the_owning_schema()
    {
        var lib = Lib;
        await lib.ExecuteSqlAsync("DROP SCHEMA IF EXISTS cl_alt CASCADE");
        await lib.ExecuteSqlAsync("CREATE SCHEMA cl_alt");
        await lib.ExecuteSqlAsync("CREATE TABLE cl_alt.parent (id bigint PRIMARY KEY)");
        // A decoy of the same name in public: if qualification were wrong, the FK would
        // bind to this one.
        await lib.ExecuteSqlAsync("DROP TABLE IF EXISTS public.parent CASCADE");
        await lib.ExecuteSqlAsync("CREATE TABLE public.parent (id bigint PRIMARY KEY)");

        var sync = await lib.SyncTableAsync<ScopedChild>(createBackup: false);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());

        var target = await lib.SqlScalarAsync<string>("""
            SELECT n.nspname
            FROM pg_constraint con
            JOIN pg_class ref ON ref.oid = con.confrelid
            JOIN pg_namespace n ON n.oid = ref.relnamespace
            WHERE con.conrelid = 'cl_alt.child'::regclass AND con.contype = 'f'
            """);
        Assert.Equal("cl_alt", target.Value);
    }
}

[Table(Name = "child", Schema = "cl_alt")]
public sealed class ScopedChild
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
    [Column(Name = "parent_id")]
    [ForeignKey("parent", "id")]
    public long ParentId { get; set; }
}

public sealed class StateKeyRow
{
    [Column(Name = "TableName")] public string TableName { get; set; } = "";
}
