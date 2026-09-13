using System.Linq.Expressions;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// The internal helpers the query and schema paths are built on. All exercised indirectly by
// the live suite, none directly — so a regression in one would surface as a confusing
// failure somewhere far away rather than here.

public sealed class ClosureEvaluatorTests
{
    private sealed class Holder { public int Value = 7; public string? Text = "t"; }

    [Fact]
    public void Constant_is_evaluated_without_compiling()
    {
        Expression expr = Expression.Constant(42);
        Assert.True(ClosureEvaluator.TryFastEvaluate(expr, out var value));
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
        // TryFastEvaluate may decline; Evaluate must still produce the value.
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
        // The point of a sequential id is index locality: later ids must sort after earlier
        // ones, otherwise every insert lands in a random leaf page.
        var earlier = SequentialGuid.NewId(DateTimeOffset.UtcNow.AddMinutes(-5));
        var later = SequentialGuid.NewId(DateTimeOffset.UtcNow);
        Assert.True(string.CompareOrdinal(earlier.ToString(), later.ToString()) < 0,
            $"{earlier} should sort before {later}");
    }

    [Fact]
    public void NewId_honours_the_supplied_timestamp()
    {
        var a = SequentialGuid.NewId(DateTimeOffset.UtcNow.AddYears(-1));
        var b = SequentialGuid.NewId(DateTimeOffset.UtcNow);
        Assert.NotEqual(a, b);
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
    }
}

public sealed class MigrationVersionTests
{
    [Fact]
    public void Orders_by_version_then_by_order()
    {
        var versions = new[]
        {
            new MigrationVersion("1.2.0", 2),
            new MigrationVersion("1.10.0", 1),
            new MigrationVersion("1.2.0", 1),
            new MigrationVersion("0.9.0", 5),
        };
        var sorted = versions.OrderBy(v => v, Comparer<MigrationVersion>.Create((a, b) => a.CompareTo(b))).ToArray();

        Assert.Equal(new MigrationVersion("0.9.0", 5), sorted[0]);
        Assert.Equal(new MigrationVersion("1.2.0", 1), sorted[1]);
        Assert.Equal(new MigrationVersion("1.2.0", 2), sorted[2]);
        // 1.10.0 must sort after 1.2.0: a string compare would put it before.
        Assert.Equal(new MigrationVersion("1.10.0", 1), sorted[3]);
    }

    [Fact]
    public void Semver_components_compare_numerically_not_lexically()
    {
        var two = new MigrationVersion("1.2.0", 0);
        var ten = new MigrationVersion("1.10.0", 0);
        Assert.True(two.CompareTo(ten) < 0, "1.2.0 must precede 1.10.0");
    }

    [Fact]
    public void IsAtOrBelow_gates_on_the_app_version()
    {
        var v = new MigrationVersion("1.4.0", 1);
        Assert.True(v.IsAtOrBelow("1.4.0"));
        Assert.True(v.IsAtOrBelow("1.5.0"));
        Assert.True(v.IsAtOrBelow("2.0.0"));
        Assert.False(v.IsAtOrBelow("1.3.9"));
        Assert.False(v.IsAtOrBelow("0.9.0"));
    }

    [Fact]
    public void ToString_is_sortable()
    {
        Assert.Equal("1.4.0/003", new MigrationVersion("1.4.0", 3).ToString());
    }
}

public sealed class DialectQuotingTests
{
    [Fact]
    public void QuoteMultipart_quotes_each_segment()
    {
        Assert.Equal("\"app\".\"users\"", PostgreSqlDialect.QuoteMultipart("app.users"));
        Assert.Equal("\"users\"", PostgreSqlDialect.QuoteMultipart("users"));
    }

    [Fact]
    public void QuoteMultipart_escapes_within_a_segment()
    {
        Assert.Equal("\"a\"\"b\".\"c\"", PostgreSqlDialect.QuoteMultipart("a\"b.c"));
    }

    [Theory]
    [InlineData("app..users")]
    [InlineData(".users")]
    [InlineData("app.")]
    public void QuoteMultipart_rejects_empty_segments(string identifier)
    {
        Assert.Throws<ArgumentException>(() => PostgreSqlDialect.QuoteMultipart(identifier));
    }
}

public sealed class SchemaAnalyzerHelperTests
{
    [Table(Name = "helper_tbl", Schema = "helper_sch")]
    private sealed class Scoped { [Column(Name = "id", Primary = true)] public int Id { get; set; } }

    private sealed class Bare { [Column(Name = "id", Primary = true)] public int Id { get; set; } }

    [Fact]
    public void Name_helpers_respect_the_table_attribute()
    {
        Assert.Equal("helper_tbl", SchemaAnalyzer.GetTableName(typeof(Scoped)));
        Assert.Equal("helper_sch", SchemaAnalyzer.GetSchemaName(typeof(Scoped)));
        Assert.Equal("\"helper_sch\".\"helper_tbl\"", SchemaAnalyzer.GetQualifiedName(typeof(Scoped)));
    }

    [Fact]
    public void Name_helpers_fall_back_to_the_type_name_and_public()
    {
        Assert.Equal("Bare", SchemaAnalyzer.GetTableName(typeof(Bare)));
        Assert.Equal("public", SchemaAnalyzer.GetSchemaName(typeof(Bare)));
    }

