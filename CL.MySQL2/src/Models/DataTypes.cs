namespace CL.MySQL2.Models;

/// <summary>MySQL column data types.</summary>
public enum DataType
{
    // Numeric
    TinyInt,
    SmallInt,
    MediumInt,
    Int,
    BigInt,
    Decimal,
    Float,
    Double,
    Bit,

    // String
    Char,
    VarChar,
    TinyText,
    Text,
    MediumText,
    LongText,
    Enum,
    Set,

    // Binary
    Binary,
    VarBinary,
    TinyBlob,
    Blob,
    MediumBlob,
    LongBlob,

    // Date/Time
    Date,
    Time,
    DateTime,
    Timestamp,
    Year,

    // Spatial / JSON
    Json,
    Geometry
}

/// <summary>MySQL storage engine options.</summary>
public enum TableEngine
{
    InnoDB,
    MyISAM,
    Memory,
    Archive,
    CSV,
    NDB
}

/// <summary>MySQL character set options.</summary>
public enum Charset
{
    Utf8mb4,
    Utf8,
    Latin1,
    Ascii,
    Binary
}

/// <summary>Sort order for query results.</summary>
public enum SortOrder
{
    Asc,
    Desc
}

/// <summary>Type of DML operation performed on a record.</summary>
public enum OperationType
{
    Insert,
    Update,
    Delete,
    Select
}
