using System.Data;
using System.Globalization;
using System.Text.Json;
using CL.MSSQL.Models;
using Microsoft.Data.SqlClient;

namespace CL.MSSQL.Core;

internal static class TypeConverter
{
    public static string GetSqlServerType(ColumnAttribute column, StorageType storageType = StorageType.Default,
        Type? propertyType = null)
    {
        if (storageType != StorageType.Default)
            return storageType switch
            {
                StorageType.Binary => $"binary({(column.Size > 0 ? column.Size : propertyType == typeof(Guid) ? 16 : 1)})",
                StorageType.VarBinary => $"varbinary({(column.Size > 0 ? column.Size : 255)})",
                StorageType.VarBinaryMax => "varbinary(max)",
                _ => throw new ArgumentOutOfRangeException(nameof(storageType))
            };

        var size = column.Size > 0 ? column.Size : 255;
        var dataType = column.DataType == DataType.Unspecified && propertyType is not null
            ? InferColumn(propertyType).DataType : column.DataType;
        return dataType switch
        {
            DataType.TinyInt => "tinyint", DataType.SmallInt => "smallint", DataType.Int => "int", DataType.BigInt => "bigint",
            DataType.Decimal => $"decimal({column.Precision},{column.Scale})", DataType.Numeric => $"numeric({column.Precision},{column.Scale})",
            DataType.Money => "money", DataType.SmallMoney => "smallmoney", DataType.Real => "real", DataType.Float => "float", DataType.Bit => "bit",
            DataType.Char => $"char({size})", DataType.VarChar => $"varchar({size})", DataType.VarCharMax or DataType.Text => "varchar(max)",
            DataType.NChar => $"nchar({size})", DataType.NVarChar => $"nvarchar({size})", DataType.NVarCharMax or DataType.NText or DataType.Json => "nvarchar(max)",
            DataType.Binary => $"binary({size})", DataType.VarBinary => $"varbinary({size})", DataType.VarBinaryMax => "varbinary(max)",
            DataType.Date => "date", DataType.Time => PrecisionSuffix("time", column), DataType.SmallDateTime => "smalldatetime",
            DataType.DateTime => "datetime", DataType.DateTime2 => PrecisionSuffix("datetime2", column), DataType.DateTimeOffset => PrecisionSuffix("datetimeoffset", column),
            DataType.UniqueIdentifier => "uniqueidentifier", DataType.Xml => "xml", DataType.Geometry => "geometry", DataType.Geography => "geography",
            DataType.RowVersion => "rowversion",
            _ => throw new ArgumentOutOfRangeException(nameof(column.DataType))
        };
    }

    public static object? ToDbValue(object? value, StorageType storageType = StorageType.Default)
    {
        if (value is null) return null;
        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (type.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
        if (storageType != StorageType.Default && value is Guid guid) return guid.ToByteArray();
        return value;
    }