    [Fact]
    public void ComputeCrc_is_deterministic_and_sensitive()
    {
        Assert.Equal(SchemaAnalyzer.ComputeCrc("abc"), SchemaAnalyzer.ComputeCrc("abc"));
        Assert.NotEqual(SchemaAnalyzer.ComputeCrc("abc"), SchemaAnalyzer.ComputeCrc("abd"));
        Assert.Equal(8, SchemaAnalyzer.ComputeCrc("abc").Length);
    }

    [Fact]
    public void Schema_crc_ignores_reflection_ordering_but_not_content()
    {
        var analyzer = new SchemaAnalyzer();
        var a = analyzer.ComputeSchemaCrc(typeof(Scoped));
        var b = analyzer.ComputeSchemaCrc(typeof(Scoped));
        Assert.Equal(a, b);
        Assert.NotEqual(a, analyzer.ComputeSchemaCrc(typeof(Bare)));
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

    private sealed class NoKeyRow
    {
        [Column(Name = "x")] public int X { get; set; }
    }

    [Fact]
    public void TryResolve_accepts_either_the_property_or_the_column_name()
    {
        Assert.Equal("row_id", EntityMetadata<MetaRow>.TryResolve("Id")?.ColumnName);
        Assert.Equal("row_id", EntityMetadata<MetaRow>.TryResolve("row_id")?.ColumnName);
        Assert.Null(EntityMetadata<MetaRow>.TryResolve("nope"));
    }

    [Fact]
    public void RequirePrimaryKey_returns_the_key_or_explains_its_absence()
    {
        Assert.Equal("row_id", EntityMetadata<MetaRow>.RequirePrimaryKey().ColumnName);

        var ex = Assert.Throws<InvalidOperationException>(() => EntityMetadata<NoKeyRow>.RequirePrimaryKey());
        // The message names the entity and the attribute to add, not just "no primary key".
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

    [Fact]
    public void Auto_increment_is_reported_from_the_attribute()
    {
        Assert.True(EntityMetadata<MetaRow>.RequireColumn("Id").IsAutoIncrement);
        Assert.False(EntityMetadata<MetaRow>.RequireColumn("Display").IsAutoIncrement);
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
    public void ToDbValue_normalises_datetime_kind()
    {
        var unspecified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var result = Assert.IsType<DateTime>(TypeConverter.ToDbValue(unspecified));
        Assert.Equal(DateTimeKind.Utc, result.Kind);

        var local = DateTime.SpecifyKind(new DateTime(2026, 1, 1, 12, 0, 0), DateTimeKind.Local);
        var converted = Assert.IsType<DateTime>(TypeConverter.ToDbValue(local));
        Assert.Equal(local.ToUniversalTime(), converted);
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
    public void FromDbValue_reports_an_impossible_conversion_with_both_type_names()
    {
        var ex = Assert.Throws<InvalidCastException>(
            () => TypeConverter.FromDbValue(new object(), typeof(int)));
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InferDataType_agrees_with_InferColumn()
    {
        Assert.Equal(DataType.Uuid, TypeConverter.InferDataType(typeof(Guid)));
        Assert.Equal(DataType.TimestampTz, TypeConverter.InferDataType(typeof(DateTime)));
        Assert.Equal(TypeConverter.InferColumn(typeof(string)).DataType,
                     TypeConverter.InferDataType(typeof(string)));
    }

    [Fact]
    public void ResolveColumn_fills_in_only_what_was_left_unset()
    {
        var declared = new ColumnAttribute { Size = 10, NotNull = true };
        var resolved = TypeConverter.ResolveColumn(declared, typeof(string));

        Assert.Equal(DataType.VarChar, resolved.DataType);   // inferred
        Assert.Equal(10, resolved.Size);                      // preserved
        Assert.True(resolved.NotNull);                        // preserved

        // An explicit type is never overridden.
        var explicitType = new ColumnAttribute { DataType = DataType.Text };
        Assert.Equal(DataType.Text, TypeConverter.ResolveColumn(explicitType, typeof(Guid)).DataType);
    }

    [Fact]
    public void CreateParameter_types_the_parameter_from_the_column()
    {
        var p = TypeConverter.CreateParameter("@p", Guid.NewGuid(),
            new ColumnAttribute { DataType = DataType.Uuid });
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.Uuid, p.NpgsqlDbType);

        // Null becomes DBNull rather than a null reference.
        var nullParam = TypeConverter.CreateParameter("@n", null, new ColumnAttribute { DataType = DataType.Text });
        Assert.Equal(DBNull.Value, nullParam.Value);

        // Without column metadata the connector is left to infer.
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
        var cols = JoinTranslator.KeyColumns(key, "t0");
        Assert.Equal(["\"t0\".\"id\""], cols);
    }

    [Fact]
    public void OnClause_pairs_the_two_sides()
    {
        Expression<Func<L, long>> left = l => l.Id;
        Expression<Func<R, long>> right = r => r.LeftId;
        var on = JoinTranslator.OnClause(left, "t0", right, "t1");

        Assert.Contains("\"t0\".\"id\"", on);
        Assert.Contains("\"t1\".\"left_id\"", on);
        Assert.Contains("=", on);
        Assert.DoesNotContain("`", on);
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
        Assert.Equal("\"t0\".\"name\"", JoinTranslator.OrderColumn(selector.Body, aliases));

        Expression<Func<L, R, long>> rightSide = (l, r) => r.LeftId;
        var rightAliases = new Dictionary<ParameterExpression, string>
        {
            [rightSide.Parameters[0]] = "t0",
            [rightSide.Parameters[1]] = "t1",
        };
        Assert.Equal("\"t1\".\"left_id\"", JoinTranslator.OrderColumn(rightSide.Body, rightAliases));
    }
}
