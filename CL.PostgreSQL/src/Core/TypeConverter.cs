using System.Buffers.Binary;
using Npgsql;
using NpgsqlTypes;
using CL.PostgreSQL.Models;

namespace CL.PostgreSQL.Core;

/// <summary>
/// Converts between PostgreSQL/CLR types and generates DDL type strings from <see cref="ColumnAttribute"/> metadata.
/// </summary>
internal static class TypeConverter
{
    /// <summary>
    /// Returns the PostgreSQL DDL type string for the given column attribute (e.g., "VARCHAR(255)", "DECIMAL(10,2)").
    /// When <paramref name="storageType"/> is not <see cref="StorageType.Default"/> it overrides
    /// the <see cref="ColumnAttribute.DataType"/>.
    /// </summary>
    public static string GetPostgreSqlType(ColumnAttribute column, StorageType storageType = StorageType.Default,
        Type? clrType = null)
    {
        if (storageType != StorageType.Default)
            return GetStorageTypeDdl(column, storageType, clrType);

        column = ResolveColumn(column, clrType);

        var size = column.Size > 0 ? column.Size : 255;
        return column.DataType switch
        {
            DataType.SmallInt        => "smallint",
            DataType.Int             => "integer",
            DataType.BigInt          => "bigint",
            DataType.SmallSerial     => "smallserial",
            DataType.Serial          => "serial",
            DataType.BigSerial       => "bigserial",
            DataType.Real            => "real",
            DataType.DoublePrecision => "double precision",
            DataType.Numeric         => $"numeric({column.Precision},{column.Scale})",
            DataType.Money           => "money",

            DataType.Char            => $"character({(column.Size > 0 ? column.Size : 1)})",
            DataType.VarChar         => $"character varying({size})",
            DataType.Text            => "text",

            DataType.Bytea           => "bytea",

            DataType.Date            => "date",
            DataType.Time            => TimePrecision("time without time zone", column),
            DataType.TimeTz          => TimePrecision("time with time zone", column),
            DataType.Timestamp       => TimePrecision("timestamp without time zone", column),
            DataType.TimestampTz     => TimePrecision("timestamp with time zone", column),
            DataType.Interval        => "interval",

            DataType.Bool            => "boolean",
            DataType.Uuid            => "uuid",

            DataType.Json            => "json",
            DataType.Jsonb           => "jsonb",
            DataType.Xml             => "xml",

            DataType.Inet            => "inet",
            DataType.Cidr            => "cidr",
            DataType.MacAddr         => "macaddr",
            DataType.Bit             => column.Size > 0 ? $"bit({column.Size})" : "bit(1)",
            DataType.VarBit          => column.Size > 0 ? $"bit varying({column.Size})" : "bit varying",

            DataType.Int4Range       => "int4range",
            DataType.Int8Range       => "int8range",
            DataType.NumRange        => "numrange",
            DataType.TsRange         => "tsrange",
            DataType.TsTzRange       => "tstzrange",
            DataType.DateRange       => "daterange",

            DataType.SmallIntArray   => "smallint[]",
            DataType.IntArray        => "integer[]",
            DataType.BigIntArray     => "bigint[]",
            DataType.TextArray       => "text[]",
            DataType.VarCharArray    => $"character varying({size})[]",
            DataType.NumericArray    => "numeric[]",
            DataType.UuidArray       => "uuid[]",
            DataType.BoolArray       => "boolean[]",
            DataType.JsonbArray      => "jsonb[]",

            _                        => "text"
        };
    }

    /// <summary>
    /// Renders a fractional-seconds precision suffix when the column declares one.
    /// PostgreSQL accepts 0-6 for the time/timestamp family and spells it
    /// <c>timestamp(3) with time zone</c>, not <c>timestamp with time zone(3)</c>.
    /// </summary>
    private static string TimePrecision(string baseType, ColumnAttribute column)
    {
        if (column.Size is <= 0 or > 6) return baseType;
        var withTz = baseType.IndexOf(" with", StringComparison.Ordinal);
        return withTz < 0
            ? $"{baseType}({column.Size})"
            : $"{baseType[..withTz]}({column.Size}){baseType[withTz..]}";
    }

    /// <summary>
    /// Converts a CLR value to a form suitable for use as an Npgsql parameter value.
    /// Handles enums, nullable types and <see cref="DateTime"/> kind normalisation.
    /// </summary>
    public static object? ToDbValue(object? value)
    {
        if (value is null) return DBNull.Value;

        // Enums -> underlying integer
        var type = value.GetType();
        if (type.IsEnum) return Convert.ChangeType(value, Enum.GetUnderlyingType(type));

