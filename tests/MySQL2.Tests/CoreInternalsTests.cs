using System.Linq.Expressions;
using CL.MySQL2.Core;
using CL.MySQL2.Models;
using Xunit;

namespace MySQL2.Tests;

// The internal helpers the query and schema paths are built on. Exercised indirectly
// everywhere, directly nowhere — so a regression in one surfaces as a confusing failure
// somewhere far away.

public sealed class ClosureEvaluatorTests
{
    private sealed class Holder { public int Value = 7; }

    [Fact]
    public void Constant_is_evaluated_without_compiling()
    {
        Assert.True(ClosureEvaluator.TryFastEvaluate(Expression.Constant(42), out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void Captured_field_is_evaluated()
    {
        var holder = new Holder();
        Expression<Func<int>> lambda = () => holder.Value;
        Assert.Equal(7, ClosureEvaluator.Evaluate(lambda.Body));
    }

    [Fact]
    public void Null_captured_value_round_trips_as_null()
    {
        string? nothing = null;
        Expression<Func<string?>> lambda = () => nothing;
        Assert.Null(ClosureEvaluator.Evaluate(lambda.Body));
    }

    [Fact]
    public void Arbitrary_expression_still_evaluates_via_the_slow_path()
    {
        Expression<Func<int>> lambda = () => 6 * 7;
        Assert.Equal(42, ClosureEvaluator.Evaluate(lambda.Body));
    }
}

public sealed class SequentialGuidTests
{
    [Fact]
    public void NewId_is_unique()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => SequentialGuid.NewId()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, ids);
    }

    [Fact]
    public void NewId_is_time_ordered()
    {
        // Index locality is the whole point: a later id must sort after an earlier one.
        var earlier = SequentialGuid.NewId(DateTimeOffset.UtcNow.AddMinutes(-5));
        var later = SequentialGuid.NewId(DateTimeOffset.UtcNow);
        Assert.True(string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0);
    }
}

public sealed class MigrationVersionTests
{
    [Fact]
    public void Semver_components_compare_numerically_not_lexically()
    {
        Assert.True(new MigrationVersion("1.2.0", 0).CompareTo(new MigrationVersion("1.10.0", 0)) < 0,
            "1.2.0 must precede 1.10.0; a string compare gets this backwards");
    }

    [Fact]
    public void Orders_by_version_then_order()
    {
        var sorted = new[]
        {
            new MigrationVersion("1.2.0", 2),
            new MigrationVersion("1.10.0", 1),
            new MigrationVersion("1.2.0", 1),
            new MigrationVersion("0.9.0", 5),
        }.OrderBy(v => v, Comparer<MigrationVersion>.Create((a, b) => a.CompareTo(b))).ToArray();

        Assert.Equal(new MigrationVersion("0.9.0", 5), sorted[0]);
        Assert.Equal(new MigrationVersion("1.2.0", 1), sorted[1]);
        Assert.Equal(new MigrationVersion("1.2.0", 2), sorted[2]);
        Assert.Equal(new MigrationVersion("1.10.0", 1), sorted[3]);
    }

    [Fact]
    public void IsAtOrBelow_gates_on_the_app_version()
    {
        var v = new MigrationVersion("1.4.0", 1);
        Assert.True(v.IsAtOrBelow("1.4.0"));
        Assert.True(v.IsAtOrBelow("2.0.0"));
        Assert.False(v.IsAtOrBelow("1.3.9"));
    }
}

public sealed class DialectTests
{
    [Fact]
    public void Quote_and_QuoteMultipart_escape_the_delimiter()
    {
        Assert.Equal("`users`", MySqlDialect.Quote("users"));
        Assert.Equal("`a``b`", MySqlDialect.Quote("a`b"));
        Assert.Equal("`db`.`users`", MySqlDialect.QuoteMultipart("db.users"));
        Assert.Equal("`a``b`.`c`", MySqlDialect.QuoteMultipart("a`b.c"));
    }

    [Theory]
    [InlineData("db..users")]
    [InlineData(".users")]
    [InlineData("db.")]
    public void QuoteMultipart_rejects_empty_segments(string identifier)
    {
        Assert.Throws<ArgumentException>(() => MySqlDialect.QuoteMultipart(identifier));
    }

