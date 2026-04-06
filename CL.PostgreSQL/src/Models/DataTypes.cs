namespace CL.PostgreSQL.Models;

/// <summary>PostgreSQL column data types.</summary>
public enum DataType
{
    // Numeric
    SmallInt,
    Int,
    BigInt,
    Real,
    DoublePrecision,
    Numeric,

    // Date/Time
    Timestamp,
    Date,
    Time,
    TimeTz,
    TimestampTz,

    // String
    Char,
    VarChar,
    Text,

    // JSON
    Json,
    Jsonb,

    // Other
    Uuid,
    Bool,
    Bytea,

    // Arrays
    IntArray,
    BigIntArray,
    TextArray,
    NumericArray
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
