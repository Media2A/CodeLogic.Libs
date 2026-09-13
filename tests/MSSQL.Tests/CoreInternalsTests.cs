using System.Linq.Expressions;
using CL.MSSQL.Core;
using CL.MSSQL.Models;
using Xunit;

namespace MSSQL.Tests;

// The internal helpers the query and schema paths are built on. Exercised indirectly
// everywhere, directly nowhere.

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
    public void Quote_escapes_the_closing_bracket()
    {
        Assert.Equal("[users]", SqlServerDialect.Quote("users"));
        // Without doubling, a ']' would close the identifier early and inject SQL.
        Assert.Equal("[a]]b]", SqlServerDialect.Quote("a]b"));
    }

    [Fact]
    public void Qualify_and_QuoteMultipart_handle_schemas()
    {
        Assert.Equal("[dbo].[users]", SqlServerDialect.Qualify("dbo", "users"));
        Assert.Equal("[dbo].[users]", SqlServerDialect.QuoteMultipart("dbo.users"));
        Assert.Equal("[users]", SqlServerDialect.QuoteMultipart("users"));
    }

    [Theory]
    [InlineData("dbo..users")]
    [InlineData(".users")]
    [InlineData("dbo.")]
    public void QuoteMultipart_rejects_empty_segments(string identifier)
    {
        Assert.Throws<ArgumentException>(() => SqlServerDialect.QuoteMultipart(identifier));
    }

    [Fact]
    public void EscapeLike_uses_bracket_escaping()
    {
        // SQL Server escapes LIKE metacharacters by bracketing them, not with a backslash.
        Assert.Equal("100[%]", SqlServerDialect.EscapeLike("100%"));
        Assert.Equal("a[_]b", SqlServerDialect.EscapeLike("a_b"));
        Assert.Equal("[[]x]", SqlServerDialect.EscapeLike("[x]"));
        Assert.Equal("plain", SqlServerDialect.EscapeLike("plain"));
    }

    [Fact]
    public void MaxBatchRows_respects_the_parameter_ceiling()
    {
        // SQL Server caps a statement at 2100 parameters.
        Assert.Equal((2100 - 16) / 10, SqlServerDialect.MaxBatchRows(10));
        Assert.Equal(1, SqlServerDialect.MaxBatchRows(100_000));
    }
}

public sealed class SchemaAnalyzerHelperTests
{
    [Table(Name = "helper_tbl", Schema = "helper_sch")]
    private sealed class Scoped
    {
        [Column(DataType = DataType.Int, Primary = true)] public int Id { get; set; }
    }

    private sealed class Bare
    {
        [Column(DataType = DataType.Int, Primary = true)] public int Id { get; set; }
    }

    [Fact]
    public void Name_helpers_respect_the_table_attribute()
    {
        Assert.Equal("helper_tbl", SchemaAnalyzer.GetTableName(typeof(Scoped)));
        Assert.Equal("helper_sch", SchemaAnalyzer.GetSchemaName(typeof(Scoped)));
    }

    [Fact]
    public void Name_helpers_fall_back_to_the_type_name_and_dbo()
    {
        Assert.Equal("Bare", SchemaAnalyzer.GetTableName(typeof(Bare)));
        Assert.Equal("dbo", SchemaAnalyzer.GetSchemaName(typeof(Bare)));
    }

    [Fact]
    public void ComputeCrc_is_deterministic_and_sensitive()
    {
        Assert.Equal(SchemaAnalyzer.ComputeCrc("abc"), SchemaAnalyzer.ComputeCrc("abc"));
        Assert.NotEqual(SchemaAnalyzer.ComputeCrc("abc"), SchemaAnalyzer.ComputeCrc("abd"));
        Assert.Equal(8, SchemaAnalyzer.ComputeCrc("abc").Length);
    }

    [Fact]
    public void Schema_crc_is_stable_and_content_sensitive()
    {
        var analyzer = new SchemaAnalyzer();
        Assert.Equal(analyzer.ComputeSchemaCrc(typeof(Scoped)), analyzer.ComputeSchemaCrc(typeof(Scoped)));
        Assert.NotEqual(analyzer.ComputeSchemaCrc(typeof(Scoped)), analyzer.ComputeSchemaCrc(typeof(Bare)));
    }
}

public sealed class EntityMetadataResolutionTests
{
    [Table(Name = "meta_row")]
    private sealed class MetaRow
    {
        [Column(Name = "row_id", DataType = DataType.BigInt, Primary = true, AutoIncrement = true)]
        public long Id { get; set; }

