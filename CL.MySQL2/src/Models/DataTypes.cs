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

/// <summary>
/// Controls how aggressively the table sync service reconciles the live database
/// schema with entity definitions. Higher levels allow more destructive operations.
/// </summary>
public enum SchemaSyncLevel
{
    /// <summary>Skip sync entirely. No ALTER/CREATE statements are issued.</summary>
    None = 0,

    /// <summary>
    /// Default. Adds missing columns, indexes and foreign keys, and modifies existing
    /// columns to match the model (grow VARCHAR, toggle NULL, update default, etc.).
    /// Never drops anything.
    /// </summary>
    Safe = 1,

    /// <summary>
    /// Safe + drops indexes and foreign keys that no longer exist in the model.
    /// No column data is lost.
    /// </summary>
    Additive = 2,

    /// <summary>
    /// Additive + drops columns that no longer exist in the model, and allows full
    /// DROP TABLE rebuild when used together with backups. Intended for development only.
    /// </summary>
    Full = 3
}
