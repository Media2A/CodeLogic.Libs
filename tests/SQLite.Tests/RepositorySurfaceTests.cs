using CL.SQLite;
using CL.SQLite.Models;
using CL.SQLite.Services;
using Xunit;

namespace SQLite.Tests;

// ── Entities ──────────────────────────────────────────────────────────────────

[SQLiteTable("repo_item")]
public sealed class RepoItem
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "name", DataType = SQLiteDataType.TEXT, IsNotNull = true)]
    public string Name { get; set; } = "";

    [SQLiteColumn(ColumnName = "qty", DataType = SQLiteDataType.INTEGER)]
    public int Qty { get; set; }

    [SQLiteColumn(ColumnName = "note", DataType = SQLiteDataType.TEXT)]
    public string? Note { get; set; }
}

/// <summary>Table whose primary key is a caller-supplied string rather than a rowid alias.</summary>
[SQLiteTable("repo_keyed")]
public sealed class RepoKeyed
{
    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "code", DataType = SQLiteDataType.TEXT)]
    public string Code { get; set; } = "";

    [SQLiteColumn(ColumnName = "label", DataType = SQLiteDataType.TEXT)]
    public string Label { get; set; } = "";
}

/// <summary>Two-column primary key — see the composite-key tests for what the DDL generator does with it.</summary>
[SQLiteTable("repo_composite")]
public sealed class RepoComposite
{
    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "k1", DataType = SQLiteDataType.TEXT)]
    public string K1 { get; set; } = "";

    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "k2", DataType = SQLiteDataType.INTEGER)]
    public int K2 { get; set; }

    [SQLiteColumn(ColumnName = "payload", DataType = SQLiteDataType.TEXT)]
    public string Payload { get; set; } = "";
}

/// <summary>
/// Composite-key entity used only to probe the CREATE TABLE generator. It has its own table
/// name so that no other test can create the table first and hide the DDL failure.
/// </summary>
[SQLiteTable("repo_composite_ddl_probe")]
public sealed class CompositeDdlProbe
{
    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "k1", DataType = SQLiteDataType.TEXT)]
    public string K1 { get; set; } = "";

    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "k2", DataType = SQLiteDataType.INTEGER)]
    public int K2 { get; set; }
}

/// <summary>
/// Entity whose DDL cannot be executed: the <c>DefaultValue</c> is emitted into the CREATE
/// TABLE verbatim and is not valid SQL. Used by the tests that need a per-type sync failure.
/// </summary>
[SQLiteTable("repo_bad_ddl_probe")]
public sealed class BadDdlProbe
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "v", DataType = SQLiteDataType.INTEGER, DefaultValue = "(((")]
    public int V { get; set; }
}

/// <summary>Entity with no primary key at all.</summary>
[SQLiteTable("repo_keyless")]
public sealed class RepoKeyless
{
    [SQLiteColumn(ColumnName = "v", DataType = SQLiteDataType.INTEGER)]
    public int V { get; set; }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

[Collection("codelogic")]
public sealed class RepositorySurfaceTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;

    public RepositorySurfaceTests(SQLiteRuntimeFixture fx) => _fx = fx;

    private static string Tag() => "t" + Guid.NewGuid().ToString("N")[..10];

