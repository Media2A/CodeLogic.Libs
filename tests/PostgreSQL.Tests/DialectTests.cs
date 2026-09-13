using CL.PostgreSQL.Configuration;
using CL.PostgreSQL.Core;
using CL.PostgreSQL.Models;
using Xunit;

namespace PostgreSQL.Tests;

// Offline verification that the generated SQL is actually PostgreSQL. CL.PostgreSQL was
// ported from CL.MySQL2, so the failure mode these tests exist to catch is a MySQL
// construct surviving the port and only blowing up against a live server.

// ── Test entities ────────────────────────────────────────────────────────────────

[Table(Name = "widgets", Schema = "inventory", Comment = "A widget")]
[CompositeIndex("ix_widget_sku_region", "sku", "region")]
[CompositeIndex("uq_widget_code", "code", Unique = true)]
public sealed class DialectWidget
{
    [Column(Name = "id", Primary = true, AutoIncrement = true)]
    public int Id { get; set; }

    [Column(Name = "code", NotNull = true)]
    public string Code { get; set; } = "";

    [Column(Name = "sku", Size = 64, NotNull = true, Index = true)]
    public string Sku { get; set; } = "";

    [Column(Name = "region", Size = 16)]
    public string? Region { get; set; }

    [Column(Name = "price", DataType = DataType.Numeric, Precision = 12, Scale = 4)]
    public decimal Price { get; set; }

    [Column(Name = "external_id")]
    public Guid ExternalId { get; set; }

    [Column(Name = "created_utc", DefaultValue = "now()")]
    public DateTime CreatedUtc { get; set; }

    [Column(Name = "updated_utc", OnUpdateCurrentTimestamp = true)]
    public DateTime UpdatedUtc { get; set; }

    [Column(Name = "payload", DataType = DataType.Jsonb)]
    public string? Payload { get; set; }
}

[Table(Name = "plain")]
public sealed class PlainRow
{
    [Column(Name = "id", Primary = true)] public long Id { get; set; }
    [Column(Name = "label")] public string Label { get; set; } = "";
}

// ── Dialect primitives ───────────────────────────────────────────────────────────

public sealed class PostgreSqlDialectTests
{
    [Fact]
    public void Quote_uses_double_quotes()
    {
        Assert.Equal("\"users\"", PostgreSqlDialect.Quote("users"));
    }

    [Fact]
    public void Quote_doubles_an_embedded_double_quote()
    {
        // Without doubling this would close the identifier and inject SQL.
        Assert.Equal("\"a\"\"b\"", PostgreSqlDialect.Quote("a\"b"));
    }

    [Fact]
    public void Qualify_emits_schema_and_table()
    {
        Assert.Equal("\"inventory\".\"widgets\"", PostgreSqlDialect.Qualify("inventory", "widgets"));
    }

    [Fact]
    public void EscapeLike_neutralises_wildcards()
    {
        Assert.Equal(@"100\%", PostgreSqlDialect.EscapeLike("100%"));
        Assert.Equal(@"a\_b", PostgreSqlDialect.EscapeLike("a_b"));
        Assert.Equal(@"a\\\%", PostgreSqlDialect.EscapeLike(@"a\%"));
    }

    [Fact]
    public void MaxBatchRows_respects_the_parameter_ceiling()
    {
        // 65535 - 16 reserved, over 10 columns per row.
        Assert.Equal((65535 - 16) / 10, PostgreSqlDialect.MaxBatchRows(10));
        Assert.Equal(1, PostgreSqlDialect.MaxBatchRows(100_000));
    }

    [Fact]
    public void NullSafeEquals_is_the_standard_spelling()
    {
        // MySQL's <=> does not exist in PostgreSQL.
        Assert.Equal("IS NOT DISTINCT FROM", PostgreSqlDialect.NullSafeEquals);
    }
}

// ── Type mapping ─────────────────────────────────────────────────────────────────

public sealed class TypeMappingTests
{
    private static string Ddl(Type clr)
    {
        var attr = TypeConverter.InferColumn(clr);
        return TypeConverter.GetPostgreSqlType(attr, attr.StorageType, clr);
    }

    [Theory]
    [InlineData(typeof(bool), "boolean")]
    [InlineData(typeof(short), "smallint")]
    [InlineData(typeof(int), "integer")]
    [InlineData(typeof(long), "bigint")]
    [InlineData(typeof(float), "real")]
    [InlineData(typeof(double), "double precision")]
    [InlineData(typeof(string), "character varying(255)")]
    [InlineData(typeof(byte[]), "bytea")]
    [InlineData(typeof(DateOnly), "date")]
    public void Clr_types_infer_native_postgres_types(Type clr, string expected)
    {
        Assert.Equal(expected, Ddl(clr));
    }

    [Fact]
    public void Guid_infers_native_uuid_not_char36()
    {
        // MySQL stores a Guid as CHAR(36); PostgreSQL has a real 16-byte uuid type.
        Assert.Equal("uuid", Ddl(typeof(Guid)));
    }