        // Npgsql 6+ refuses to write a DateTime whose Kind is Local or Unspecified to a
        // timestamptz column, and a DateTime whose Kind is Utc to a timestamp column.
        // Unspecified is by far the common case (it is what DateTime literals, JSON
        // round-trips and most ORMs produce) and rejecting it would make the library
        // unusable, so treat Unspecified as UTC -- which is what the rest of the stack
        // already assumes, since every timestamp it generates comes from UtcNow.
        if (value is DateTime dt)
        {
            return dt.Kind switch
            {
                DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                DateTimeKind.Local       => dt.ToUniversalTime(),
                _                        => dt
            };
        }

        // Nullable unwrap is handled by the null check above.
        return value;
    }

    /// <summary>
    /// Converts a database value read from an <see cref="NpgsqlDataReader"/> to the target
    /// CLR type. Handles DBNull, enums, and the PostgreSQL-native types Npgsql already
    /// materialises as CLR values (Guid, DateTime, DateTimeOffset, TimeSpan, arrays).
    /// </summary>
    public static object? FromDbValue(object? dbValue, Type targetType)
    {
        if (dbValue is null || dbValue is DBNull) return null;

        // Unwrap nullable
        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Npgsql already returns native CLR types for most PostgreSQL types, so the common
        // path is an exact match that needs no conversion at all.
        if (underlyingType.IsInstanceOfType(dbValue)) return dbValue;

        // timestamptz comes back as a UTC DateTime; widen or narrow as the property asks.
        if (dbValue is DateTime pgDateTime)
        {
            if (underlyingType == typeof(DateTimeOffset))
                return new DateTimeOffset(pgDateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(pgDateTime, DateTimeKind.Utc)
                    : pgDateTime.ToUniversalTime());
            if (underlyingType == typeof(DateOnly)) return DateOnly.FromDateTime(pgDateTime);
            if (underlyingType == typeof(TimeOnly)) return TimeOnly.FromDateTime(pgDateTime);
        }
        if (dbValue is DateTimeOffset pgOffset)
        {
            if (underlyingType == typeof(DateTime)) return pgOffset.UtcDateTime;
            if (underlyingType == typeof(DateOnly)) return DateOnly.FromDateTime(pgOffset.UtcDateTime);
        }
        // interval -> TimeSpan, and time-of-day columns mapped onto TimeOnly.
        if (dbValue is TimeSpan pgInterval && underlyingType == typeof(TimeOnly))
            return TimeOnly.FromTimeSpan(pgInterval);
        if (dbValue is TimeOnly pgTime && underlyingType == typeof(TimeSpan))
            return pgTime.ToTimeSpan();
        if (dbValue is DateOnly pgDate && underlyingType == typeof(DateTime))
            return pgDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        // uuid -> string, and a textual uuid landing on a Guid property.
        if (dbValue is Guid pgUuid && underlyingType == typeof(string)) return pgUuid.ToString();
        if (dbValue is string uuidText && underlyingType == typeof(Guid) && Guid.TryParse(uuidText, out var parsedUuid))
            return parsedUuid;

        // Enum
        if (underlyingType.IsEnum)
        {
            return Enum.ToObject(underlyingType, Convert.ChangeType(dbValue, Enum.GetUnderlyingType(underlyingType)));
        }

