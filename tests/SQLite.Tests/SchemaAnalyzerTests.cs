using CL.SQLite.Models;
using CL.SQLite.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace SQLite.Tests;

// ── Entities used only for schema generation ──────────────────────────────────

[SQLiteTable("schema_kitchen_sink")]
[SQLiteIndex("a", "b", Name = "ix_composite")]
[SQLiteIndex("c", IsUnique = true)]
public sealed class KitchenSink
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "a", DataType = SQLiteDataType.TEXT, Size = 64, IsNotNull = true)]
    public string A { get; set; } = "";

    [SQLiteColumn(ColumnName = "b", DataType = SQLiteDataType.INTEGER, IsIndexed = true)]
    public int B { get; set; }

    [SQLiteColumn(ColumnName = "c", DataType = SQLiteDataType.TEXT, IsUnique = true)]
    public string C { get; set; } = "";

    [SQLiteColumn(ColumnName = "d", DataType = SQLiteDataType.INTEGER, DefaultValue = "7")]
    public int D { get; set; }

    // No attribute — must be ignored entirely.
    public string Ignored { get; set; } = "";
}

[SQLiteTable("schema_fk_child")]
public sealed class FkChild
{
    [SQLiteColumn(IsPrimaryKey = true, IsAutoIncrement = true, ColumnName = "id", DataType = SQLiteDataType.INTEGER)]
    public long Id { get; set; }

    [SQLiteColumn(ColumnName = "parent_id", DataType = SQLiteDataType.INTEGER)]
    [SQLiteForeignKey("schema_fk_parent", "id",
        OnDelete = ForeignKeyAction.Cascade, OnUpdate = ForeignKeyAction.Restrict)]
    public long ParentId { get; set; }
}

/// <summary>Entity with a two-column primary key.</summary>
[SQLiteTable("schema_composite")]
public sealed class CompositeKeyed
{
    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "k1", DataType = SQLiteDataType.TEXT)]
    public string K1 { get; set; } = "";

    [SQLiteColumn(IsPrimaryKey = true, ColumnName = "k2", DataType = SQLiteDataType.INTEGER)]
    public int K2 { get; set; }

    [SQLiteColumn(ColumnName = "payload", DataType = SQLiteDataType.TEXT)]
    public string Payload { get; set; } = "";
}

/// <summary>Entity with no <see cref="SQLiteTableAttribute"/>: the type name is the table name.</summary>
public sealed class UnattributedEntity
{
    [SQLiteColumn(DataType = SQLiteDataType.INTEGER)]
    public int Value { get; set; }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="SchemaAnalyzer"/> is <c>internal</c> in CL.SQLite, which grants
/// <c>InternalsVisibleTo("SQLite.Tests")</c> as its three sibling database libraries do, so
/// these tests call it directly. Nothing here needs the CodeLogic runtime.
/// </summary>
public sealed class SchemaAnalyzerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_sa_" + Guid.NewGuid().ToString("N"));

    public SchemaAnalyzerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── GetTableName ──────────────────────────────────────────────────────────

    [Fact]
    public void GetTableName_prefers_the_table_attribute()
        => Assert.Equal("schema_kitchen_sink", SchemaAnalyzer.GetTableName(typeof(KitchenSink)));

    [Fact]
    public void GetTableName_falls_back_to_the_type_name()
        => Assert.Equal(nameof(UnattributedEntity), SchemaAnalyzer.GetTableName(typeof(UnattributedEntity)));

    // ── MapDataType ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SQLiteDataType.INTEGER, "INTEGER")]
    [InlineData(SQLiteDataType.REAL, "REAL")]
    [InlineData(SQLiteDataType.TEXT, "TEXT")]
    [InlineData(SQLiteDataType.BLOB, "BLOB")]
    [InlineData(SQLiteDataType.NUMERIC, "NUMERIC")]
    [InlineData(SQLiteDataType.DATETIME, "TEXT")]
    [InlineData(SQLiteDataType.DATE, "TEXT")]
    [InlineData(SQLiteDataType.BOOLEAN, "INTEGER")]
    [InlineData(SQLiteDataType.UUID, "TEXT")]
    public void MapDataType_maps_every_enum_member_to_a_storage_class(SQLiteDataType dt, string expected)
        => Assert.Equal(expected, new SchemaAnalyzer().MapDataType(dt));

    [Fact]
    public void MapDataType_falls_back_to_TEXT_for_an_out_of_range_value()
        => Assert.Equal("TEXT", new SchemaAnalyzer().MapDataType((SQLiteDataType)999));

    // ── GetModelColumns ───────────────────────────────────────────────────────

    [Fact]
    public void GetModelColumns_returns_only_attributed_properties()
    {
        var cols = new SchemaAnalyzer().GetModelColumns(typeof(KitchenSink));

        Assert.Equal(5, cols.Count);
        Assert.DoesNotContain(cols, c => c.PropertyName == nameof(KitchenSink.Ignored));
        Assert.Equal(["id", "a", "b", "c", "d"], cols.Select(c => c.ColumnName));
    }