    [Fact]
    public void DateTime_infers_timestamptz()
    {
        // "timestamp without time zone" would silently discard the offset.
        Assert.Equal("timestamp with time zone", Ddl(typeof(DateTime)));
    }

    [Fact]
    public void Byte_widens_to_smallint()
    {
        // PostgreSQL has no 1-byte integer and no unsigned types.
        Assert.Equal("smallint", Ddl(typeof(byte)));
    }

    [Fact]
    public void No_mysql_type_names_are_emitted()
    {
        var mysqlOnly = new[] { "TINYINT", "DATETIME", "BLOB", "LONGTEXT", "MEDIUMINT", "UNSIGNED" };
        foreach (var clr in new[]
                 {
                     typeof(bool), typeof(byte), typeof(int), typeof(long), typeof(string),
                     typeof(DateTime), typeof(Guid), typeof(byte[]), typeof(decimal), typeof(TimeSpan)
                 })
        {
            var ddl = Ddl(clr);
            foreach (var bad in mysqlOnly)
                Assert.DoesNotContain(bad, ddl, StringComparison.OrdinalIgnoreCase);
        }
    }
}

// ── Type-equivalence: the comparison that decides whether to rewrite a table ──────

public sealed class TypeEquivalenceTests
{
    // The pre-port library compared format_type output against generated DDL with a plain
    // string compare, so almost every column looked changed and every sync rewrote the
    // whole table. These pin the alias folding that prevents that.
    [Theory]
    [InlineData("character varying(255)", "character varying(255)")]
    [InlineData("character varying(255)", "varchar(255)")]
    [InlineData("integer", "int4")]
    [InlineData("integer", "int")]
    [InlineData("bigint", "int8")]
    [InlineData("smallint", "int2")]
    [InlineData("boolean", "bool")]
    [InlineData("timestamp with time zone", "timestamptz")]
    [InlineData("timestamp without time zone", "timestamp")]
    [InlineData("double precision", "float8")]
    [InlineData("numeric(12,4)", "decimal(12,4)")]
    [InlineData("character(1)", "bpchar(1)")]
    public void Aliases_are_equivalent(string dbType, string modelType)
    {
        Assert.True(SchemaAnalyzer.TypesEquivalent(dbType, modelType),
            $"'{dbType}' should be equivalent to '{modelType}'");
    }

    [Fact]
    public void Unspecified_precision_matches_any_precision()
    {
        Assert.True(SchemaAnalyzer.TypesEquivalent("numeric(10,2)", "numeric"));
        Assert.True(SchemaAnalyzer.TypesEquivalent("numeric", "numeric(10,2)"));
    }

    [Theory]
    [InlineData("integer", "bigint")]
    [InlineData("text", "character varying(255)")]
    [InlineData("timestamp with time zone", "timestamp without time zone")]
    [InlineData("character varying(64)", "character varying(255)")]
    [InlineData("uuid", "text")]
    public void Genuinely_different_types_are_not_equivalent(string dbType, string modelType)
    {
        Assert.False(SchemaAnalyzer.TypesEquivalent(dbType, modelType),
            $"'{dbType}' should NOT be equivalent to '{modelType}'");
    }

    [Fact]
    public void Array_types_keep_their_suffix()
    {
        Assert.True(SchemaAnalyzer.TypesEquivalent("integer[]", "int4[]"));
        Assert.False(SchemaAnalyzer.TypesEquivalent("integer[]", "integer"));
    }
}

// ── Generated DDL ────────────────────────────────────────────────────────────────

public sealed class CreateTableDdlTests
{
    private static readonly string Sql = new SchemaAnalyzer().GenerateCreateTable(typeof(DialectWidget));

