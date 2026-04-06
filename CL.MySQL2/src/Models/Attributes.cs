namespace CL.MySQL2.Models;

/// <summary>
/// Marks a class as a database table entity.
/// Apply to a class to enable table sync and repository operations.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    /// <summary>Custom table name. Defaults to the class name if null.</summary>
    public string? Name { get; set; }

    /// <summary>MySQL storage engine. Default: InnoDB.</summary>
    public TableEngine Engine { get; set; } = TableEngine.InnoDB;

    /// <summary>Table character set. Default: utf8mb4.</summary>
    public Charset Charset { get; set; } = Charset.Utf8mb4;

    /// <summary>Table collation (e.g., "utf8mb4_unicode_ci"). Optional.</summary>
    public string? Collation { get; set; }

    /// <summary>Table comment. Optional.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// Maps a property to a database column with optional schema metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute : Attribute
{
    /// <summary>Custom column name. Defaults to the property name if null.</summary>
    public string? Name { get; set; }

    /// <summary>MySQL data type for the column.</summary>
    public DataType DataType { get; set; }

    /// <summary>Column size / length (e.g., 255 for VARCHAR(255)). 0 = type default.</summary>
    public int Size { get; set; } = 0;

    /// <summary>Numeric precision (for DECIMAL columns). Default: 10.</summary>
    public int Precision { get; set; } = 10;

    /// <summary>Numeric scale (decimal places, for DECIMAL columns). Default: 2.</summary>
    public int Scale { get; set; } = 2;

    /// <summary>Whether this column is the primary key.</summary>
    public bool Primary { get; set; } = false;

    /// <summary>Whether this column uses AUTO_INCREMENT.</summary>
    public bool AutoIncrement { get; set; } = false;

    /// <summary>Whether this column has a NOT NULL constraint.</summary>
    public bool NotNull { get; set; } = false;

    /// <summary>Whether this column has a UNIQUE constraint.</summary>
    public bool Unique { get; set; } = false;

    /// <summary>Whether this column has a plain INDEX.</summary>
    public bool Index { get; set; } = false;

    /// <summary>Default value expression (e.g., "0", "'active'", "CURRENT_TIMESTAMP").</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Column-level character set override (for string types). Optional.</summary>
    public string? Charset { get; set; }

    /// <summary>Column comment. Optional.</summary>
    public string? Comment { get; set; }

    /// <summary>Whether this column is UNSIGNED (for numeric types).</summary>
    public bool Unsigned { get; set; } = false;

    /// <summary>Whether to add ON UPDATE CURRENT_TIMESTAMP (for TIMESTAMP/DATETIME).</summary>
    public bool OnUpdateCurrentTimestamp { get; set; } = false;
}

/// <summary>
/// Declares a foreign key constraint on a column property.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ForeignKeyAttribute : Attribute
{
    /// <summary>The referenced table name. Required.</summary>
    public string ReferenceTable { get; }

    /// <summary>The referenced column name. Required.</summary>
    public string ReferenceColumn { get; }

    /// <summary>Action when the referenced row is deleted. Default: Restrict.</summary>
    public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.Restrict;

    /// <summary>Action when the referenced row is updated. Default: Restrict.</summary>
    public ForeignKeyAction OnUpdate { get; set; } = ForeignKeyAction.Restrict;

    /// <summary>Custom constraint name. Auto-generated if null.</summary>
    public string? ConstraintName { get; set; }

    /// <summary>
    /// Declares a foreign key to the specified table and column.
    /// </summary>
    /// <param name="referenceTable">The referenced table name.</param>
    /// <param name="referenceColumn">The referenced column name.</param>
    public ForeignKeyAttribute(string referenceTable, string referenceColumn)
    {
        ReferenceTable = referenceTable;
        ReferenceColumn = referenceColumn;
    }
}

/// <summary>Referential action for ON DELETE / ON UPDATE foreign key clauses.</summary>
public enum ForeignKeyAction
{
    Restrict,
    Cascade,
    SetNull,
    NoAction,
    SetDefault
}

/// <summary>
/// Excludes a property from all database operations (read, write, schema sync).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreAttribute : Attribute { }

/// <summary>
/// Declares a composite index spanning multiple columns.
/// Apply to the class (not a single property) by using the index column names.
/// Can be applied multiple times for multiple composite indexes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class CompositeIndexAttribute : Attribute
{
    /// <summary>The index name (must be unique within the table).</summary>
    public string IndexName { get; }

    /// <summary>The column names included in this index.</summary>
    public string[] ColumnNames { get; }

    /// <summary>Whether this is a unique index. Default: false.</summary>
    public bool Unique { get; set; } = false;

    /// <summary>
    /// Declares a composite index with the given name covering the specified columns.
    /// </summary>
    /// <param name="indexName">Index name (unique within the table).</param>
    /// <param name="columnNames">One or more column names to include.</param>
    public CompositeIndexAttribute(string indexName, params string[] columnNames)
    {
        IndexName = indexName;
        ColumnNames = columnNames;
    }
}

/// <summary>
/// Marks a property as a many-to-many navigation property resolved via a junction table entity.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ManyToManyAttribute : Attribute
{
    /// <summary>The CLR type of the junction table entity.</summary>
    public Type JunctionEntityType { get; }

    /// <summary>
    /// Declares a many-to-many relationship via the specified junction entity type.
    /// </summary>
    /// <param name="junctionEntityType">The junction table entity type.</param>
    public ManyToManyAttribute(Type junctionEntityType)
    {
        JunctionEntityType = junctionEntityType;
    }
}