    [Fact]
    public void GetModelColumns_carries_the_attribute_options_onto_the_definition()
    {
        var cols = new SchemaAnalyzer().GetModelColumns(typeof(KitchenSink));

        var id = cols.Single(c => c.ColumnName == "id");
        Assert.True(id.IsPrimaryKey);
        Assert.True(id.IsAutoIncrement);
        Assert.True(id.IsNotNull); // a primary key is implicitly NOT NULL

        var a = cols.Single(c => c.ColumnName == "a");
        Assert.True(a.IsNotNull);
        Assert.Equal(typeof(string), a.PropertyType);

        Assert.True(cols.Single(c => c.ColumnName == "c").IsUnique);
        Assert.Equal("7", cols.Single(c => c.ColumnName == "d").DefaultValue);
    }

    [Fact]
    public void GetModelColumns_uses_the_property_name_when_ColumnName_is_omitted()
    {
        var cols = new SchemaAnalyzer().GetModelColumns(typeof(UnattributedEntity));
        Assert.Equal("Value", Assert.Single(cols).ColumnName);
    }

    [Fact]
    public void GetModelColumns_surfaces_the_foreign_key_attribute()
    {
        var col = new SchemaAnalyzer().GetModelColumns(typeof(FkChild)).Single(c => c.ColumnName == "parent_id");

        Assert.NotNull(col.ForeignKey);
        Assert.Equal("schema_fk_parent", col.ForeignKey!.ReferencedTable);
        Assert.Equal("id", col.ForeignKey.ReferencedColumn);
        Assert.Equal(ForeignKeyAction.Cascade, col.ForeignKey.OnDelete);
        Assert.Equal(ForeignKeyAction.Restrict, col.ForeignKey.OnUpdate);
    }

    // ── GenerateCreateTableSql ────────────────────────────────────────────────

    [Fact]
    public void GenerateCreateTableSql_emits_quoted_identifiers_and_every_constraint()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_kitchen_sink", a.GetModelColumns(typeof(KitchenSink)));

