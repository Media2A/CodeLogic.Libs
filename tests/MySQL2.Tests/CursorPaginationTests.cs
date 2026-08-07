using CL.MySQL2.Core;
using CL.MySQL2.Models;
using Xunit;

namespace MySQL2.Tests;

public sealed class CursorPaginationUnitTests
{
    [Fact]
    public void Token_round_trips_typed_values_as_base64url()
    {
        var orders = Orders((nameof(CursorUnitRow.Rank), false), (nameof(CursorUnitRow.Id), false));
        var row = new CursorUnitRow { Id = 42, Rank = 7 };

        var token = CursorTokenCodec.Encode(row, orders);
        var values = CursorTokenCodec.Decode<CursorUnitRow>(token, orders);

        Assert.DoesNotContain("+", token);
        Assert.DoesNotContain("/", token);
        Assert.DoesNotContain("=", token);
        Assert.Equal(7, values[0]);
        Assert.Equal(42L, values[1]);
    }

    [Fact]
    public void Token_rejects_malformed_and_mismatched_ordering()
    {
        var orders = Orders((nameof(CursorUnitRow.Rank), false), (nameof(CursorUnitRow.Id), false));
        var reversed = Orders((nameof(CursorUnitRow.Rank), true), (nameof(CursorUnitRow.Id), false));
        var token = CursorTokenCodec.Encode(new CursorUnitRow { Id = 1, Rank = null }, orders);

        Assert.Equal("mysql.invalid_cursor",
            Assert.Throws<CursorPagingException>(() =>
                CursorTokenCodec.Decode<CursorUnitRow>("not-a-token", orders)).ErrorCode);
        Assert.Equal("mysql.invalid_cursor",
            Assert.Throws<CursorPagingException>(() =>
                CursorTokenCodec.Decode<CursorUnitRow>(token, reversed)).ErrorCode);
    }

    [Fact]
    public void Token_rejects_oversized_input_before_decoding()
    {
        var orders = Orders((nameof(CursorUnitRow.Rank), false), (nameof(CursorUnitRow.Id), false));
        var oversized = new string('A', CursorTokenCodec.MaxEncodedLength + 1);

        var error = Assert.Throws<CursorPagingException>(() =>
            CursorTokenCodec.Decode<CursorUnitRow>(oversized, orders));

        Assert.Equal("mysql.invalid_cursor", error.ErrorCode);
        Assert.Contains(CursorTokenCodec.MaxEncodedLength.ToString(), error.Message);
    }

    [Fact]
    public void Effective_order_appends_primary_key_with_final_direction()
    {
        var explicitOrders = Orders((nameof(CursorUnitRow.Rank), true));

        var effective = CursorPagination.GetEffectiveOrder<CursorUnitRow>(explicitOrders);

        Assert.Equal(2, effective.Count);
        Assert.Equal("rank", effective[0].Column.ColumnName);
        Assert.Equal("id", effective[1].Column.ColumnName);
        Assert.All(effective, order => Assert.True(order.Descending));
    }

    [Fact]
    public void Seek_predicate_matches_mysql_null_ordering()
    {
        var ascending = Orders((nameof(CursorUnitRow.Rank), false), (nameof(CursorUnitRow.Id), false));
        var (ascSql, ascParams) = CursorPagination.BuildSeekPredicate(ascending, [null, 10L]);

        Assert.Contains("`rank` IS NOT NULL", ascSql);
        Assert.Contains("`rank` <=> @cursor_0 AND `id` > @cursor_1", ascSql);
        Assert.Equal(DBNull.Value, ascParams["@cursor_0"]);

        var descending = Orders((nameof(CursorUnitRow.Rank), true), (nameof(CursorUnitRow.Id), true));
        var (descSql, _) = CursorPagination.BuildSeekPredicate(descending, [5, 10L]);

        Assert.Contains("`rank` < @cursor_0 OR `rank` IS NULL", descSql);
        Assert.Contains("`rank` <=> @cursor_0 AND (`id` < @cursor_1 OR `id` IS NULL)", descSql);
    }

    private static List<CursorOrder> Orders(params (string Property, bool Descending)[] values) =>
        values.Select(value => new CursorOrder(
            EntityMetadata<CursorUnitRow>.RequireColumn(value.Property), value.Descending)).ToList();

    [Table(Name = "cursor_unit")]
    private sealed class CursorUnitRow
    {
        [Column(Name = "id", DataType = DataType.BigInt, Primary = true)]
        public long Id { get; set; }

        [Column(Name = "rank", DataType = DataType.Int)]
        public int? Rank { get; set; }
    }
}
