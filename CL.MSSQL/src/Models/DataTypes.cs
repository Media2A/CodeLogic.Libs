namespace CL.MSSQL.Models;

/// <summary>Native SQL Server column data types.</summary>
public enum DataType
{
    Unspecified = 0,
    TinyInt, SmallInt, Int, BigInt, Decimal, Numeric, Money, SmallMoney, Real, Float, Bit,
    Char, VarChar, VarCharMax, NChar, NVarChar, NVarCharMax, Text, NText,
    Binary, VarBinary, VarBinaryMax,
    Date, Time, SmallDateTime, DateTime, DateTime2, DateTimeOffset,
    UniqueIdentifier, Xml, Json, Geometry, Geography, RowVersion
}

/// <summary>Optional binary physical-storage override.</summary>
public enum StorageType
{
    Default = 0,
    Binary,
    VarBinary,
    VarBinaryMax
}

public enum SortOrder { Asc, Desc }

public enum SchemaSyncLevel { None = 0, Safe = 1, Additive = 2, Full = 3 }

public enum SyncMode { Developer = 0, Production = 1, Migration = 2 }
