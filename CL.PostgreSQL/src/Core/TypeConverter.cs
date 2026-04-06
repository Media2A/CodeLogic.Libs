using CL.PostgreSQL.Models;

namespace CL.PostgreSQL.Core;

internal static class TypeConverter
{
    /// <summary>
    /// Returns the PostgreSQL DDL type string for the given DataType.
    /// </summary>
    public static string GetPostgreSqlType(DataType dataType, int size, int precision, int scale)
    {
        return dataType switch
        {
            DataType.SmallInt       => "SMALLINT",
            DataType.Int            => "INTEGER",
            DataType.BigInt         => "BIGINT",
            DataType.Real           => "REAL",
            DataType.DoublePrecision => "DOUBLE PRECISION",
            DataType.Numeric        => $"NUMERIC({precision},{scale})",

            DataType.Timestamp      => "TIMESTAMP",
            DataType.TimestampTz    => "TIMESTAMPTZ",
            DataType.Date           => "DATE",
            DataType.Time           => "TIME",
            DataType.TimeTz         => "TIMETZ",

            DataType.Char           => size > 0 ? $"CHAR({size})" : "CHAR(1)",
            DataType.VarChar        => size > 0 ? $"VARCHAR({size})" : "VARCHAR(255)",
            DataType.Text           => "TEXT",

            DataType.Json           => "JSON",
            DataType.Jsonb          => "JSONB",

            DataType.Uuid           => "UUID",
            DataType.Bool           => "BOOLEAN",
            DataType.Bytea          => "BYTEA",

            DataType.IntArray       => "INTEGER[]",
            DataType.BigIntArray    => "BIGINT[]",
            DataType.TextArray      => "TEXT[]",
            DataType.NumericArray   => "NUMERIC[]",

            _                       => "TEXT"
        };
    }

    /// <summary>
    /// Converts a CLR value to a form suitable for use as an Npgsql parameter value.
    /// </summary>
    public static object? ToDbValue(object? value)
    {
        if (value is null) return DBNull.Value;

        var type = value.GetType();
        if (type.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(type));

        return value;
    }

    /// <summary>
    /// Converts a database value read from an NpgsqlDataReader to the target CLR type.
    /// </summary>
    public static object? FromDbValue(object? dbValue, Type targetType)
    {
        if (dbValue is null || dbValue is DBNull) return null;

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlyingType.IsEnum)
        {
            return Enum.ToObject(underlyingType,
                Convert.ChangeType(dbValue, Enum.GetUnderlyingType(underlyingType)));
        }

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
    /// Infers the best-matching DataType for a CLR type.
    /// </summary>
    public static DataType InferDataType(Type clrType)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(bool))           return DataType.Bool;
        if (type == typeof(byte))           return DataType.SmallInt;
        if (type == typeof(sbyte))          return DataType.SmallInt;
        if (type == typeof(short))          return DataType.SmallInt;
        if (type == typeof(ushort))         return DataType.SmallInt;
        if (type == typeof(int))            return DataType.Int;
        if (type == typeof(uint))           return DataType.Int;
        if (type == typeof(long))           return DataType.BigInt;
        if (type == typeof(ulong))          return DataType.BigInt;
        if (type == typeof(float))          return DataType.Real;
        if (type == typeof(double))         return DataType.DoublePrecision;
        if (type == typeof(decimal))        return DataType.Numeric;
        if (type == typeof(string))         return DataType.Text;
        if (type == typeof(char))           return DataType.Char;
        if (type == typeof(DateTime))       return DataType.TimestampTz;
        if (type == typeof(DateTimeOffset)) return DataType.TimestampTz;
        if (type == typeof(TimeSpan))       return DataType.Time;
        if (type == typeof(Guid))           return DataType.Uuid;
        if (type == typeof(byte[]))         return DataType.Bytea;
        if (type.IsEnum)                    return DataType.Int;

        return DataType.Text;
    }
}