        Assert.StartsWith("CREATE TABLE IF NOT EXISTS \"schema_kitchen_sink\" (", sql);
        Assert.Contains("\"id\" INTEGER PRIMARY KEY AUTOINCREMENT", sql);
        Assert.Contains("\"a\" TEXT(64) NOT NULL", sql);
        Assert.Contains("\"c\" TEXT UNIQUE", sql);
        Assert.Contains("\"d\" INTEGER DEFAULT 7", sql);
        Assert.EndsWith(");", sql);
    }

    /// <summary>
    /// <c>SQLiteColumnAttribute.Size</c> is emitted as a length modifier on the declared type.
    /// SQLite records the declared type but does not enforce the length, and the modifier does
    /// not change the column's TEXT affinity — <c>0</c> (the default) emits nothing.
    /// </summary>
    [Fact]
    public void Size_on_a_column_attribute_becomes_a_declared_length()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_kitchen_sink", a.GetModelColumns(typeof(KitchenSink)));

        Assert.Contains("\"a\" TEXT(64) NOT NULL", sql);
        // Columns that declare no Size are unchanged.
        Assert.Contains("\"c\" TEXT UNIQUE", sql);
        Assert.Contains("\"b\" INTEGER", sql);
    }

    [Fact]
    public void A_declared_length_keeps_the_columns_TEXT_affinity()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_kitchen_sink", a.GetModelColumns(typeof(KitchenSink)));

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "sized.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT type FROM pragma_table_info('schema_kitchen_sink') WHERE name = 'a';";
        Assert.Equal("TEXT(64)", Convert.ToString(check.ExecuteScalar()));

        using var affinity = conn.CreateCommand();
        affinity.CommandText = "INSERT INTO \"schema_kitchen_sink\" (\"a\", \"c\") VALUES ('x', 'y'); " +
                               "SELECT typeof(\"a\") FROM \"schema_kitchen_sink\";";
        Assert.Equal("text", Convert.ToString(affinity.ExecuteScalar()));
    }

    [Fact]
    public void A_primary_key_is_not_also_given_a_redundant_NOT_NULL_or_UNIQUE()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_kitchen_sink", a.GetModelColumns(typeof(KitchenSink)));

        Assert.DoesNotContain("PRIMARY KEY AUTOINCREMENT NOT NULL", sql);
        Assert.DoesNotContain("PRIMARY KEY AUTOINCREMENT UNIQUE", sql);
    }

    [Fact]
    public void GenerateCreateTableSql_emits_a_FOREIGN_KEY_clause_with_the_mapped_actions()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_fk_child", a.GetModelColumns(typeof(FkChild)));

        Assert.Contains(
            "FOREIGN KEY (\"parent_id\") REFERENCES \"schema_fk_parent\" (\"id\") ON DELETE CASCADE ON UPDATE RESTRICT",
            sql);
    }

    [Theory]
    [InlineData(ForeignKeyAction.NoAction, "NO ACTION")]
    [InlineData(ForeignKeyAction.Restrict, "RESTRICT")]
    [InlineData(ForeignKeyAction.SetNull, "SET NULL")]
    [InlineData(ForeignKeyAction.SetDefault, "SET DEFAULT")]
    [InlineData(ForeignKeyAction.Cascade, "CASCADE")]
    public void Every_ForeignKeyAction_maps_to_its_SQL_spelling(ForeignKeyAction action, string expected)
    {
        var a = new SchemaAnalyzer();
        var cols = a.GetModelColumns(typeof(FkChild));
        var fkCol = cols.Single(c => c.ColumnName == "parent_id");
        fkCol.ForeignKey = new SQLiteForeignKeyAttribute("p", "id") { OnDelete = action, OnUpdate = action };

        var sql = a.GenerateCreateTableSql("t", cols);
        Assert.Contains($"ON DELETE {expected} ON UPDATE {expected}", sql);
    }

    /// <summary>
    /// Two or more key columns become one table-level constraint — SQLite rejects a table with
    /// more than one inline PRIMARY KEY — and each key column keeps its NOT NULL.
    /// </summary>
    [Fact]
    public void A_composite_key_becomes_one_table_level_PRIMARY_KEY_constraint()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_composite", a.GetModelColumns(typeof(CompositeKeyed)));

        Assert.Contains("PRIMARY KEY (\"k1\", \"k2\")", sql);
        Assert.Contains("\"k1\" TEXT NOT NULL", sql);
        Assert.Contains("\"k2\" INTEGER NOT NULL", sql);
        Assert.DoesNotContain("\"k1\" TEXT PRIMARY KEY", sql);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "composite.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A single auto-increment key stays inline: SQLite only accepts AUTOINCREMENT on an inline
    /// INTEGER primary key, never on a table-level constraint.
    /// </summary>
    [Fact]
    public void A_single_auto_increment_key_stays_inline()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_kitchen_sink", a.GetModelColumns(typeof(KitchenSink)));

        Assert.Contains("\"id\" INTEGER PRIMARY KEY AUTOINCREMENT", sql);
        Assert.DoesNotContain("PRIMARY KEY (", sql);
    }

    [Fact]
    public void The_generated_DDL_actually_executes_against_SQLite()
    {
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("schema_kitchen_sink", a.GetModelColumns(typeof(KitchenSink)));

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "ddl.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('schema_kitchen_sink');";
        Assert.Equal(5L, Convert.ToInt64(check.ExecuteScalar()));
    }

    [Fact]
    public void Generating_DDL_for_a_table_with_no_columns_produces_invalid_SQL()
    {
        // Documented edge: an entity with no [SQLiteColumn] yields "CREATE TABLE ... (\n);",
        // which SQLite rejects. The analyzer does not guard against it.
        var a = new SchemaAnalyzer();
        var sql = a.GenerateCreateTableSql("empty_tbl", a.GetModelColumns(typeof(ConfigurationTests)));

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "empty.db")}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    // ── TableExistsAsync / GetDatabaseColumnsAsync ────────────────────────────

    [Fact]
    public async Task TableExistsAsync_is_false_before_creation_and_true_after()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "exists.db")}");
        await conn.OpenAsync();
        var a = new SchemaAnalyzer();

        Assert.False(await a.TableExistsAsync(conn, "later"));

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE later(x INTEGER);";
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.True(await a.TableExistsAsync(conn, "later"));
    }

    [Fact]
    public async Task TableExistsAsync_matches_the_name_exactly_and_ignores_views_and_indexes()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "kinds.db")}");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE base(x INTEGER);
                CREATE VIEW v AS SELECT * FROM base;
                CREATE INDEX ix ON base(x);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var a = new SchemaAnalyzer();
        Assert.True(await a.TableExistsAsync(conn, "base"));
        Assert.False(await a.TableExistsAsync(conn, "v"));
        Assert.False(await a.TableExistsAsync(conn, "ix"));
        Assert.False(await a.TableExistsAsync(conn, "BASE_"));
    }

    [Fact]
    public async Task GetDatabaseColumnsAsync_lists_the_columns_in_declaration_order()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "cols.db")}");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t(\"id\" INTEGER, \"Order\" INTEGER, \"weird name\" TEXT);";
            await cmd.ExecuteNonQueryAsync();
        }

        var cols = await new SchemaAnalyzer().GetDatabaseColumnsAsync(conn, "t");
        Assert.Equal(["id", "Order", "weird name"], cols);
    }

    [Fact]
    public async Task GetDatabaseColumnsAsync_returns_an_empty_list_for_a_missing_table()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "missing.db")}");
        await conn.OpenAsync();

        Assert.Empty(await new SchemaAnalyzer().GetDatabaseColumnsAsync(conn, "not_there"));
    }
}