    [Fact]
    public void Table_is_schema_qualified()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"inventory\".\"widgets\"", Sql);
    }

    [Fact]
    public void Identity_replaces_auto_increment()
    {
        Assert.Contains("GENERATED BY DEFAULT AS IDENTITY", Sql);
        Assert.DoesNotContain("AUTO_INCREMENT", Sql);
    }

    [Fact]
    public void Indexes_are_separate_create_index_statements()
    {
        // PostgreSQL has no inline INDEX clause inside CREATE TABLE.
        Assert.Contains("CREATE INDEX IF NOT EXISTS \"idx_widgets_sku\"", Sql);
        Assert.Contains("CREATE INDEX IF NOT EXISTS \"ix_widget_sku_region\"", Sql);
        Assert.DoesNotContain("  INDEX ", Sql);
        Assert.DoesNotContain("UNIQUE KEY", Sql);
    }

    [Fact]
    public void Unique_composite_stays_a_table_constraint()
    {
        // It must be a constraint, not just an index, so ON CONFLICT can arbitrate on it.
        Assert.Contains("CONSTRAINT \"uq_widget_code\" UNIQUE (\"code\")", Sql);
    }

    [Fact]
    public void Comments_are_separate_statements()
    {
        Assert.Contains("COMMENT ON TABLE \"inventory\".\"widgets\" IS 'A widget';", Sql);
        Assert.DoesNotContain("COMMENT=", Sql);
    }

    [Fact]
    public void On_update_timestamp_becomes_a_trigger()
    {
        // MySQL's ON UPDATE CURRENT_TIMESTAMP has no PostgreSQL column-clause equivalent.
        Assert.DoesNotContain("ON UPDATE CURRENT_TIMESTAMP", Sql);
        Assert.Contains("CREATE OR REPLACE FUNCTION \"inventory\".\"fn_widgets_updated_utc_touch\"()", Sql);
        Assert.Contains("CREATE TRIGGER \"trg_widgets_updated_utc_touch\" BEFORE UPDATE", Sql);
    }

    [Fact]
    public void Native_types_are_used()
    {
        Assert.Contains("\"external_id\" uuid", Sql);
        Assert.Contains("\"created_utc\" timestamp with time zone", Sql);
        Assert.Contains("\"payload\" jsonb", Sql);
        Assert.Contains("\"price\" numeric(12,4)", Sql);
    }

    [Fact]
    public void Explicit_and_inferred_sizes_are_respected()
    {
        Assert.Contains("\"sku\" character varying(64)", Sql);
        // 'code' declares no size, so it falls back to the default string size.
        Assert.Contains("\"code\" character varying(255)", Sql);
    }

    [Fact]
    public void No_mysql_constructs_survive()
    {
        foreach (var bad in new[]
                 {
                     "ENGINE=", "DEFAULT CHARSET", "utf8mb4", "AUTO_INCREMENT",
                     "UNIQUE KEY", "`", "ON UPDATE CURRENT_TIMESTAMP", "COMMENT=",
                 })
        {
            Assert.DoesNotContain(bad, Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Plain_entity_generates_minimal_valid_ddl()
    {
        var sql = new SchemaAnalyzer().GenerateCreateTable(typeof(PlainRow));
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"public\".\"plain\"", sql);
        Assert.Contains("\"id\" bigint NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (\"id\")", sql);
        Assert.DoesNotContain("`", sql);
    }
}

// ── Entity metadata ──────────────────────────────────────────────────────────────

public sealed class EntityMetadataTests
{
    [Fact]
    public void Schema_defaults_to_public()
    {
        Assert.Equal("public", EntityMetadata<PlainRow>.SchemaName);
        Assert.Equal("\"public\".\"plain\"", EntityMetadata<PlainRow>.QualifiedTableName);
    }

    [Fact]
    public void Declared_schema_is_honoured()
    {
        Assert.Equal("inventory", EntityMetadata<DialectWidget>.SchemaName);
        Assert.Equal("\"inventory\".\"widgets\"", EntityMetadata<DialectWidget>.QualifiedTableName);
    }

    [Fact]
    public void Unknown_column_is_rejected()
    {
        // The allow-list that keeps string-typed column APIs from becoming an injection point.
        Assert.Throws<ArgumentException>(() => EntityMetadata<PlainRow>.RequireColumn("id\"; DROP TABLE x --"));
    }

    [Fact]
    public void Column_resolves_by_property_or_column_name()
    {
        Assert.Equal("label", EntityMetadata<PlainRow>.RequireColumn("Label").ColumnName);
        Assert.Equal("label", EntityMetadata<PlainRow>.RequireColumn("label").ColumnName);
    }
}

// ── Connection string ────────────────────────────────────────────────────────────

public sealed class ConnectionStringTests
{
    [Fact]
    public void Special_characters_are_quoted_not_injected()
    {
        // Built through NpgsqlConnectionStringBuilder: a ';' in a value must not be able to
        // append a new connection option.
        var cfg = new PostgreSqlDatabaseConfig
        {
            Host = "localhost",
            Database = "db",
            Username = "user",
            Password = "pa;ss=word'\"x"
        };

        var connStr = cfg.BuildConnectionString();
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connStr);

        Assert.Equal("pa;ss=word'\"x", parsed.Password);
        Assert.Equal("db", parsed.Database);
    }

    [Fact]
    public void Ssl_mode_maps_onto_npgsql()
    {
        var cfg = new PostgreSqlDatabaseConfig
        {
            Host = "h", Database = "d", Username = "u",
            SslMode = PostgreSqlSslMode.VerifyFull
        };
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(cfg.BuildConnectionString());
        Assert.Equal(Npgsql.SslMode.VerifyFull, parsed.SslMode);
    }

    [Fact]
    public void Default_schema_becomes_search_path()
    {
        var cfg = new PostgreSqlDatabaseConfig
        {
            Host = "h", Database = "d", Username = "u", DefaultSchema = "inventory"
        };
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(cfg.BuildConnectionString());
        Assert.Equal("inventory", parsed.SearchPath);
    }

    [Fact]
    public void Validation_rejects_an_inverted_pool_range()
    {
        var cfg = new PostgreSqlDatabaseConfig
        {
            Host = "h", Database = "d", Username = "u",
            MinPoolSize = 50, MaxPoolSize = 10
        };
        Assert.False(cfg.Validate().IsValid);
    }
}
