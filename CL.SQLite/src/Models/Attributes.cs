namespace CL.SQLite.Models;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SQLiteTableAttribute : Attribute
{
    public string TableName { get; }
    public SQLiteTableAttribute(string tableName) => TableName = tableName;
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class SQLiteIndexAttribute : Attribute
{
    public string[] Columns { get; }
    public bool IsUnique { get; set; } = false;
    public string? Name { get; set; }

    public SQLiteIndexAttribute(params string[] columns) => Columns = columns;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class SQLiteColumnAttribute : Attribute
{
    public bool IsPrimaryKey { get; set; } = false;
    public bool IsIndexed { get; set; } = false;
    public bool IsUnique { get; set; } = false;
    public bool IsAutoIncrement { get; set; } = false;
    public string? ColumnName { get; set; }
    public SQLiteDataType DataType { get; set; }
    public int Size { get; set; } = 0;
    public bool IsNotNull { get; set; } = false;
    public string? DefaultValue { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class SQLiteForeignKeyAttribute : Attribute
{
    public string ReferencedTable { get; }
    public string ReferencedColumn { get; }
    public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.NoAction;
    public ForeignKeyAction OnUpdate { get; set; } = ForeignKeyAction.NoAction;

    public SQLiteForeignKeyAttribute(string referencedTable, string referencedColumn)
    {
        ReferencedTable = referencedTable;
        ReferencedColumn = referencedColumn;
    }
}

public enum ForeignKeyAction
{
    NoAction,
    Restrict,
    SetNull,
    SetDefault,
    Cascade
}
