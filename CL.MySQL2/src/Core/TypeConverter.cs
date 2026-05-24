using System.Buffers.Binary;
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
    /// When <paramref name="storageType"/> is not <see cref="StorageType.Default"/> it overrides
    /// the <see cref="ColumnAttribute.DataType"/>.
    /// </summary>
    public static string GetMySqlType(ColumnAttribute column, StorageType storageType = StorageType.Default,
        Type? clrType = null)
    {
        if (storageType != StorageType.Default)
            return GetStorageTypeDdl(column, storageType, clrType);

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

    // ── StorageType support ──────────────────────────────────────────────────

    /// <summary>
    /// Converts a CLR value to a DB parameter value, applying <see cref="StorageType"/>-specific
    /// conversions (e.g. <see cref="System.Guid"/> → big-endian <c>byte[16]</c> when stored as Binary).
    /// </summary>
    public static object? ToDbValue(object? value, StorageType storageType)
    {
        if (storageType != StorageType.Default)
        {
            var bin = ToBinary(value);
            if (bin is not null) return bin;
        }

        return ToDbValue(value);
    }

    /// <summary>
    /// Converts a database value to the target CLR type, applying <see cref="StorageType"/>-specific
    /// conversions (e.g. <c>byte[16]</c> → <see cref="System.Guid"/> when stored as Binary).
    /// </summary>
    public static object? FromDbValue(object? dbValue, Type targetType, StorageType storageType)
    {
        if (storageType != StorageType.Default && dbValue is byte[] bytes)
        {
            var result = FromBinary(bytes, targetType);
            if (result is not null) return result;
        }

        return FromDbValue(dbValue, targetType);
    }

    // ── Binary serialization (big-endian for correct sort order) ─────────────

    private static byte[]? ToBinary(object? value) => value switch
    {
        null                => null,
        byte[] b            => b,
        Guid guid           => GuidToBytes(guid),
        string s            => System.Text.Encoding.UTF8.GetBytes(s),
        bool b              => [b ? (byte)1 : (byte)0],
        byte v              => [v],
        sbyte v             => [(byte)v],
        short v             => BinaryToBytes(2, buf => BinaryPrimitives.WriteInt16BigEndian(buf, v)),
        ushort v            => BinaryToBytes(2, buf => BinaryPrimitives.WriteUInt16BigEndian(buf, v)),
        int v               => BinaryToBytes(4, buf => BinaryPrimitives.WriteInt32BigEndian(buf, v)),
        uint v              => BinaryToBytes(4, buf => BinaryPrimitives.WriteUInt32BigEndian(buf, v)),
        long v              => BinaryToBytes(8, buf => BinaryPrimitives.WriteInt64BigEndian(buf, v)),
        ulong v             => BinaryToBytes(8, buf => BinaryPrimitives.WriteUInt64BigEndian(buf, v)),
        float v             => BinaryToBytes(4, buf => BinaryPrimitives.WriteSingleBigEndian(buf, v)),
        double v            => BinaryToBytes(8, buf => BinaryPrimitives.WriteDoubleBigEndian(buf, v)),
        decimal v           => DecimalToBytes(v),
        DateTime v          => BinaryToBytes(8, buf => BinaryPrimitives.WriteInt64BigEndian(buf, v.Ticks)),
        DateTimeOffset v    => BinaryToBytes(8, buf => BinaryPrimitives.WriteInt64BigEndian(buf, v.UtcTicks)),
        _                   => null
    };

    private static object? FromBinary(byte[] bytes, Type targetType)
    {
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (t == typeof(byte[]))        return bytes;
        if (t == typeof(Guid))          return bytes.Length == 16 ? GuidFromBytes(bytes) : null;
        if (t == typeof(string))        return System.Text.Encoding.UTF8.GetString(bytes);
        if (t == typeof(bool))          return bytes.Length >= 1 && bytes[0] != 0;
        if (t == typeof(byte))          return bytes.Length >= 1 ? bytes[0] : null;
        if (t == typeof(sbyte))         return bytes.Length >= 1 ? (sbyte)bytes[0] : null;
        if (t == typeof(short))         return bytes.Length >= 2 ? BinaryPrimitives.ReadInt16BigEndian(bytes) : null;
        if (t == typeof(ushort))        return bytes.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : null;
        if (t == typeof(int))           return bytes.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(bytes) : null;
        if (t == typeof(uint))          return bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : null;
        if (t == typeof(long))          return bytes.Length >= 8 ? BinaryPrimitives.ReadInt64BigEndian(bytes) : null;
        if (t == typeof(ulong))         return bytes.Length >= 8 ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : null;
        if (t == typeof(float))         return bytes.Length >= 4 ? BinaryPrimitives.ReadSingleBigEndian(bytes) : null;
        if (t == typeof(double))        return bytes.Length >= 8 ? BinaryPrimitives.ReadDoubleBigEndian(bytes) : null;
        if (t == typeof(decimal))       return bytes.Length >= 16 ? BytesToDecimal(bytes) : null;
        if (t == typeof(DateTime))      return bytes.Length >= 8 ? new DateTime(BinaryPrimitives.ReadInt64BigEndian(bytes)) : null;
        if (t == typeof(DateTimeOffset)) return bytes.Length >= 8 ? new DateTimeOffset(BinaryPrimitives.ReadInt64BigEndian(bytes), TimeSpan.Zero) : null;

        return null;
    }

    internal static byte[] GuidToBytes(Guid guid)
    {
        var bytes = new byte[16];
        guid.TryWriteBytes(bytes, bigEndian: true, out _);
        return bytes;
    }

    internal static Guid GuidFromBytes(byte[] bytes) => new(bytes, bigEndian: true);

    private static byte[] BinaryToBytes(int size, Action<Span<byte>> writer)
    {
        var bytes = new byte[size];
        writer(bytes);
        return bytes;
    }

    private static byte[] DecimalToBytes(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var bytes = new byte[16];
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(i * 4), bits[i]);
        return bytes;
    }

    private static decimal BytesToDecimal(byte[] bytes)
    {
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++)
            bits[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(i * 4));
        return new decimal(bits);
    }

    private static string GetStorageTypeDdl(ColumnAttribute column, StorageType storageType, Type? clrType)
    {
        return storageType switch
        {
            StorageType.Binary     => $"BINARY({InferBinarySize(column, clrType)})",
            StorageType.VarBinary  => column.Size > 0 ? $"VARBINARY({column.Size})" : "VARBINARY(255)",
            StorageType.TinyBlob   => "TINYBLOB",
            StorageType.Blob       => "BLOB",
            StorageType.MediumBlob => "MEDIUMBLOB",
            StorageType.LongBlob   => "LONGBLOB",
            _                      => GetMySqlType(column)
        };
    }

    private static int InferBinarySize(ColumnAttribute column, Type? clrType)
    {
        if (column.Size > 0) return column.Size;

        var t = clrType is not null ? (Nullable.GetUnderlyingType(clrType) ?? clrType) : null;
        return t switch
        {
            not null when t == typeof(Guid)           => 16,
            not null when t == typeof(long)
                       || t == typeof(ulong)
                       || t == typeof(double)
                       || t == typeof(DateTime)
                       || t == typeof(DateTimeOffset) => 8,
            not null when t == typeof(int)
                       || t == typeof(uint)
                       || t == typeof(float)          => 4,
            not null when t == typeof(short)
                       || t == typeof(ushort)          => 2,
            not null when t == typeof(bool)
                       || t == typeof(byte)
                       || t == typeof(sbyte)           => 1,
            not null when t == typeof(decimal)         => 16,
            _                                          => 1
        };
    }
}
