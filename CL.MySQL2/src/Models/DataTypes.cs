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

/// <summary>
/// Optional physical-storage override for a column. When set to anything other than
/// <see cref="StorageType.Default"/>, the storage type takes precedence over
/// <see cref="DataType"/> for DDL generation. For certain CLR types automatic value
/// conversion is applied (e.g. a <see cref="System.Guid"/> property with
/// <see cref="StorageType.Binary"/> is stored as <c>BINARY(16)</c> with big-endian byte conversion).
/// </summary>
public enum StorageType
{
    /// <summary>No override — use <see cref="DataType"/> as-is.</summary>
    Default = 0,

    /// <summary>Fixed-length binary. Uses <see cref="ColumnAttribute.Size"/> for explicit length; for <see cref="System.Guid"/> auto-sizes to 16.</summary>
    Binary,

    /// <summary>Variable-length binary. Uses <see cref="ColumnAttribute.Size"/> (default 255).</summary>
    VarBinary,

    /// <summary>Tiny binary large object (max 255 bytes).</summary>
    TinyBlob,

    /// <summary>Binary large object (max 65 535 bytes).</summary>
    Blob,

    /// <summary>Medium binary large object (max 16 MB).</summary>
    MediumBlob,

    /// <summary>Long binary large object (max 4 GB).</summary>
    LongBlob
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
