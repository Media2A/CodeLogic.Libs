using CL.SQLite;
using CL.SQLite.Models;
using CL.SQLite.Services;
using Xunit;

namespace SQLite.Tests;

[SQLiteTable("qb_row")]
public sealed class QbRow
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "tag", DataType = SQLiteDataType.TEXT, IsNotNull = true)]
    public string Tag { get; set; } = "";

    [SQLiteColumn(ColumnName = "n", DataType = SQLiteDataType.INTEGER)]
    public int N { get; set; }

    [SQLiteColumn(ColumnName = "score", DataType = SQLiteDataType.REAL)]
    public double Score { get; set; }

    [SQLiteColumn(ColumnName = "flag", DataType = SQLiteDataType.BOOLEAN)]
    public bool Flag { get; set; }

    [SQLiteColumn(ColumnName = "note", DataType = SQLiteDataType.TEXT)]
    public string? Note { get; set; }
}

/// <summary>
/// Column names that are SQL reserved words. Every name here is still a legal parameter
/// token, so the whole CRUD path works — only identifier quoting is under test.
/// </summary>
[SQLiteTable("qb_awkward")]
public sealed class QbAwkward
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "Order", DataType = SQLiteDataType.INTEGER)]
    public int Order { get; set; }

    [SQLiteColumn(ColumnName = "group", DataType = SQLiteDataType.TEXT)]
    public string Group { get; set; } = "";

    [SQLiteColumn(ColumnName = "index", DataType = SQLiteDataType.TEXT)]
    public string Index { get; set; } = "";

    /// <summary>Property name deliberately unlike its column name, for the projection test.</summary>
    [SQLiteColumn(ColumnName = "kind_col", DataType = SQLiteDataType.TEXT)]
    public string Kind { get; set; } = "";
}

/// <summary>
/// A column name containing a space. Legal in SQLite when quoted, but it is not a legal
/// parameter token, which is what the insert and update paths derive their parameter names
/// from. Used only by the tests that pin that defect.
/// </summary>
[SQLiteTable("qb_spaced")]
public sealed class QbSpaced
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "tag", DataType = SQLiteDataType.TEXT)]
    public string Tag { get; set; } = "";

    [SQLiteColumn(ColumnName = "select from", DataType = SQLiteDataType.TEXT)]
    public string SelectFrom { get; set; } = "";
}

[Collection("codelogic")]
public sealed class QueryBuilderSurfaceTests
{
    private readonly SQLiteRuntimeFixture _fx;
    private SQLiteLibrary Lib => _fx.Library;

    public QueryBuilderSurfaceTests(SQLiteRuntimeFixture fx) => _fx = fx;

    private QueryBuilder<QbRow> Q() => Lib.GetQueryBuilder<QbRow>();

