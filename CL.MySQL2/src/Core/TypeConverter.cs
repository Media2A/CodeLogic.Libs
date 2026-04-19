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
        catch (Exception ex)
        {
            // Hard fail: silent fallthrough to the raw DB value is a data-corruption trap
            // (prior behaviour). Surface a helpful message instead so it's obvious which
            // column and which conversion went wrong.
            throw new InvalidCastException(
                $"Cannot convert DB value '{dbValue}' (type {dbValue.GetType().Name}) " +
                $"to CLR type '{underlyingType.Name}'.", ex);
        }
    }

    /// <summary>
    /// Infers a <see cref="ColumnAttribute"/> — not just a DataType — for a CLR type
    /// lacking an explicit one. This lets us pick the right size for Guid, varchar,
    /// etc. instead of silently falling back to MySQL defaults.
    /// </summary>
    public static ColumnAttribute InferColumn(Type clrType, int defaultStringSize = 255)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(bool))        return new ColumnAttribute { DataType = DataType.TinyInt };
        if (type == typeof(byte))        return new ColumnAttribute { DataType = DataType.TinyInt, Unsigned = true };
        if (type == typeof(sbyte))       return new ColumnAttribute { DataType = DataType.TinyInt };
        if (type == typeof(short))       return new ColumnAttribute { DataType = DataType.SmallInt };
        if (type == typeof(ushort))      return new ColumnAttribute { DataType = DataType.SmallInt, Unsigned = true };
        if (type == typeof(int))         return new ColumnAttribute { DataType = DataType.Int };
        if (type == typeof(uint))        return new ColumnAttribute { DataType = DataType.Int, Unsigned = true };
        if (type == typeof(long))        return new ColumnAttribute { DataType = DataType.BigInt };
        if (type == typeof(ulong))       return new ColumnAttribute { DataType = DataType.BigInt, Unsigned = true };
        if (type == typeof(float))       return new ColumnAttribute { DataType = DataType.Float };
        if (type == typeof(double))      return new ColumnAttribute { DataType = DataType.Double };
        if (type == typeof(decimal))     return new ColumnAttribute { DataType = DataType.Decimal };
        if (type == typeof(string))      return new ColumnAttribute { DataType = DataType.VarChar, Size = defaultStringSize };
        if (type == typeof(char))        return new ColumnAttribute { DataType = DataType.Char, Size = 1 };
        if (type == typeof(DateTime))    return new ColumnAttribute { DataType = DataType.DateTime };
        if (type == typeof(DateTimeOffset)) return new ColumnAttribute { DataType = DataType.DateTime };
        if (type == typeof(TimeSpan))    return new ColumnAttribute { DataType = DataType.Time };
        if (type == typeof(Guid))        return new ColumnAttribute { DataType = DataType.Char, Size = 36 };
        if (type == typeof(byte[]))     return new ColumnAttribute { DataType = DataType.Blob };
        if (type.IsEnum)                 return new ColumnAttribute { DataType = DataType.Int };

        return new ColumnAttribute { DataType = DataType.Text };
    }

    /// <summary>
    /// Legacy helper — preserved for SchemaAnalyzer call sites that only need the DataType.
    /// Prefer <see cref="InferColumn"/> which carries size/unsigned metadata.
    /// </summary>
    public static DataType InferDataType(Type clrType) => InferColumn(clrType).DataType;
}