    [Fact]
    public void EscapeLike_neutralises_wildcards_and_the_escape_character()
    {
        Assert.Equal(@"100\%", MySqlDialect.EscapeLike("100%"));
        Assert.Equal(@"a\_b", MySqlDialect.EscapeLike("a_b"));
        Assert.Equal(@"a\\\%", MySqlDialect.EscapeLike(@"a\%"));
        Assert.Equal("plain", MySqlDialect.EscapeLike("plain"));
    }
}

public sealed class SchemaAnalyzerHelperTests
{
    [Table(Name = "helper_tbl")]
    private sealed class Named { [Column(Name = "id", Primary = true)] public int Id { get; set; } }

    private sealed class Bare { [Column(Name = "id", Primary = true)] public int Id { get; set; } }

    [Fact]
    public void GetTableName_uses_the_attribute_or_the_type_name()
    {
        Assert.Equal("helper_tbl", SchemaAnalyzer.GetTableName(typeof(Named)));
        Assert.Equal("Bare", SchemaAnalyzer.GetTableName(typeof(Bare)));
    }

    [Fact]
    public void ComputeCrc_is_deterministic_and_sensitive()
    {
        Assert.Equal(SchemaAnalyzer.ComputeCrc("abc"), SchemaAnalyzer.ComputeCrc("abc"));
        Assert.NotEqual(SchemaAnalyzer.ComputeCrc("abc"), SchemaAnalyzer.ComputeCrc("abd"));
        Assert.Equal(8, SchemaAnalyzer.ComputeCrc("abc").Length);
    }

    [Fact]
    public void GenerateCreateTable_emits_escaped_identifiers_and_a_stable_crc()
    {
        var analyzer = new SchemaAnalyzer();
        var ddl = analyzer.GenerateCreateTable(typeof(Named));

        Assert.Contains("CREATE TABLE IF NOT EXISTS `helper_tbl`", ddl);
        Assert.Contains("ENGINE=", ddl);

        Assert.Equal(analyzer.ComputeSchemaCrc(typeof(Named)), analyzer.ComputeSchemaCrc(typeof(Named)));
        Assert.NotEqual(analyzer.ComputeSchemaCrc(typeof(Named)), analyzer.ComputeSchemaCrc(typeof(Bare)));
    }
}

public sealed class EntityMetadataResolutionTests
{
    [Table(Name = "meta_row")]
    private sealed class MetaRow
    {
        [Column(Name = "row_id", Primary = true, AutoIncrement = true)] public long Id { get; set; }
        [Column(Name = "display")] public string? Display { get; set; }
    }

    private sealed class NoKeyRow { [Column(Name = "x")] public int X { get; set; } }

    [Fact]
    public void TryResolve_accepts_either_the_property_or_the_column_name()
    {
        Assert.Equal("row_id", EntityMetadata<MetaRow>.TryResolve("Id")?.ColumnName);
        Assert.Equal("row_id", EntityMetadata<MetaRow>.TryResolve("row_id")?.ColumnName);
        Assert.Null(EntityMetadata<MetaRow>.TryResolve("nope"));
    }

