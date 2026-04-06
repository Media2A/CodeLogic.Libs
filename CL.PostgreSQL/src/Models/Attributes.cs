namespace CL.PostgreSQL.Models;

/// <summary>
/// Marks a class as a PostgreSQL table entity.
/// Apply to a class to enable table sync and repository operations.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TableAttribute : Attribute
{
    /// <summary>Custom table name. Defaults to the class name if null.</summary>
    public string? Name { get; set; }

    /// <summary>PostgreSQL schema. Default: "public".</summary>
    public string? Schema { get; set; } = "public";

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

    /// <summary>PostgreSQL data type for the column.</summary>
    public DataType DataType { get; set; }

    /// <summary>Column size / length (e.g., 255 for VARCHAR(255)). 0 = type default.</summary>
    public int Size { get; set; } = 0;

    /// <summary>Numeric precision (for NUMERIC columns). Default: 10.</summary>
    public int Precision { get; set; } = 10;

    /// <summary>Numeric scale (decimal places, for NUMERIC columns). Default: 2.</summary>
    public int Scale { get; set; } = 2;

    /// <summary>Whether this column is the primary key.</summary>
    public bool Primary { get; set; } = false;

    /// <summary>Whether this column uses GENERATED ALWAYS AS IDENTITY.</summary>
    public bool AutoIncrement { get; set; } = false;

    /// <summary>Whether this column has a NOT NULL constraint.</summary>
    public bool NotNull { get; set; } = false;

    /// <summary>Whether this column has a UNIQUE constraint.</summary>
    public bool Unique { get; set; } = false;

    /// <summary>Whether this column has a plain INDEX.</summary>
    public bool Index { get; set; } = false;

    /// <summary>Default value expression. Optional.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Column comment. Optional.</summary>
    public string? Comment { get; set; }

    /// <summary>Whether to add ON UPDATE CURRENT_TIMESTAMP behavior (via trigger). PostgreSQL note: use triggers for this.</summary>
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

    public CompositeIndexAttribute(string indexName, params string[] columnNames)
    {
        IndexName = indexName;
        ColumnNames = columnNames;
    }
}