        // BINARY column → CLR value WITHOUT [Column(StorageType)] metadata. The
        // projection path (anonymous types / DTOs in ProjectedQuery and paged
        // projections) lands here because projection targets carry no column
        // attributes. A 16-byte value is a BINARY(16) UUID by convention —
        // decode big-endian (RFC 4122), matching ToBinary/GuidToBytes. Without
        // this, every projected Guid from a BINARY(16) column throws
        // InvalidCastException ("Object must implement IConvertible").
        if (dbValue is byte[] raw)
        {
            if (underlyingType == typeof(byte[]))
                return raw;
            if (underlyingType == typeof(Guid) && raw.Length == 16)
                return GuidFromBytes(raw);
            if (underlyingType == typeof(string) && raw.Length == 16)
                return GuidFromBytes(raw).ToString();
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
    /// etc. instead of silently falling back to PostgreSQL defaults.
    /// </summary>
    public static ColumnAttribute InferColumn(Type clrType, int defaultStringSize = 255)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type == typeof(bool))           return new ColumnAttribute { DataType = DataType.Bool };
        // PostgreSQL has no unsigned integers and no 1-byte integer: the narrowest is
        // smallint, so byte/sbyte widen rather than lose range.
        if (type == typeof(byte))           return new ColumnAttribute { DataType = DataType.SmallInt };
        if (type == typeof(sbyte))          return new ColumnAttribute { DataType = DataType.SmallInt };
        if (type == typeof(short))          return new ColumnAttribute { DataType = DataType.SmallInt };
        if (type == typeof(ushort))         return new ColumnAttribute { DataType = DataType.Int };
        if (type == typeof(int))            return new ColumnAttribute { DataType = DataType.Int };
        if (type == typeof(uint))           return new ColumnAttribute { DataType = DataType.BigInt };
        if (type == typeof(long))           return new ColumnAttribute { DataType = DataType.BigInt };
        // ulong overflows bigint at the top of its range; numeric keeps it exact.
        if (type == typeof(ulong))          return new ColumnAttribute { DataType = DataType.Numeric, Precision = 20, Scale = 0 };
        if (type == typeof(float))          return new ColumnAttribute { DataType = DataType.Real };
        if (type == typeof(double))         return new ColumnAttribute { DataType = DataType.DoublePrecision };
        if (type == typeof(decimal))        return new ColumnAttribute { DataType = DataType.Numeric };
        if (type == typeof(string))         return new ColumnAttribute { DataType = DataType.VarChar, Size = defaultStringSize };
        if (type == typeof(char))           return new ColumnAttribute { DataType = DataType.Char, Size = 1 };
        // timestamptz is the safe default: it stores an absolute instant, where
        // "timestamp without time zone" silently discards the offset.
        if (type == typeof(DateTime))       return new ColumnAttribute { DataType = DataType.TimestampTz };
        if (type == typeof(DateTimeOffset)) return new ColumnAttribute { DataType = DataType.TimestampTz };
        if (type == typeof(DateOnly))       return new ColumnAttribute { DataType = DataType.Date };
        if (type == typeof(TimeOnly))       return new ColumnAttribute { DataType = DataType.Time };
        if (type == typeof(TimeSpan))       return new ColumnAttribute { DataType = DataType.Interval };
        // Native uuid, not CHAR(36): 16 bytes instead of 36, and a real type.
        if (type == typeof(Guid))           return new ColumnAttribute { DataType = DataType.Uuid };
        if (type == typeof(byte[]))         return new ColumnAttribute { DataType = DataType.Bytea };
        if (type == typeof(System.Net.IPAddress)) return new ColumnAttribute { DataType = DataType.Inet };
        if (type.IsEnum)                    return new ColumnAttribute { DataType = DataType.Int };
        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            if (element == typeof(short))   return new ColumnAttribute { DataType = DataType.SmallIntArray };
            if (element == typeof(int))     return new ColumnAttribute { DataType = DataType.IntArray };
            if (element == typeof(long))    return new ColumnAttribute { DataType = DataType.BigIntArray };
            if (element == typeof(string))  return new ColumnAttribute { DataType = DataType.TextArray };
            if (element == typeof(decimal)) return new ColumnAttribute { DataType = DataType.NumericArray };
            if (element == typeof(Guid))    return new ColumnAttribute { DataType = DataType.UuidArray };
            if (element == typeof(bool))    return new ColumnAttribute { DataType = DataType.BoolArray };
        }