    private async Task<Repository<RepoItem>> ItemsAsync()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoItem>()).IsSuccess);
        return Lib.GetRepository<RepoItem>();
    }

    // ── FindAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAsync_returns_only_the_matching_rows()
    {
        var repo = await ItemsAsync();
        var tag = Tag();
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 1 });
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 2 });
        await repo.InsertAsync(new RepoItem { Name = tag + "x", Qty = 3 });

        var found = await repo.FindAsync(i => i.Name == tag);
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.Equal(2, found.Value!.Count);
        Assert.All(found.Value!, i => Assert.Equal(tag, i.Name));
    }

    [Fact]
    public async Task FindAsync_supports_compound_predicates()
    {
        var repo = await ItemsAsync();
        var tag = Tag();
        for (var q = 1; q <= 5; q++)
            await repo.InsertAsync(new RepoItem { Name = tag, Qty = q });

        var found = await repo.FindAsync(i => i.Name == tag && i.Qty >= 2 && i.Qty <= 4);
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.Equal([2, 3, 4], found.Value!.Select(i => i.Qty).Order());
    }

    [Fact]
    public async Task FindAsync_returns_an_empty_list_rather_than_a_failure_when_nothing_matches()
    {
        var repo = await ItemsAsync();

        var found = await repo.FindAsync(i => i.Name == Tag());
        Assert.True(found.IsSuccess, found.Error?.Message);
        Assert.Empty(found.Value!);
    }

    [Fact]
    public async Task FindAsync_translates_an_IS_NULL_comparison()
    {
        var repo = await ItemsAsync();
        var tag = Tag();
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 1, Note = null });
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 2, Note = "set" });

        var nulls = await repo.FindAsync(i => i.Name == tag && i.Note == null);
        Assert.True(nulls.IsSuccess, nulls.Error?.Message);
        Assert.Equal(1, Assert.Single(nulls.Value!).Qty);

        var notNulls = await repo.FindAsync(i => i.Name == tag && i.Note != null);
        Assert.True(notNulls.IsSuccess, notNulls.Error?.Message);
        Assert.Equal(2, Assert.Single(notNulls.Value!).Qty);
    }

    [Fact]
    public async Task FindAsync_reports_a_failure_for_an_untranslatable_predicate()
    {
        var repo = await ItemsAsync();

        var found = await repo.FindAsync(i => i.Name.ToUpperInvariant() == "X");
        Assert.True(found.IsFailure);
        Assert.NotNull(found.Error);
    }

    // ── GetByKeysAsync / DeleteByKeysAsync on a single-column key ─────────────

    [Fact]
    public async Task GetByKeysAsync_finds_a_row_by_its_single_string_key()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoKeyed>()).IsSuccess);
        var repo = Lib.GetRepository<RepoKeyed>();
        var code = Tag();
        Assert.True((await repo.InsertAsync(new RepoKeyed { Code = code, Label = "hello" })).IsSuccess);

        var got = await repo.GetByKeysAsync(default, code);
        Assert.True(got.IsSuccess, got.Error?.Message);
        Assert.Equal("hello", got.Value!.Label);
    }

    [Fact]
    public async Task GetByKeysAsync_returns_a_null_value_and_not_a_failure_when_the_key_is_absent()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoKeyed>()).IsSuccess);
        var repo = Lib.GetRepository<RepoKeyed>();

        var got = await repo.GetByKeysAsync(default, Tag());
        Assert.True(got.IsSuccess, got.Error?.Message);
        Assert.Null(got.Value);
    }

    [Fact]
    public async Task GetByKeysAsync_fails_validation_when_the_key_count_is_wrong()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoKeyed>()).IsSuccess);
        var repo = Lib.GetRepository<RepoKeyed>();

        var tooMany = await repo.GetByKeysAsync(default, "a", "b");
        Assert.True(tooMany.IsFailure);
        Assert.Contains("Expected 1 key", tooMany.Error!.Message ?? tooMany.Error.ToString());

        var tooFew = await repo.GetByKeysAsync(default);
        Assert.True(tooFew.IsFailure);
    }

    [Fact]
    public async Task DeleteByKeysAsync_removes_the_row_and_is_idempotent()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoKeyed>()).IsSuccess);
        var repo = Lib.GetRepository<RepoKeyed>();
        var code = Tag();
        await repo.InsertAsync(new RepoKeyed { Code = code, Label = "bye" });

        Assert.True((await repo.DeleteByKeysAsync(default, code)).IsSuccess);
        Assert.Null((await repo.GetByKeysAsync(default, code)).Value);

        // Deleting a row that is already gone is a no-op success, not a failure.
        Assert.True((await repo.DeleteByKeysAsync(default, code)).IsSuccess);
    }

    [Fact]
    public async Task DeleteByKeysAsync_fails_validation_when_the_key_count_is_wrong()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoKeyed>()).IsSuccess);
        var repo = Lib.GetRepository<RepoKeyed>();

        Assert.True((await repo.DeleteByKeysAsync(default, "a", "b")).IsFailure);
    }

    // ── Composite keys ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_composite_key_entity_can_be_synced()
    {
        var sync = await Lib.TableSync.SyncTableAsync<CompositeDdlProbe>();
        Assert.True(sync.IsSuccess, sync.Error?.Message);
        Assert.True(sync.Value!.Success, sync.Value.Message);
    }

    /// <summary>
    /// The generated composite-key DDL is a single table-level constraint, and both key columns
    /// are NOT NULL — the same shape the hand-written table below declares.
    /// </summary>
    [Fact]
    public async Task A_composite_key_is_generated_as_one_table_level_constraint()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<CompositeDdlProbe>()).IsSuccess);

        var ddl = await Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT sql FROM sqlite_master WHERE type='table' AND name='repo_composite_ddl_probe';";
            return Convert.ToString(await cmd.ExecuteScalarAsync())!;
        });

        Assert.Contains("PRIMARY KEY (\"k1\", \"k2\")", ddl);
        Assert.DoesNotContain("\"k1\" TEXT PRIMARY KEY", ddl);

        var keys = await Lib.ConnectionManager.ExecuteAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('repo_composite_ddl_probe') WHERE pk > 0;";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        });
        Assert.Equal(2L, keys);
    }

    /// <summary>
    /// Exercises GetByKeysAsync / DeleteByKeysAsync against a genuine two-column key, on a table
    /// created by the schema generator itself.
    /// </summary>
    [Fact]
    public async Task GetByKeysAsync_and_DeleteByKeysAsync_work_against_a_composite_table()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoComposite>()).IsSuccess);

        var repo = Lib.GetRepository<RepoComposite>();
        var k1 = Tag();
        Assert.True((await repo.InsertAsync(new RepoComposite { K1 = k1, K2 = 1, Payload = "one" })).IsSuccess);
        Assert.True((await repo.InsertAsync(new RepoComposite { K1 = k1, K2 = 2, Payload = "two" })).IsSuccess);

        var got = await repo.GetByKeysAsync(default, k1, 2);
        Assert.True(got.IsSuccess, got.Error?.Message);
        Assert.Equal("two", got.Value!.Payload);

        Assert.True((await repo.DeleteByKeysAsync(default, k1, 1)).IsSuccess);
        Assert.Null((await repo.GetByKeysAsync(default, k1, 1)).Value);
        Assert.NotNull((await repo.GetByKeysAsync(default, k1, 2)).Value);
    }

    // ── Entities with no primary key ──────────────────────────────────────────

    [Fact]
    public async Task Update_and_Delete_fail_with_a_clear_error_for_an_entity_with_no_primary_key()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<RepoKeyless>()).IsSuccess);
        var repo = Lib.GetRepository<RepoKeyless>();
        Assert.True((await repo.InsertAsync(new RepoKeyless { V = 1 })).IsSuccess);

        var update = await repo.UpdateAsync(new RepoKeyless { V = 2 });
        Assert.True(update.IsFailure);
        Assert.Contains("no primary key", update.Error!.ToString(), StringComparison.OrdinalIgnoreCase);

        var delete = await repo.DeleteAsync(1);
        Assert.True(delete.IsFailure);
        Assert.Contains("no primary key", delete.Error!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── GetPagedAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagedAsync_walks_a_dedicated_table_page_by_page()
    {
        // GetPagedAsync counts and reads the whole table with no filter, so it needs a table
        // nothing else writes to. This one is created and dropped by the test itself.
        var table = "repo_paged";
        var repo = Lib.GetRepository<RepoItem>();
        await repo.RawExecuteAsync($"DROP TABLE IF EXISTS \"{table}\";");
        await repo.RawExecuteAsync($"CREATE TABLE \"{table}\" (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, qty INTEGER, note TEXT);");
        for (var i = 0; i < 7; i++)
            await repo.RawExecuteAsync($"INSERT INTO \"{table}\" (name, qty) VALUES ('p', {i});");

        var paged = Lib.GetRepository<PagedRow>();
        var page1 = await paged.GetPagedAsync(1, 3, orderBy: "qty");
        Assert.True(page1.IsSuccess, page1.Error?.Message);
        Assert.Equal(7, page1.Value!.TotalItems);
        Assert.Equal(3, page1.Value.TotalPages);
        Assert.Equal(1, page1.Value.PageNumber);
        Assert.Equal(3, page1.Value.PageSize);
        Assert.Equal([0, 1, 2], page1.Value.Items.Select(r => r.Qty));

        var page3 = await paged.GetPagedAsync(3, 3, orderBy: "qty");
        Assert.True(page3.IsSuccess, page3.Error?.Message);
        Assert.Equal([6], page3.Value!.Items.Select(r => r.Qty));

        var descending = await paged.GetPagedAsync(1, 2, orderBy: "qty", desc: true);
        Assert.True(descending.IsSuccess, descending.Error?.Message);
        Assert.Equal([6, 5], descending.Value!.Items.Select(r => r.Qty));

        var beyondTheEnd = await paged.GetPagedAsync(99, 3, orderBy: "qty");
        Assert.True(beyondTheEnd.IsSuccess, beyondTheEnd.Error?.Message);
        Assert.Empty(beyondTheEnd.Value!.Items);
        Assert.Equal(7, beyondTheEnd.Value.TotalItems);

        await repo.RawExecuteAsync($"DROP TABLE IF EXISTS \"{table}\";");
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(-1, -1)]
    public async Task GetPagedAsync_rejects_a_non_positive_page_or_page_size(int page, int pageSize)
    {
        var repo = await ItemsAsync();

        var result = await repo.GetPagedAsync(page, pageSize);
        Assert.True(result.IsFailure);
        Assert.Contains(">= 1", result.Error!.ToString());
    }

    [Fact]
    public async Task GetPagedAsync_with_no_orderBy_still_returns_a_page()
    {
        var repo = await ItemsAsync();
        await repo.InsertAsync(new RepoItem { Name = Tag(), Qty = 1 });

        var result = await repo.GetPagedAsync(1, 1);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(result.Value!.Items);
    }

    [Fact]
    public async Task PagedResult_TotalPages_rounds_up()
    {
        var pr = new PagedResult<RepoItem> { Items = [], PageNumber = 1, PageSize = 3, TotalItems = 7 };
        Assert.Equal(3, pr.TotalPages);

        var empty = new PagedResult<RepoItem> { Items = [], PageNumber = 1, PageSize = 0, TotalItems = 7 };
        Assert.Equal(0, empty.TotalPages);
    }

    // ── RawQueryAsync / RawExecuteAsync ───────────────────────────────────────

    [Fact]
    public async Task RawQueryAsync_maps_rows_onto_the_entity()
    {
        var repo = await ItemsAsync();
        var tag = Tag();
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 11 });

        var rows = await repo.RawQueryAsync(
            "SELECT * FROM \"repo_item\" WHERE \"name\" = @n;",
            new Dictionary<string, object?> { ["@n"] = tag });

        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal(11, Assert.Single(rows.Value!).Qty);
    }

    [Fact]
    public async Task RawQueryAsync_works_without_parameters_and_over_a_projection()
    {
        var repo = await ItemsAsync();
        var tag = Tag();
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 5 });

        var rows = await repo.RawQueryAsync($"SELECT \"name\", \"qty\" FROM \"repo_item\" WHERE \"name\" = '{tag}';");
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        var row = Assert.Single(rows.Value!);
        Assert.Equal(tag, row.Name);
        Assert.Equal(5, row.Qty);
        Assert.Equal(0, row.Id); // not selected, so left at its default
    }

    [Fact]
    public async Task RawQueryAsync_returns_an_empty_list_for_a_query_that_matches_nothing()
    {
        var repo = await ItemsAsync();

        var rows = await repo.RawQueryAsync("SELECT * FROM \"repo_item\" WHERE 1 = 0;");
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Empty(rows.Value!);
    }

    [Fact]
    public async Task RawQueryAsync_reports_a_failure_for_invalid_SQL()
    {
        var repo = await ItemsAsync();

        var rows = await repo.RawQueryAsync("SELECT FROM WHERE;");
        Assert.True(rows.IsFailure);
        Assert.NotNull(rows.Error);
    }

    [Fact]
    public async Task RawExecuteAsync_returns_the_number_of_rows_it_changed()
    {
        var repo = await ItemsAsync();
        var tag = Tag();
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 1 });
        await repo.InsertAsync(new RepoItem { Name = tag, Qty = 2 });

        var updated = await repo.RawExecuteAsync(
            "UPDATE \"repo_item\" SET \"qty\" = \"qty\" + 100 WHERE \"name\" = @n;",
            new Dictionary<string, object?> { ["@n"] = tag });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(2, updated.Value);

        var deleted = await repo.RawExecuteAsync(
            "DELETE FROM \"repo_item\" WHERE \"name\" = @n;",
            new Dictionary<string, object?> { ["@n"] = tag });
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal(2, deleted.Value);
    }

    [Fact]
    public async Task RawExecuteAsync_reports_zero_when_nothing_matched_and_fails_on_bad_SQL()
    {
        var repo = await ItemsAsync();

        var none = await repo.RawExecuteAsync("DELETE FROM \"repo_item\" WHERE \"name\" = @n;",
            new Dictionary<string, object?> { ["@n"] = Tag() });
        Assert.True(none.IsSuccess, none.Error?.Message);
        Assert.Equal(0, none.Value);

        Assert.True((await repo.RawExecuteAsync("NOT SQL AT ALL")).IsFailure);
    }

    [Fact]
    public async Task RawExecuteAsync_can_run_DDL()
    {
        var repo = await ItemsAsync();

        Assert.True((await repo.RawExecuteAsync("CREATE TABLE IF NOT EXISTS raw_ddl (x INTEGER);")).IsSuccess);
        Assert.True((await repo.RawExecuteAsync("DROP TABLE raw_ddl;")).IsSuccess);
    }

    // ── UpdateAsync / DeleteAsync / CountAsync / GetAllAsync edges ────────────

    [Fact]
    public async Task UpdateAsync_writes_every_non_key_column_back()
    {
        var repo = await ItemsAsync();
        var id = (await repo.InsertAsync(new RepoItem { Name = Tag(), Qty = 1, Note = "before" })).Value;

        var updated = new RepoItem { Id = id, Name = "renamed", Qty = 42, Note = null };
        Assert.True((await repo.UpdateAsync(updated)).IsSuccess);

        var fetched = (await repo.GetByIdAsync(id)).Value!;
        Assert.Equal("renamed", fetched.Name);
        Assert.Equal(42, fetched.Qty);
        Assert.Null(fetched.Note);
    }

    [Fact]
    public async Task UpdateAsync_against_a_missing_id_succeeds_without_changing_anything()
    {
        var repo = await ItemsAsync();

        var result = await repo.UpdateAsync(new RepoItem { Id = 999_999_999, Name = "ghost" });
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null((await repo.GetByIdAsync(999_999_999L)).Value);
    }

    [Fact]
    public async Task DeleteAsync_removes_a_row_by_its_primary_key()
    {
        var repo = await ItemsAsync();
        var id = (await repo.InsertAsync(new RepoItem { Name = Tag(), Qty = 1 })).Value;

        Assert.True((await repo.DeleteAsync(id)).IsSuccess);
        Assert.Null((await repo.GetByIdAsync(id)).Value);
    }

    [Fact]
    public async Task GetByIdAsync_returns_a_null_value_for_an_absent_id()
    {
        var repo = await ItemsAsync();

        var result = await repo.GetByIdAsync(-12345L);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task InsertAsync_assigns_the_new_rowid_back_onto_the_entity()
    {
        var repo = await ItemsAsync();
        var entity = new RepoItem { Name = Tag(), Qty = 1 };
        Assert.Equal(0, entity.Id);

        var result = await repo.InsertAsync(entity);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(result.Value, entity.Id);
        Assert.True(entity.Id > 0);
    }

    [Fact]
    public async Task InsertAsync_reports_a_failure_when_a_NOT_NULL_constraint_is_violated()
    {
        var repo = await ItemsAsync();

        var result = await repo.InsertAsync(new RepoItem { Name = null!, Qty = 1 });
        Assert.True(result.IsFailure);
        Assert.Contains("NOT NULL", result.Error!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CountAsync_counts_the_whole_table_and_GetAllAsync_honours_its_limit()
    {
        var repo = await ItemsAsync();
        var before = (await repo.CountAsync()).Value;
        for (var i = 0; i < 3; i++)
            await repo.InsertAsync(new RepoItem { Name = Tag(), Qty = i });

        var after = await repo.CountAsync();
        Assert.True(after.IsSuccess, after.Error?.Message);
        Assert.Equal(before + 3, after.Value);

        var limited = await repo.GetAllAsync(limit: 2);
        Assert.True(limited.IsSuccess, limited.Error?.Message);
        Assert.Equal(2, limited.Value!.Count);

        var none = await repo.GetAllAsync(limit: 0);
        Assert.True(none.IsSuccess, none.Error?.Message);
        Assert.Empty(none.Value!);
    }

    [Fact]
    public async Task A_repository_for_an_unknown_connection_id_throws_when_it_is_used()
    {
        // The connection id is not validated at construction — only when a query runs.
        var repo = Lib.GetRepository<RepoItem>("NoSuchDatabase");

        var result = await repo.CountAsync();
        Assert.True(result.IsFailure);
        Assert.Contains("NoSuchDatabase", result.Error!.ToString());
    }
}

/// <summary>Maps the <c>repo_paged</c> scratch table used by the paging test.</summary>
[SQLiteTable("repo_paged")]
public sealed class PagedRow
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "name", DataType = SQLiteDataType.TEXT)]
    public string Name { get; set; } = "";

    [SQLiteColumn(ColumnName = "qty", DataType = SQLiteDataType.INTEGER)]
    public int Qty { get; set; }
}