    private async Task<string> SeedAsync(int count = 5)
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbRow>()).IsSuccess);
        var repo = Lib.GetRepository<QbRow>();
        var tag = "q" + Guid.NewGuid().ToString("N")[..10];
        for (var i = 0; i < count; i++)
            Assert.True((await repo.InsertAsync(new QbRow
            {
                Tag = tag,
                N = i,
                Score = i * 1.5,
                Flag = i % 2 == 0,
                Note = i == 0 ? null : $"note{i}"
            })).IsSuccess);
        return tag;
    }

    // ── FirstOrDefaultAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task FirstOrDefaultAsync_returns_the_first_row_in_the_requested_order()
    {
        var tag = await SeedAsync();

        var lowest = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).FirstOrDefaultAsync();
        Assert.True(lowest.IsSuccess, lowest.Error?.Message);
        Assert.Equal(0, lowest.Value!.N);

        var highest = await Q().Where(r => r.Tag == tag).OrderByDescending(r => r.N).FirstOrDefaultAsync();
        Assert.True(highest.IsSuccess, highest.Error?.Message);
        Assert.Equal(4, highest.Value!.N);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_returns_a_null_value_and_not_a_failure_for_an_empty_result()
    {
        await SeedAsync(0);

        var result = await Q().Where(r => r.Tag == "no-such-tag").FirstOrDefaultAsync();
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_respects_an_Offset_already_on_the_builder()
    {
        var tag = await SeedAsync();

        var second = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Offset(1).FirstOrDefaultAsync();
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(1, second.Value!.N);
    }

    // ── Select ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Select_of_a_single_column_populates_only_that_property()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Select(r => r.N).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([0, 1, 2, 3, 4], rows.Value!.Select(r => r.N));
        Assert.All(rows.Value!, r =>
        {
            Assert.Equal(0, r.Id);      // "id" was not selected
            Assert.Equal("", r.Tag);    // neither was "tag"
        });
    }

    [Fact]
    public async Task Select_of_an_anonymous_projection_populates_each_named_column()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N)
            .Select(r => new { r.N, r.Score })
            .ToListAsync();

        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal(5, rows.Value!.Count);
        Assert.Equal(3.0, rows.Value[2].Score);
        Assert.Equal(2, rows.Value[2].N);
        Assert.All(rows.Value!, r => Assert.Equal("", r.Tag));
    }

    /// <summary>
    /// A single-member <c>Select</c> resolves the column through <c>[SQLiteColumn]</c> and so
    /// honours a renamed column correctly.
    /// </summary>
    [Fact]
    public async Task A_single_member_Select_honours_a_renamed_column()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);

        var rows = await Lib.GetQueryBuilder<QbAwkward>().Select(r => r.Kind).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
    }

    /// <summary>
    /// An anonymous-type projection resolves each member back to the source entity's mapped
    /// column, so a renamed column — here <c>Kind</c> mapped to <c>kind_col</c> — is emitted
    /// under its column name and not the C# property name.
    /// </summary>
    [Fact]
    public async Task An_anonymous_projection_honours_a_renamed_column()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);

        var rows = await Lib.GetQueryBuilder<QbAwkward>().Select(r => new { r.Kind }).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
    }

    [Fact]
    public async Task An_anonymous_projection_populates_the_renamed_property_it_names()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);
        var repo = Lib.GetRepository<QbAwkward>();
        var marker = "m" + Guid.NewGuid().ToString("N")[..8];
        await repo.InsertAsync(new QbAwkward { Group = marker, Kind = "kk" });

        var rows = await Lib.GetQueryBuilder<QbAwkward>()
            .Where(r => r.Group == marker)
            .Select(r => new { r.Kind })
            .ToListAsync();

        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal("kk", Assert.Single(rows.Value!).Kind);
    }

    /// <summary>
    /// <c>Select</c> quotes every projected column, like WHERE and ORDER BY, so a reserved word
    /// or an awkward identifier is legal in the SELECT list too.
    /// </summary>
    [Fact]
    public async Task Select_of_a_reserved_word_column_executes()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);

        var rows = await Lib.GetQueryBuilder<QbAwkward>().Select(r => r.Order).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
    }

    [Fact]
    public async Task Select_of_a_column_name_containing_a_space_executes()
    {
        var marker = await SeedSpacedAsync();

        var rows = await Lib.GetQueryBuilder<QbSpaced>()
            .Where(r => r.Tag == marker)
            .Select(r => new { r.SelectFrom })
            .ToListAsync();

        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal("seed", Assert.Single(rows.Value!).SelectFrom);
    }

    // ── Limit / Offset / Take / Skip ──────────────────────────────────────────

    [Fact]
    public async Task Limit_caps_the_row_count()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Limit(2).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([0, 1], rows.Value!.Select(r => r.N));
    }

    [Fact]
    public async Task Take_is_an_alias_for_Limit()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Take(3).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([0, 1, 2], rows.Value!.Select(r => r.N));
    }

    [Fact]
    public async Task Limit_and_Offset_together_form_a_window()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Limit(2).Offset(2).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([2, 3], rows.Value!.Select(r => r.N));
    }

    [Fact]
    public async Task Skip_is_an_alias_for_Offset()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Take(2).Skip(3).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([3, 4], rows.Value!.Select(r => r.N));
    }

    [Fact]
    public async Task The_last_Limit_and_Offset_call_wins()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N)
            .Limit(4).Take(1).Offset(9).Skip(1)
            .ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([1], rows.Value!.Select(r => r.N));
    }

    [Fact]
    public async Task An_Offset_past_the_end_yields_an_empty_list()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Limit(10).Offset(100).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Empty(rows.Value!);
    }

    /// <summary>
    /// SQLite's grammar is <c>LIMIT expr [OFFSET expr]</c> — OFFSET is only legal as a suffix of
    /// LIMIT — so an offset with no limit is emitted as <c>LIMIT -1 OFFSET n</c>, SQLite's
    /// "no upper bound" spelling.
    /// </summary>
    [Fact]
    public async Task Offset_without_Limit_skips_rows()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Offset(2).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal([2, 3, 4], rows.Value!.Select(r => r.N));
    }

    [Fact]
    public async Task A_query_with_neither_Limit_nor_Offset_emits_no_LIMIT_at_all()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal([0, 1, 2, 3, 4], rows.Value!.Select(r => r.N));
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_removes_the_rows_matched_by_the_WHERE_clause()
    {
        var tag = await SeedAsync();

        var deleted = await Q().Where(r => r.Tag == tag && r.N < 2).DeleteAsync();
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal(2, deleted.Value);

        var left = await Q().Where(r => r.Tag == tag).CountAsync();
        Assert.Equal(3, left.Value);
    }

    [Fact]
    public async Task DeleteAsync_reports_zero_when_nothing_matched()
    {
        await SeedAsync(0);

        var deleted = await Q().Where(r => r.Tag == "absent-tag").DeleteAsync();
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.Equal(0, deleted.Value);
    }

    /// <summary>
    /// A builder with no <c>Where</c> deletes the whole table. Pinned deliberately: the API has
    /// no guard against it, so the behaviour should be a conscious one.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_with_no_WHERE_clause_empties_the_table()
    {
        var repo = Lib.GetRepository<QbDeleteAll>();
        Assert.True((await Lib.TableSync.SyncTableAsync<QbDeleteAll>()).IsSuccess);
        await repo.InsertAsync(new QbDeleteAll { V = 1 });
        await repo.InsertAsync(new QbDeleteAll { V = 2 });

        var deleted = await Lib.GetQueryBuilder<QbDeleteAll>().DeleteAsync();
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.True(deleted.Value >= 2);
        Assert.Equal(0, (await repo.CountAsync()).Value);
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_sets_the_named_columns_on_the_matched_rows()
    {
        var tag = await SeedAsync();

        var updated = await Q().Where(r => r.Tag == tag && r.N >= 3)
            .UpdateAsync(new Dictionary<string, object?> { ["note"] = "bulk", ["score"] = 99.5 });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(2, updated.Value);

        var touched = await Q().Where(r => r.Tag == tag && r.Note == "bulk").ToListAsync();
        Assert.Equal(2, touched.Value!.Count);
        Assert.All(touched.Value!, r => Assert.Equal(99.5, r.Score));
    }

    [Fact]
    public async Task UpdateAsync_can_write_a_NULL()
    {
        var tag = await SeedAsync();

        var updated = await Q().Where(r => r.Tag == tag && r.N == 1)
            .UpdateAsync(new Dictionary<string, object?> { ["note"] = null });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(1, updated.Value);

        var row = await Q().Where(r => r.Tag == tag && r.N == 1).FirstOrDefaultAsync();
        Assert.Null(row.Value!.Note);
    }

    [Fact]
    public async Task UpdateAsync_reports_zero_when_nothing_matched()
    {
        await SeedAsync(0);

        var updated = await Q().Where(r => r.Tag == "absent-tag")
            .UpdateAsync(new Dictionary<string, object?> { ["note"] = "x" });
        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal(0, updated.Value);
    }

    [Fact]
    public async Task UpdateAsync_quotes_a_reserved_word_column_in_its_SET_list()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);
        var repo = Lib.GetRepository<QbAwkward>();
        var marker = "m" + Guid.NewGuid().ToString("N")[..8];
        await repo.InsertAsync(new QbAwkward { Order = 1, Group = marker });

        var updated = await Lib.GetQueryBuilder<QbAwkward>()
            .Where(r => r.Group == marker)
            .UpdateAsync(new Dictionary<string, object?> { ["Order"] = 42 });

        Assert.True(updated.IsSuccess, updated.Error?.ToString());
        Assert.Equal(1, updated.Value);

        var row = await Lib.GetQueryBuilder<QbAwkward>().Where(r => r.Group == marker).FirstOrDefaultAsync();
        Assert.Equal(42, row.Value!.Order);
    }

    /// <summary>
    /// <c>UpdateAsync</c> names its parameters positionally (<c>@p0</c>, <c>@p1</c>, …), so a
    /// column name that is not a legal parameter token — one containing a space — still works.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_handles_a_column_name_containing_a_space()
    {
        var marker = await SeedSpacedAsync();

        var updated = await Lib.GetQueryBuilder<QbSpaced>()
            .Where(r => r.Tag == marker)
            .UpdateAsync(new Dictionary<string, object?> { ["select from"] = "value" });

        Assert.True(updated.IsSuccess, updated.Error?.ToString());
        Assert.Equal(1, updated.Value);
    }

    [Fact]
    public async Task UpdateAsync_writes_the_value_it_was_given_to_a_spaced_column()
    {
        var marker = await SeedSpacedAsync();

        Assert.True((await Lib.GetQueryBuilder<QbSpaced>()
            .Where(r => r.Tag == marker)
            .UpdateAsync(new Dictionary<string, object?> { ["select from"] = "value" })).IsSuccess);

        var row = await Lib.GetQueryBuilder<QbSpaced>().Where(r => r.Tag == marker).FirstOrDefaultAsync();
        Assert.True(row.IsSuccess, row.Error?.ToString());
        Assert.Equal("value", row.Value!.SelectFrom);
    }

    /// <summary>
    /// <c>Repository.InsertAsync</c> — and <c>UpsertAsync</c> and <c>UpdateAsync</c> with it —
    /// names its parameters positionally, so a column name that is not a legal parameter token
    /// no longer breaks the write path.
    /// </summary>
    [Fact]
    public async Task InsertAsync_handles_an_entity_with_a_spaced_column_name()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbSpaced>()).IsSuccess);

        var result = await Lib.GetRepository<QbSpaced>()
            .InsertAsync(new QbSpaced { Tag = "t", SelectFrom = "v" });
        Assert.True(result.IsSuccess, result.Error?.ToString());
    }

    [Fact]
    public async Task Upsert_and_repository_Update_also_handle_a_spaced_column_name()
    {
        var marker = await SeedSpacedAsync();
        var repo = Lib.GetRepository<QbSpaced>();

        var row = (await Lib.GetQueryBuilder<QbSpaced>().Where(r => r.Tag == marker).FirstOrDefaultAsync()).Value!;
        row.SelectFrom = "updated";
        Assert.True((await repo.UpdateAsync(row)).IsSuccess);
        Assert.Equal("updated", (await repo.GetByIdAsync(row.Id)).Value!.SelectFrom);

        row.SelectFrom = "upserted";
        Assert.True((await repo.UpsertAsync(row)).IsSuccess);
        Assert.Equal("upserted", (await repo.GetByIdAsync(row.Id)).Value!.SelectFrom);
    }

    /// <summary>Seeds one row into <c>qb_spaced</c> through the repository.</summary>
    private async Task<string> SeedSpacedAsync()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbSpaced>()).IsSuccess);
        var marker = "m" + Guid.NewGuid().ToString("N")[..8];
        var inserted = await Lib.GetRepository<QbSpaced>()
            .InsertAsync(new QbSpaced { Tag = marker, SelectFrom = "seed" });
        Assert.True(inserted.IsSuccess, inserted.Error?.ToString());
        return marker;
    }

    // ── Aggregates and grouping ───────────────────────────────────────────────

    [Fact]
    public async Task Sum_Min_and_Max_agree_with_the_seeded_data()
    {
        var tag = await SeedAsync();

        Assert.Equal(10, (await Q().Where(r => r.Tag == tag).SumAsync(r => r.N)).Value);
        Assert.Equal(0, (await Q().Where(r => r.Tag == tag).MinAsync(r => r.N)).Value);
        Assert.Equal(4, (await Q().Where(r => r.Tag == tag).MaxAsync(r => r.N)).Value);
        Assert.Equal(6.0, (await Q().Where(r => r.Tag == tag).MaxAsync(r => r.Score)).Value);
    }

    [Fact]
    public async Task An_aggregate_over_an_empty_set_yields_the_default_rather_than_a_failure()
    {
        await SeedAsync(0);

        var sum = await Q().Where(r => r.Tag == "absent-tag").SumAsync(r => r.N);
        Assert.True(sum.IsSuccess, sum.Error?.Message);
        Assert.Equal(0, sum.Value);

        var max = await Q().Where(r => r.Tag == "absent-tag").MaxAsync(r => r.N);
        Assert.True(max.IsSuccess, max.Error?.Message);
        Assert.Equal(0, max.Value);
    }

    [Fact]
    public async Task CountAsync_over_an_empty_set_is_zero()
    {
        await SeedAsync(0);

        var count = await Q().Where(r => r.Tag == "absent-tag").CountAsync();
        Assert.True(count.IsSuccess, count.Error?.Message);
        Assert.Equal(0, count.Value);
    }

    [Fact]
    public async Task GroupBy_collapses_the_result_to_one_row_per_group()
    {
        var tag = await SeedAsync();

        var grouped = await Q().Where(r => r.Tag == tag).GroupBy(r => r.Flag).ToListAsync();
        Assert.True(grouped.IsSuccess, grouped.Error?.Message);
        Assert.Equal(2, grouped.Value!.Count); // Flag true and false
    }

    [Fact]
    public async Task CountAsync_on_a_grouped_builder_counts_the_groups()
    {
        var tag = await SeedAsync();

        var groups = await Q().Where(r => r.Tag == tag).GroupBy(r => r.Flag).CountAsync();
        Assert.True(groups.IsSuccess, groups.Error?.ToString());
        Assert.Equal(2, groups.Value); // Flag true and false, not the five rows

        var rows = await Q().Where(r => r.Tag == tag).CountAsync();
        Assert.Equal(5, rows.Value);
    }

    [Fact]
    public async Task ToPagedListAsync_on_a_grouped_builder_totals_the_groups()
    {
        var tag = await SeedAsync();

        var paged = await Q().Where(r => r.Tag == tag).GroupBy(r => r.Flag).ToPagedListAsync(1, 10);
        Assert.True(paged.IsSuccess, paged.Error?.ToString());
        Assert.Equal(2, paged.Value!.TotalItems);
        Assert.Equal(2, paged.Value.Items.Count);
    }

    [Fact]
    public async Task The_typed_aggregates_refuse_a_grouped_builder()
    {
        var tag = await SeedAsync();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => Q().Where(r => r.Tag == tag).GroupBy(r => r.Flag).SumAsync(r => r.N));
        Assert.Contains("GroupBy", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ToListAsync", ex.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => Q().GroupBy(r => r.Flag).MinAsync(r => r.N));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => Q().GroupBy(r => r.Flag).MaxAsync(r => r.N));
    }

    [Fact]
    public async Task ToPagedListAsync_reports_the_filtered_total_not_the_table_total()
    {
        var tag = await SeedAsync();
        await SeedAsync(3); // unrelated rows that must not be counted

        var paged = await Q().Where(r => r.Tag == tag).OrderBy(r => r.N).ToPagedListAsync(2, 2);
        Assert.True(paged.IsSuccess, paged.Error?.Message);
        Assert.Equal(5, paged.Value!.TotalItems);
        Assert.Equal([2, 3], paged.Value.Items.Select(r => r.N));
        Assert.Equal(3, paged.Value.TotalPages);
    }

    // ── WHERE translation edges ───────────────────────────────────────────────

    [Fact]
    public async Task Several_Where_calls_are_combined_with_AND()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).Where(r => r.N > 1).Where(r => r.N < 4).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([2, 3], rows.Value!.Select(r => r.N).Order());
    }

    [Fact]
    public async Task A_predicate_with_more_than_ten_parameters_rekeys_without_clobbering()
    {
        var tag = await SeedAsync();

        // 12 parameters in one predicate: @p1 must not be substituted into @p10/@p11.
        var rows = await Q().Where(r =>
                r.Tag == tag &&
                r.N != 100 && r.N != 101 && r.N != 102 && r.N != 103 && r.N != 104 &&
                r.N != 105 && r.N != 106 && r.N != 107 && r.N != 108 && r.N != 109 &&
                r.N != 110)
            .ToListAsync();

        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal(5, rows.Value!.Count);
    }

    [Fact]
    public async Task An_OR_predicate_translates()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag && (r.N == 0 || r.N == 4)).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([0, 4], rows.Value!.Select(r => r.N).Order());
    }

    [Fact]
    public async Task A_negated_predicate_translates_to_NOT()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).Where(r => !(r.N < 3)).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([3, 4], rows.Value!.Select(r => r.N).Order());
    }

    [Fact]
    public async Task A_bare_boolean_property_is_treated_as_equals_true()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag).Where(r => r.Flag).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([0, 2, 4], rows.Value!.Select(r => r.N).Order());
    }

    [Fact]
    public async Task StartsWith_and_EndsWith_become_LIKE_patterns()
    {
        var tag = await SeedAsync();

        var prefix = tag[..4];
        var starts = await Q().Where(r => r.Tag.StartsWith(prefix)).CountAsync();
        Assert.True(starts.IsSuccess, starts.Error?.Message);
        Assert.True(starts.Value >= 5);

        var ends = await Q().Where(r => r.Tag == tag && r.Note != null && r.Note.EndsWith("3")).ToListAsync();
        Assert.True(ends.IsSuccess, ends.Error?.Message);
        Assert.Equal(3, Assert.Single(ends.Value!).N);
    }

    [Fact]
    public async Task An_empty_IN_list_matches_nothing_instead_of_being_a_syntax_error()
    {
        var tag = await SeedAsync();
        var ids = new List<long>();

        var rows = await Q().Where(r => r.Tag == tag && ids.Contains(r.Id)).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Empty(rows.Value!);

        var empty = Array.Empty<long>();
        var viaArray = await Q().Where(r => r.Tag == tag && empty.Contains(r.Id)).ToListAsync();
        Assert.True(viaArray.IsSuccess, viaArray.Error?.Message);
        Assert.Empty(viaArray.Value!);
    }

    [Fact]
    public async Task A_multi_element_IN_list_matches_exactly_those_rows()
    {
        var tag = await SeedAsync();
        var wanted = new List<int> { 1, 3 };

        var rows = await Q().Where(r => r.Tag == tag && wanted.Contains(r.N)).ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([1, 3], rows.Value!.Select(r => r.N).Order());
    }

    /// <summary>
    /// <c>QueryBuilder.Where</c> translates eagerly and does not wrap the translation in a
    /// try/catch, so an unsupported expression throws straight out of the fluent chain instead
    /// of surfacing as a failed <c>Result</c> from the terminal call. <c>Repository.FindAsync</c>
    /// takes the same predicate and *does* return a failed Result, so the two entry points
    /// disagree about how a translation failure is reported.
    /// </summary>
    [Fact]
    public async Task An_untranslatable_predicate_throws_from_Where_rather_than_failing_the_Result()
    {
        await SeedAsync(0);

        Assert.Throws<NotSupportedException>(() => Q().Where(r => r.Note!.Trim() == "x"));

        // The repository's equivalent reports the same predicate as a failed Result.
        Assert.True((await Lib.GetRepository<QbRow>().FindAsync(r => r.Note!.Trim() == "x")).IsFailure);
    }

    // ── Reserved-word and awkward identifiers in WHERE / ORDER BY ─────────────

    [Fact]
    public async Task The_whole_CRUD_path_works_for_columns_named_after_SQL_reserved_words()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);
        var repo = Lib.GetRepository<QbAwkward>();
        var marker = "m" + Guid.NewGuid().ToString("N")[..8];

        var first = await repo.InsertAsync(new QbAwkward { Order = 2, Group = marker, Index = "b", Kind = "k" });
        Assert.True(first.IsSuccess, first.Error?.ToString());
        Assert.True((await repo.InsertAsync(new QbAwkward { Order = 1, Group = marker, Index = "a", Kind = "k" })).IsSuccess);

        var ordered = await Lib.GetQueryBuilder<QbAwkward>()
            .Where(r => r.Group == marker).OrderBy(r => r.Order).ToListAsync();
        Assert.True(ordered.IsSuccess, ordered.Error?.ToString());
        Assert.Equal([1, 2], ordered.Value!.Select(r => r.Order));

        var descending = await Lib.GetQueryBuilder<QbAwkward>()
            .Where(r => r.Group == marker).OrderByDescending(r => r.Index).ToListAsync();
        Assert.True(descending.IsSuccess, descending.Error?.ToString());
        Assert.Equal("b", descending.Value![0].Index);

        var byReservedWord = await Lib.GetQueryBuilder<QbAwkward>()
            .Where(r => r.Group == marker && r.Order == 2).ToListAsync();
        Assert.True(byReservedWord.IsSuccess, byReservedWord.Error?.ToString());
        Assert.Equal("b", Assert.Single(byReservedWord.Value!).Index);

        // UPDATE and DELETE against the same reserved-word columns.
        var updated = await repo.UpdateAsync(new QbAwkward
        {
            Id = first.Value, Order = 3, Group = marker, Index = "c", Kind = "k"
        });
        Assert.True(updated.IsSuccess, updated.Error?.ToString());
        Assert.Equal(3, (await repo.GetByIdAsync(first.Value)).Value!.Order);

        Assert.True((await repo.DeleteAsync(first.Value)).IsSuccess);
        Assert.Null((await repo.GetByIdAsync(first.Value)).Value);
    }

    [Fact]
    public async Task A_reserved_word_column_round_trips_through_a_raw_query()
    {
        Assert.True((await Lib.TableSync.SyncTableAsync<QbAwkward>()).IsSuccess);
        var repo = Lib.GetRepository<QbAwkward>();
        var marker = "m" + Guid.NewGuid().ToString("N")[..8];
        await repo.InsertAsync(new QbAwkward { Order = 9, Group = marker });

        var rows = await repo.RawQueryAsync(
            "SELECT * FROM \"qb_awkward\" WHERE \"group\" = @g;",
            new Dictionary<string, object?> { ["@g"] = marker });

        Assert.True(rows.IsSuccess, rows.Error?.ToString());
        Assert.Equal(9, Assert.Single(rows.Value!).Order);
    }

    [Fact]
    public async Task ThenBy_and_ThenByDescending_add_secondary_sort_keys()
    {
        var tag = await SeedAsync();

        var rows = await Q().Where(r => r.Tag == tag)
            .OrderBy(r => r.Flag).ThenByDescending(r => r.N)
            .ToListAsync();
        Assert.True(rows.IsSuccess, rows.Error?.Message);
        Assert.Equal([3, 1, 4, 2, 0], rows.Value!.Select(r => r.N));

        var ascending = await Q().Where(r => r.Tag == tag)
            .OrderBy(r => r.Flag).ThenBy(r => r.N)
            .ToListAsync();
        Assert.Equal([1, 3, 0, 2, 4], ascending.Value!.Select(r => r.N));
    }

    // ── Builder reuse ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstOrDefaultAsync_leaves_the_builder_unchanged_for_reuse()
    {
        // The LIMIT 1 is local to the call: a builder reused afterwards still returns every row.
        var tag = await SeedAsync();
        var builder = Q().Where(r => r.Tag == tag).OrderBy(r => r.N);

        Assert.Equal(0, (await builder.FirstOrDefaultAsync()).Value!.N);

        var reused = await builder.ToListAsync();
        Assert.True(reused.IsSuccess, reused.Error?.Message);
        Assert.Equal([0, 1, 2, 3, 4], reused.Value!.Select(r => r.N));

        // An explicit Limit is still honoured, and still not disturbed by the terminal.
        var capped = Q().Where(r => r.Tag == tag).OrderBy(r => r.N).Limit(3);
        Assert.Equal(0, (await capped.FirstOrDefaultAsync()).Value!.N);
        Assert.Equal(3, (await capped.ToListAsync()).Value!.Count);
    }

    [Fact]
    public async Task A_query_builder_for_an_unknown_connection_id_fails_when_it_runs()
    {
        var result = await Lib.GetQueryBuilder<QbRow>("NoSuchDatabase").CountAsync();

        Assert.True(result.IsFailure);
        Assert.Contains("NoSuchDatabase", result.Error!.ToString());
    }
}

/// <summary>Private table for the unfiltered-delete test so no other test's rows are removed.</summary>
[SQLiteTable("qb_delete_all")]
public sealed class QbDeleteAll
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "v", DataType = SQLiteDataType.INTEGER)]
    public int V { get; set; }
}