        return new ColumnAttribute { DataType = DataType.Text };
    }

    /// <summary>
    /// Builds a <see cref="NpgsqlParameter"/> with an explicit <see cref="NpgsqlDbType"/> derived
    /// from the column's declared (or inferred) type, instead of letting the connector guess
    /// from the CLR value. An inferred type can differ from the column's actual type — binding
    /// a string to an integer key, for example — which forces a server-side conversion and
    /// prevents the index on that column from being used.
    /// </summary>
    public static NpgsqlParameter CreateParameter(
        string name, object? value, ColumnAttribute? column = null, Type? clrType = null)
    {
        var parameter = new NpgsqlParameter(name, value ?? DBNull.Value);
        if (column is null) return parameter;

        if (column.StorageType != StorageType.Default)
        {
            // Every binary storage override maps to bytea; PostgreSQL has no fixed-width
            // binary type, so Size is not sent to the server.
            parameter.NpgsqlDbType = NpgsqlDbType.Bytea;
            return parameter;
        }

        var resolved = ResolveColumn(column, clrType ?? value?.GetType());
        if (resolved.DataType == DataType.Unspecified) return parameter;

        parameter.NpgsqlDbType = ToNpgsqlDbType(resolved.DataType);
        if (resolved.Size > 0) parameter.Size = resolved.Size;
        if (resolved.DataType == DataType.Numeric)
        {
            parameter.Precision = checked((byte)resolved.Precision);
            parameter.Scale = checked((byte)resolved.Scale);
        }
        return parameter;
    }

    private static NpgsqlDbType ToNpgsqlDbType(DataType type) => type switch
    {
        DataType.SmallInt or DataType.SmallSerial => NpgsqlDbType.Smallint,
        DataType.Int or DataType.Serial           => NpgsqlDbType.Integer,
        DataType.BigInt or DataType.BigSerial     => NpgsqlDbType.Bigint,
        DataType.Real                             => NpgsqlDbType.Real,
        DataType.DoublePrecision                  => NpgsqlDbType.Double,
        DataType.Numeric                          => NpgsqlDbType.Numeric,
        DataType.Money                            => NpgsqlDbType.Money,

        DataType.Char                             => NpgsqlDbType.Char,
        DataType.VarChar                          => NpgsqlDbType.Varchar,
        DataType.Text                             => NpgsqlDbType.Text,

        DataType.Bytea                            => NpgsqlDbType.Bytea,

        DataType.Date                             => NpgsqlDbType.Date,
        DataType.Time                             => NpgsqlDbType.Time,
        DataType.TimeTz                           => NpgsqlDbType.TimeTz,
        DataType.Timestamp                        => NpgsqlDbType.Timestamp,
        DataType.TimestampTz                      => NpgsqlDbType.TimestampTz,
        DataType.Interval                         => NpgsqlDbType.Interval,

        DataType.Bool                             => NpgsqlDbType.Boolean,
        DataType.Uuid                             => NpgsqlDbType.Uuid,

        DataType.Json                             => NpgsqlDbType.Json,
        DataType.Jsonb                            => NpgsqlDbType.Jsonb,
        DataType.Xml                              => NpgsqlDbType.Xml,

        DataType.Inet                             => NpgsqlDbType.Inet,
        DataType.Cidr                             => NpgsqlDbType.Cidr,
        DataType.MacAddr                          => NpgsqlDbType.MacAddr,
        DataType.Bit                              => NpgsqlDbType.Bit,
        DataType.VarBit                           => NpgsqlDbType.Varbit,

        DataType.Int4Range                        => NpgsqlDbType.IntegerRange,
        DataType.Int8Range                        => NpgsqlDbType.BigIntRange,
        DataType.NumRange                         => NpgsqlDbType.NumericRange,
        DataType.TsRange                          => NpgsqlDbType.TimestampRange,
        DataType.TsTzRange                        => NpgsqlDbType.TimestampTzRange,
        DataType.DateRange                        => NpgsqlDbType.DateRange,

        DataType.SmallIntArray                    => NpgsqlDbType.Array | NpgsqlDbType.Smallint,
        DataType.IntArray                         => NpgsqlDbType.Array | NpgsqlDbType.Integer,
        DataType.BigIntArray                      => NpgsqlDbType.Array | NpgsqlDbType.Bigint,
        DataType.TextArray                        => NpgsqlDbType.Array | NpgsqlDbType.Text,
        DataType.VarCharArray                     => NpgsqlDbType.Array | NpgsqlDbType.Varchar,
        DataType.NumericArray                     => NpgsqlDbType.Array | NpgsqlDbType.Numeric,
        DataType.UuidArray                        => NpgsqlDbType.Array | NpgsqlDbType.Uuid,
        DataType.BoolArray                        => NpgsqlDbType.Array | NpgsqlDbType.Boolean,
        DataType.JsonbArray                       => NpgsqlDbType.Array | NpgsqlDbType.Jsonb,

        _                                         => NpgsqlDbType.Text
    };

    /// <summary>
    /// Resolves a column whose <see cref="ColumnAttribute.DataType"/> is
    /// <see cref="DataType.Unspecified"/> against its CLR property type, filling in the
    /// inferred data type plus any size/unsigned facets the declaration left at their
    /// defaults. A column that declares its type explicitly is returned unchanged.
    /// </summary>
    public static ColumnAttribute ResolveColumn(ColumnAttribute column, Type? clrType)
    {
        if (column.DataType != DataType.Unspecified || clrType is null) return column;

        var inferred = InferColumn(clrType);
        return new ColumnAttribute
        {
            Name       = column.Name,
            DataType   = inferred.DataType,
            Size       = column.Size > 0 ? column.Size : inferred.Size,
            Precision  = column.Precision,
            Scale      = column.Scale,
            Unsigned   = column.Unsigned || inferred.Unsigned,
            Primary    = column.Primary,
            AutoIncrement = column.AutoIncrement,
            NotNull    = column.NotNull,
            Unique     = column.Unique,
            Index      = column.Index,
            DefaultValue = column.DefaultValue,
            Comment    = column.Comment,
            Charset    = column.Charset,
            StorageType = column.StorageType,
            OnUpdateCurrentTimestamp = column.OnUpdateCurrentTimestamp,
            PreviousName = column.PreviousName
        };
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
        string s when Guid.TryParse(s, out var g) => GuidToBytes(g),
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
        // PostgreSQL has a single variable-length binary type, so every binary storage
        // override renders as bytea. Size stays advisory: the value converter uses it to
        // decide how many bytes a fixed-width CLR value occupies.
        _ = clrType;
        return storageType switch
        {
            StorageType.Binary or StorageType.VarBinary => "bytea",
            _                                           => GetPostgreSqlType(column)
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