        [Column(Name = "display", DataType = DataType.NVarChar, Size = 50)]
        public string? Display { get; set; }
    }

    private sealed class NoKeyRow
    {
        [Column(Name = "x", DataType = DataType.Int)] public int X { get; set; }
    }

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
    }

    [Fact]
    public void Qualified_name_is_schema_scoped()
    {
        Assert.Equal("[dbo].[meta_row]", EntityMetadata<MetaRow>.QualifiedTableName);
        Assert.Equal("dbo", EntityMetadata<MetaRow>.SchemaName);
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
    public void ToDbValue_maps_enums_and_leaves_null_for_the_binding_site()
    {
        Assert.Equal(1, TypeConverter.ToDbValue(Colour.Green, StorageType.Default));

        // Note the divergence from CL.MySQL2 and CL.PostgreSQL, whose ToDbValue returns
        // DBNull.Value for null. Here null passes through and every binding site coalesces
        // it — CreateParameter does `value ?? DBNull.Value`, and the AddWithValue callers
        // do the same. Pinned so the convention is not "tidied" in one place only.
        Assert.Null(TypeConverter.ToDbValue(null, StorageType.Default));
        Assert.Equal(DBNull.Value,
            TypeConverter.CreateParameter("@n", TypeConverter.ToDbValue(null, StorageType.Default)).Value);
    }

    [Fact]
    public void FromDbValue_handles_null_enum_and_exact_matches()
    {
        Assert.Null(TypeConverter.FromDbValue(null, typeof(int?), StorageType.Default));
        Assert.Null(TypeConverter.FromDbValue(DBNull.Value, typeof(string), StorageType.Default));
        Assert.Equal(Colour.Green, TypeConverter.FromDbValue(1, typeof(Colour), StorageType.Default));
        Assert.Equal("x", TypeConverter.FromDbValue("x", typeof(string), StorageType.Default));
    }

    [Fact]
    public void Unspecified_infers_from_the_clr_type()
    {
        var attr = new ColumnAttribute();
        Assert.Equal(DataType.Unspecified, attr.DataType);
        Assert.Equal("nvarchar(255)", TypeConverter.GetSqlServerType(attr, StorageType.Default, typeof(string)));
        Assert.Equal("uniqueidentifier", TypeConverter.GetSqlServerType(attr, StorageType.Default, typeof(Guid)));
        Assert.Equal("bigint", TypeConverter.GetSqlServerType(attr, StorageType.Default, typeof(long)));
    }

    [Fact]
    public void Explicit_type_and_size_win_over_inference()
    {
        var attr = new ColumnAttribute { DataType = DataType.VarChar, Size = 12 };
        Assert.Equal("varchar(12)", TypeConverter.GetSqlServerType(attr, StorageType.Default, typeof(string)));
    }

    [Fact]
    public void CreateParameter_types_the_parameter_from_the_column()
    {
        var p = TypeConverter.CreateParameter("@p", Guid.NewGuid(),
            new ColumnAttribute { DataType = DataType.UniqueIdentifier });
        Assert.Equal(System.Data.SqlDbType.UniqueIdentifier, p.SqlDbType);

        var nullParam = TypeConverter.CreateParameter("@n", null,
            new ColumnAttribute { DataType = DataType.NVarChar, Size = 10 });
        Assert.Equal(DBNull.Value, nullParam.Value);
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
        [Column(Name = "id", DataType = DataType.BigInt, Primary = true)] public long Id { get; set; }
        [Column(Name = "name", DataType = DataType.NVarChar, Size = 50)] public string? Name { get; set; }
    }

    [Table(Name = "jt_right")]
    private sealed class R
    {
        [Column(Name = "left_id", DataType = DataType.BigInt)] public long LeftId { get; set; }
    }

    [Fact]
    public void KeyColumns_qualifies_with_the_alias()
    {
        Expression<Func<L, long>> key = l => l.Id;
        Assert.Equal(["[t0].[id]"], JoinTranslator.KeyColumns(key, "t0"));
    }

    [Fact]
    public void OnClause_pairs_the_two_sides()
    {
        Expression<Func<L, long>> left = l => l.Id;
        Expression<Func<R, long>> right = r => r.LeftId;
        var on = JoinTranslator.OnClause(left, "t0", right, "t1");

        Assert.Contains("[t0].[id]", on);
        Assert.Contains("[t1].[left_id]", on);
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
        Assert.Equal("[t0].[name]", JoinTranslator.OrderColumn(selector.Body, aliases));
    }
}