    [Fact]
    public void RequirePrimaryKey_returns_the_key_or_names_what_is_missing()
    {
        Assert.Equal("row_id", EntityMetadata<MetaRow>.RequirePrimaryKey().ColumnName);

        var ex = Assert.Throws<InvalidOperationException>(() => EntityMetadata<NoKeyRow>.RequirePrimaryKey());
        Assert.Contains("NoKeyRow", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Primary = true", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiled_accessors_read_and_write_without_reflection()
    {
        var col = EntityMetadata<MetaRow>.RequireColumn("Display");
        var row = new MetaRow();
        col.Set(row, "written");
        Assert.Equal("written", row.Display);
        Assert.Equal("written", col.Get(row));
    }
}

public sealed class TypeConverterUnitTests
{
    private enum Colour { Red = 0, Green = 1 }

    [Fact]
    public void ToDbValue_maps_null_and_enums()
    {
        Assert.Equal(DBNull.Value, TypeConverter.ToDbValue(null));
        Assert.Equal(1, TypeConverter.ToDbValue(Colour.Green));
    }

    [Fact]
    public void FromDbValue_handles_null_enum_and_exact_matches()
    {
        Assert.Null(TypeConverter.FromDbValue(null, typeof(int?)));
        Assert.Null(TypeConverter.FromDbValue(DBNull.Value, typeof(string)));
        Assert.Equal(Colour.Green, TypeConverter.FromDbValue(1, typeof(Colour)));
        Assert.Equal("x", TypeConverter.FromDbValue("x", typeof(string)));
    }

    [Fact]
    public void InferColumn_picks_the_right_type_and_size()
    {
        Assert.Equal(DataType.VarChar, TypeConverter.InferColumn(typeof(string)).DataType);
        Assert.Equal(255, TypeConverter.InferColumn(typeof(string)).Size);
        // A Guid is CHAR(36): the inferred size has to survive, or it renders as CHAR(1).
        Assert.Equal(DataType.Char, TypeConverter.InferColumn(typeof(Guid)).DataType);
        Assert.Equal(36, TypeConverter.InferColumn(typeof(Guid)).Size);
        Assert.True(TypeConverter.InferColumn(typeof(uint)).Unsigned);
    }

    [Fact]
    public void InferDataType_agrees_with_InferColumn()
    {
        Assert.Equal(TypeConverter.InferColumn(typeof(long)).DataType,
                     TypeConverter.InferDataType(typeof(long)));
    }

    [Fact]
    public void ResolveColumn_fills_in_only_what_was_left_unset()
    {
        var declared = new ColumnAttribute { Size = 10, NotNull = true };
        var resolved = TypeConverter.ResolveColumn(declared, typeof(string));

        Assert.Equal(DataType.VarChar, resolved.DataType);
        Assert.Equal(10, resolved.Size);
        Assert.True(resolved.NotNull);

        var explicitType = new ColumnAttribute { DataType = DataType.Text };
        Assert.Equal(DataType.Text, TypeConverter.ResolveColumn(explicitType, typeof(Guid)).DataType);
    }

    [Fact]
    public void Unspecified_infers_rather_than_falling_back_to_TinyInt()
    {
        // The bug this guards: DataType defaulted to TinyInt, so a [Column] with no
        // DataType silently generated TINYINT for a string.
        var attr = new ColumnAttribute { Name = "email" };
        Assert.Equal(DataType.Unspecified, attr.DataType);
        Assert.Equal("VARCHAR(255)", TypeConverter.GetMySqlType(attr, StorageType.Default, typeof(string)));
        Assert.Equal("CHAR(36)", TypeConverter.GetMySqlType(attr, StorageType.Default, typeof(Guid)));
    }

    [Fact]
    public void CreateParameter_types_the_parameter_from_the_column()
    {
        var p = TypeConverter.CreateParameter("@p", 5L, new ColumnAttribute { DataType = DataType.BigInt });
        Assert.Equal(MySqlConnector.MySqlDbType.Int64, p.MySqlDbType);

        var nullParam = TypeConverter.CreateParameter("@n", null, new ColumnAttribute { DataType = DataType.Text });
        Assert.Equal(DBNull.Value, nullParam.Value);

        var untyped = TypeConverter.CreateParameter("@u", 5);
        Assert.Equal(5, untyped.Value);
    }

    [Fact]
    public void Binary_storage_round_trips_a_guid_through_bytes()
    {
        var id = Guid.NewGuid();
        var bytes = Assert.IsType<byte[]>(TypeConverter.ToDbValue(id, StorageType.Binary));
        Assert.Equal(16, bytes.Length);
        Assert.Equal(id, TypeConverter.FromDbValue(bytes, typeof(Guid), StorageType.Binary));
    }
}

public sealed class JoinTranslatorTests
{
    [Table(Name = "jt_left")]
    private sealed class L
    {
        [Column(Name = "id", Primary = true)] public long Id { get; set; }
        [Column(Name = "name")] public string? Name { get; set; }
    }

    [Table(Name = "jt_right")]
    private sealed class R
    {
        [Column(Name = "left_id")] public long LeftId { get; set; }
    }

    [Fact]
    public void KeyColumns_qualifies_with_the_alias()
    {
        Expression<Func<L, long>> key = l => l.Id;
        Assert.Equal(["`t0`.`id`"], JoinTranslator.KeyColumns(key, "t0"));
    }

    [Fact]
    public void OnClause_pairs_the_two_sides()
    {
        Expression<Func<L, long>> left = l => l.Id;
        Expression<Func<R, long>> right = r => r.LeftId;
        var on = JoinTranslator.OnClause(left, "t0", right, "t1");

        Assert.Contains("`t0`.`id`", on);
        Assert.Contains("`t1`.`left_id`", on);
        Assert.Contains("=", on);
    }

    [Fact]
    public void OrderColumn_resolves_against_the_alias_map()
    {
        Expression<Func<L, R, string?>> selector = (l, r) => l.Name;
        var aliases = new Dictionary<ParameterExpression, string>
        {
            [selector.Parameters[0]] = "t0",
            [selector.Parameters[1]] = "t1",
        };
        Assert.Equal("`t0`.`name`", JoinTranslator.OrderColumn(selector.Body, aliases));
    }
}