    public static object? FromDbValue(object? value, Type targetType, StorageType storageType = StorageType.Default)
    {
        if (value is null or DBNull) return null;
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (storageType != StorageType.Default && type == typeof(Guid) && value is byte[] bytes) return new Guid(bytes);
        if (type.IsEnum) return Enum.ToObject(type, Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)!);
        if (type == typeof(Guid)) return value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        if (type == typeof(DateOnly)) return DateOnly.FromDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture));
        if (type == typeof(TimeOnly)) return value is TimeSpan ts ? TimeOnly.FromTimeSpan(ts) : TimeOnly.FromDateTime(Convert.ToDateTime(value, CultureInfo.InvariantCulture));
        if (type == typeof(byte[]) && value is byte[] data) return data;
        return type.IsInstanceOfType(value) ? value : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    public static ColumnAttribute InferColumn(Type propertyType, int defaultStringSize = 255)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (type == typeof(string)) return new() { DataType = DataType.NVarChar, Size = defaultStringSize };
        if (type == typeof(bool)) return new() { DataType = DataType.Bit };
        if (type == typeof(byte) || type == typeof(sbyte)) return new() { DataType = DataType.TinyInt };
        if (type == typeof(short) || type == typeof(ushort)) return new() { DataType = DataType.SmallInt };
        if (type == typeof(int) || type == typeof(uint) || type.IsEnum) return new() { DataType = DataType.Int };
        if (type == typeof(long) || type == typeof(ulong)) return new() { DataType = DataType.BigInt };
        if (type == typeof(decimal)) return new() { DataType = DataType.Decimal, Precision = 18, Scale = 2 };
        if (type == typeof(float)) return new() { DataType = DataType.Real };
        if (type == typeof(double)) return new() { DataType = DataType.Float };
        if (type == typeof(DateTime)) return new() { DataType = DataType.DateTime2, Precision = 7 };
        if (type == typeof(DateTimeOffset)) return new() { DataType = DataType.DateTimeOffset, Precision = 7 };
        if (type == typeof(DateOnly)) return new() { DataType = DataType.Date };
        if (type == typeof(TimeOnly) || type == typeof(TimeSpan)) return new() { DataType = DataType.Time, Precision = 7 };
        if (type == typeof(Guid)) return new() { DataType = DataType.UniqueIdentifier };
        if (type == typeof(byte[])) return new() { DataType = DataType.VarBinaryMax };
        throw new NotSupportedException($"No SQL Server mapping exists for CLR type '{type}'.");
    }

    public static SqlParameter CreateParameter(string name, object? value, ColumnAttribute? column = null)
    {
        var parameter = new SqlParameter(name, value ?? DBNull.Value);
        if (column is null) return parameter;
        var dataType = column.DataType == DataType.Unspecified && value is not null
            ? InferColumn(value.GetType()).DataType : column.DataType;
        if (dataType == DataType.Unspecified) return parameter;
        parameter.SqlDbType = ToSqlDbType(dataType, column.StorageType);
        if (column.Size > 0) parameter.Size = column.Size;
        else if (dataType is DataType.NVarCharMax or DataType.VarCharMax or DataType.VarBinaryMax or DataType.Json) parameter.Size = -1;
        if (dataType is DataType.Decimal or DataType.Numeric)
        {
            parameter.Precision = checked((byte)column.Precision);
            parameter.Scale = checked((byte)column.Scale);
        }
        if (dataType is DataType.Geometry or DataType.Geography)
            parameter.UdtTypeName = dataType == DataType.Geometry ? "geometry" : "geography";
        return parameter;
    }

    private static SqlDbType ToSqlDbType(DataType type, StorageType storage) => storage switch
    {
        StorageType.Binary => SqlDbType.Binary, StorageType.VarBinary or StorageType.VarBinaryMax => SqlDbType.VarBinary,
        _ => type switch
        {
            DataType.TinyInt => SqlDbType.TinyInt, DataType.SmallInt => SqlDbType.SmallInt, DataType.Int => SqlDbType.Int, DataType.BigInt => SqlDbType.BigInt,
            DataType.Decimal => SqlDbType.Decimal, DataType.Numeric => SqlDbType.Decimal, DataType.Money => SqlDbType.Money, DataType.SmallMoney => SqlDbType.SmallMoney,
            DataType.Real => SqlDbType.Real, DataType.Float => SqlDbType.Float, DataType.Bit => SqlDbType.Bit,
            DataType.Char => SqlDbType.Char, DataType.VarChar or DataType.VarCharMax or DataType.Text => SqlDbType.VarChar,
            DataType.NChar => SqlDbType.NChar, DataType.NVarChar or DataType.NVarCharMax or DataType.NText or DataType.Json => SqlDbType.NVarChar,
            DataType.Binary => SqlDbType.Binary, DataType.VarBinary or DataType.VarBinaryMax or DataType.RowVersion => SqlDbType.VarBinary,
            DataType.Date => SqlDbType.Date, DataType.Time => SqlDbType.Time, DataType.SmallDateTime => SqlDbType.SmallDateTime, DataType.DateTime => SqlDbType.DateTime,
            DataType.DateTime2 => SqlDbType.DateTime2, DataType.DateTimeOffset => SqlDbType.DateTimeOffset, DataType.UniqueIdentifier => SqlDbType.UniqueIdentifier,
            DataType.Xml => SqlDbType.Xml, _ => SqlDbType.Udt
        }
    };

    private static string PrecisionSuffix(string name, ColumnAttribute column) => column.Precision is >= 0 and <= 7 ? $"{name}({column.Precision})" : name;
}
