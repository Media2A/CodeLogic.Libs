using CL.MySQL2.Core;
using CL.MySQL2.Models;
using Xunit;

namespace MySQL2.Tests;

// Regression cover for the hardening back-ported from CL.MSSQL:
//   • DataType.Unspecified resolves against the CLR property type
//   • identifiers are escaped at the render site
//   • identifiers carrying a backtick are rejected at the metadata source
//   • LIKE metacharacters are escaped
// All offline — no database required.

public sealed class MySqlDialectTests
{
    [Fact]
    public void Quote_wraps_in_backticks()
    {
        Assert.Equal("`users`", MySqlDialect.Quote("users"));
    }

    [Fact]
    public void Quote_doubles_an_embedded_backtick()
    {
        // Without doubling this would close the identifier and inject SQL.
        Assert.Equal("`a``b`", MySqlDialect.Quote("a`b"));
    }

    [Fact]
    public void Quote_output_is_unchanged_for_safe_identifiers()
    {
        // Guarantees the dialect rollout did not alter generated DDL text,
        // which the schema CRC is computed from.
        foreach (var id in new[] { "users", "user_id", "Order2", "créé" })
            Assert.Equal($"`{id}`", MySqlDialect.Quote(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Quote_rejects_blank_identifiers(string identifier)
    {
        Assert.Throws<ArgumentException>(() => MySqlDialect.Quote(identifier));
    }

    [Fact]
    public void QuoteMultipart_quotes_each_part()
    {
        Assert.Equal("`app`.`users`", MySqlDialect.QuoteMultipart("app.users"));
    }

    [Fact]
    public void QuoteMultipart_rejects_empty_parts()
    {
        Assert.Throws<ArgumentException>(() => MySqlDialect.QuoteMultipart("app..users"));
    }

    [Fact]
    public void EscapeLike_neutralises_wildcards_and_the_escape_character()
    {
        Assert.Equal(@"100\%", MySqlDialect.EscapeLike("100%"));
        Assert.Equal(@"a\_b", MySqlDialect.EscapeLike("a_b"));
        // The backslash is escaped first so it cannot double-escape a later wildcard.
        Assert.Equal(@"a\\\%", MySqlDialect.EscapeLike(@"a\%"));
    }

    [Fact]
    public void EscapeLike_leaves_ordinary_text_alone()
    {
        Assert.Equal("plain text", MySqlDialect.EscapeLike("plain text"));
    }
}

public sealed class UnspecifiedDataTypeTests
{
    private sealed class Row
    {
        // Attribute present but no DataType: must infer from the CLR type rather than
        // resolving to TinyInt (enum default) as it did before Unspecified was added.
        [Column(Name = "email")] public string Email { get; set; } = "";
        [Column(Name = "external_id")] public Guid ExternalId { get; set; }
        [Column(Name = "hits")] public long Hits { get; set; }
        // Explicit declaration must win over inference.
        [Column(Name = "note", DataType = DataType.Text)] public string Note { get; set; } = "";
        // Explicit size must survive inference.
        [Column(Name = "code", Size = 8)] public string Code { get; set; } = "";
    }

    private static string Ddl(string property)
    {
        var prop = typeof(Row).GetProperty(property)!;
        var attr = (ColumnAttribute)Attribute.GetCustomAttribute(prop, typeof(ColumnAttribute))!;
        return TypeConverter.GetMySqlType(attr, attr.StorageType, prop.PropertyType);
    }

    [Fact]
    public void String_column_without_datatype_infers_varchar()
    {
        Assert.Equal("VARCHAR(255)", Ddl(nameof(Row.Email)));
    }

    [Fact]
    public void Guid_column_without_datatype_infers_char_36()
    {
        // Previously CHAR(1) — the inferred size was dropped along with the inferred type.
        Assert.Equal("CHAR(36)", Ddl(nameof(Row.ExternalId)));
    }

    [Fact]
    public void Long_column_without_datatype_infers_bigint()
    {
        Assert.Equal("BIGINT", Ddl(nameof(Row.Hits)));
    }

    [Fact]
    public void Explicit_datatype_is_not_overridden_by_inference()
    {
        Assert.Equal("TEXT", Ddl(nameof(Row.Note)));
    }

    [Fact]
    public void Explicit_size_survives_inference()
    {
        Assert.Equal("VARCHAR(8)", Ddl(nameof(Row.Code)));
    }

    [Fact]
    public void Unspecified_is_the_enum_default()
    {
        Assert.Equal(DataType.Unspecified, new ColumnAttribute().DataType);
        Assert.Equal(0, (int)DataType.Unspecified);
    }
}

public sealed class EntityMetadataIdentifierTests
{
    [Table(Name = "ok_table")]
    private sealed class SafeRow
    {
        [Column(Name = "id", Primary = true)] public int Id { get; set; }
    }

    [Table(Name = "bad`table")]
    private sealed class UnsafeTableRow
    {
        [Column(Name = "id", Primary = true)] public int Id { get; set; }
    }

    private sealed class UnsafeColumnRow
    {
        [Column(Name = "id`, (SELECT 1)) -- ", Primary = true)] public int Id { get; set; }
    }

    [Fact]
    public void Safe_identifiers_are_accepted()
    {
        Assert.Equal("ok_table", EntityMetadata<SafeRow>.TableName);
        Assert.Equal("id", EntityMetadata<SafeRow>.Columns[0].ColumnName);
    }

    [Fact]
    public void Table_name_containing_a_backtick_is_rejected()
    {
        // Static-ctor failures surface as TypeInitializationException.
        var ex = Assert.ThrowsAny<Exception>(() => _ = EntityMetadata<UnsafeTableRow>.TableName);
        Assert.Contains("not permitted in a MySQL identifier", Flatten(ex));
    }

    [Fact]
    public void Column_name_containing_a_backtick_is_rejected()
    {
        var ex = Assert.ThrowsAny<Exception>(() => _ = EntityMetadata<UnsafeColumnRow>.TableName);
        Assert.Contains("not permitted in a MySQL identifier", Flatten(ex));
    }

    private static string Flatten(Exception ex)
    {
        var text = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            text += " | " + inner.Message;
        return text;
    }
}
