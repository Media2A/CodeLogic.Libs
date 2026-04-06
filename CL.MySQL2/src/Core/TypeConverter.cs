using MySqlConnector;
using CL.MySQL2.Models;

namespace CL.MySQL2.Core;

/// <summary>
/// Converts between MySQL/CLR types and generates DDL type strings from <see cref="ColumnAttribute"/> metadata.
/// </summary>
internal static class TypeConverter
{
    /// <summary>
    /// Returns the MySQL DDL type string for the given column attribute (e.g., "VARCHAR(255)", "DECIMAL(10,2)").
    /// </summary>
    public static string GetMySqlType(ColumnAttribute column)
    {
        return column.DataType switch
        {
            DataType.TinyInt    => column.Unsigned ? "TINYINT UNSIGNED" : "TINYINT",
            DataType.SmallInt   => column.Unsigned ? "SMALLINT UNSIGNED" : "SMALLINT",
            DataType.MediumInt  => column.Unsigned ? "MEDIUMINT UNSIGNED" : "MEDIUMINT",
            DataType.Int        => column.Unsigned ? "INT UNSIGNED" : "INT",
            DataType.BigInt     => column.Unsigned ? "BIGINT UNSIGNED" : "BIGINT",
            DataType.Decimal    => $"DECIMAL({column.Precision},{column.Scale})",
            DataType.Float      => "FLOAT",
            DataType.Double     => "DOUBLE",
            DataType.Bit        => column.Size > 0 ? $"BIT({column.Size})" : "BIT",

            DataType.Char       => column.Size > 0 ? $"CHAR({column.Size})" : "CHAR(1)",
            DataType.VarChar    => column.Size > 0 ? $"VARCHAR({column.Size})" : "VARCHAR(255)",
            DataType.TinyText   => "TINYTEXT",
            DataType.Text       => "TEXT",
            DataType.MediumText => "MEDIUMTEXT",
            DataType.LongText   => "LONGTEXT",
            DataType.Enum       => "ENUM",
            DataType.Set        => "SET",

            DataType.Binary     => column.Size > 0 ? $"BINARY({column.Size})" : "BINARY(1)",
            DataType.VarBinary  => column.Size > 0 ? $"VARBINARY({column.Size})" : "VARBINARY(255)",
            DataType.TinyBlob   => "TINYBLOB",
            DataType.Blob       => "BLOB",
            DataType.MediumBlob => "MEDIUMBLOB",
            DataType.LongBlob   => "LONGBLOB",

            DataType.Date       => "DATE",
            DataType.Time       => "TIME",
            DataType.DateTime   => "DATETIME",
            DataType.Timestamp  => "TIMESTAMP",
            DataType.Year       => "YEAR",

            DataType.Json       => "JSON",
            DataType.Geometry   => "GEOMETRY",

            _                   => "TEXT"
        };
    }

    /// <summary>
    /// Converts a CLR value to a form suitable for use as a MySqlConnector parameter value.
    /// Handles enums, nullable types, and MySqlDateTime.
    /// </summary>
    public static object? ToDbValue(object? value)
    {
        if (value is null) return DBNull.Value;

        // Enums → underlying integer
        var type = value.GetType();
        if (type.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(type));

        // Nullable unwrap is handled by the null check above.
        return value;
    }

    /// <summary>
    /// Converts a database value read from a MySqlDataReader to the target CLR type.
    /// Handles DBNull, enums, and MySqlDateTime.
    /// </summary>
    public static object? FromDbValue(object? dbValue, Type targetType)
    {
        if (dbValue is null || dbValue is DBNull) return null;

        // Unwrap nullable
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // MySqlDateTime → DateTime
        if (dbValue is MySqlDateTime mdt)
        {
            if (!mdt.IsValidDateTime) return null;
            var dt = mdt.GetDateTime();
            return underlyingType == typeof(DateTimeOffset) ? (object)new DateTimeOffset(dt) : dt;
        }

        // Enum
        if (underlyingType.IsEnum)
        {
            return Enum.ToObject(underlyingType, Convert.ChangeType(dbValue, Enum.GetUnderlyingType(underlyingType)));
        }

        // Standard conversions
        try
        {
            return Convert.ChangeType(dbValue, underlyingType);
        }
        catch
        {
            return dbValue;
        }
    }

    /// <summary>
    /// Infers the best-matching <see cref="DataType"/> for a CLR type.
    /// Used when reflecting entity properties without explicit <see cref="ColumnAttribute"/>.
    /// </summary>
    public static DataType InferDataType(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(bool))        return DataType.TinyInt;
        if (type == typeof(byte))        return DataType.TinyInt;
        if (type == typeof(sbyte))       return DataType.TinyInt;
        if (type == typeof(short))       return DataType.SmallInt;
        if (type == typeof(ushort))      return DataType.SmallInt;
        if (type == typeof(int))         return DataType.Int;
        if (type == typeof(uint))        return DataType.Int;
        if (type == typeof(long))        return DataType.BigInt;
        if (type == typeof(ulong))       return DataType.BigInt;
        if (type == typeof(float))       return DataType.Float;
        if (type == typeof(double))      return DataType.Double;
        if (type == typeof(decimal))     return DataType.Decimal;
        if (type == typeof(string))      return DataType.VarChar;
        if (type == typeof(char))        return DataType.Char;
        if (type == typeof(DateTime))    return DataType.DateTime;
        if (type == typeof(DateTimeOffset)) return DataType.DateTime;
        if (type == typeof(TimeSpan))    return DataType.Time;
        if (type == typeof(Guid))        return DataType.Char;
        if (type == typeof(byte[]))      return DataType.Blob;
        if (type.IsEnum)                 return DataType.Int;

        return DataType.Text;
    }
}
