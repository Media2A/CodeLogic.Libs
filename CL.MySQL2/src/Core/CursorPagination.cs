using System.Text.Json;
using System.Text.Json.Serialization;

namespace CL.MySQL2.Core;

internal sealed record CursorOrder(ColumnMetadata Column, bool Descending);

internal sealed class CursorPagingException : Exception
{
    public string ErrorCode { get; }

    public CursorPagingException(string message, string errorCode = "mysql.cursor_paging_invalid", Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }
}

internal static class CursorPagination
{
    public static IReadOnlyList<CursorOrder> GetEffectiveOrder<T>(IReadOnlyList<CursorOrder> explicitOrders)
        where T : class
    {
        if (explicitOrders.Count == 0)
            throw new CursorPagingException(
                "Cursor paging requires at least one OrderBy or OrderByDescending clause.");

        var duplicate = explicitOrders
            .GroupBy(x => x.Column.ColumnName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new CursorPagingException(
                $"Cursor paging cannot order by column '{duplicate.Key}' more than once.");

        var primaryKey = EntityMetadata<T>.PrimaryKey
            ?? throw new CursorPagingException(
                $"Entity '{typeof(T).Name}' must have a mapped primary key for cursor paging.");

        var result = explicitOrders.ToList();
        if (!result.Any(x => string.Equals(
                x.Column.ColumnName, primaryKey.ColumnName, StringComparison.OrdinalIgnoreCase)))
        {
            result.Add(new CursorOrder(primaryKey, result[^1].Descending));
        }

        return result;
    }

    public static (string Sql, Dictionary<string, object?> Parameters) BuildSeekPredicate(
        IReadOnlyList<CursorOrder> orders,
        IReadOnlyList<object?> values)
    {
        if (orders.Count != values.Count)
            throw new CursorPagingException("The cursor value count does not match its ordering.", "mysql.invalid_cursor");

        var parameters = new Dictionary<string, object?>();
        for (var i = 0; i < orders.Count; i++)
        {
            parameters[$"@cursor_{i}"] = TypeConverter.ToDbValue(
                values[i], orders[i].Column.EffectiveStorageType);
        }

        var branches = new List<string>();
        for (var i = 0; i < orders.Count; i++)
        {
            var terms = new List<string>();
            for (var prefix = 0; prefix < i; prefix++)
                terms.Add($"`{orders[prefix].Column.ColumnName}` <=> @cursor_{prefix}");

            var column = $"`{orders[i].Column.ColumnName}`";
            var value = values[i];
            string? comparison;
            if (!orders[i].Descending)
            {
                comparison = value is null
                    ? $"{column} IS NOT NULL"
                    : $"{column} > @cursor_{i}";
            }
            else
            {
                comparison = value is null
                    ? null
                    : $"({column} < @cursor_{i} OR {column} IS NULL)";
            }

            if (comparison is null) continue;
            terms.Add(comparison);
            branches.Add($"({string.Join(" AND ", terms)})");
        }

        return (branches.Count == 0 ? "0 = 1" : $"({string.Join(" OR ", branches)})", parameters);
    }
}

internal static class CursorTokenCodec
{
    private const int CurrentVersion = 1;
    internal const int MaxEncodedLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static string Encode<T>(T item, IReadOnlyList<CursorOrder> orders) where T : class
    {
        var payload = new CursorPayload
        {
            Version = CurrentVersion,
            Entity = typeof(T).FullName ?? typeof(T).Name,
            Table = EntityMetadata<T>.TableName,
            Order = orders.Select(order =>
            {
                var value = order.Column.Get(item);
                var propertyType = order.Column.Property.PropertyType;
                var serializationType = value is null
                    ? propertyType
                    : Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                return new CursorPart
                {
                    Column = order.Column.ColumnName,
                    Descending = order.Descending,
                    Value = JsonSerializer.SerializeToElement(value, serializationType, JsonOptions)
                };
            }).ToList()
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return ToBase64Url(json);
    }

    public static IReadOnlyList<object?> Decode<T>(string token, IReadOnlyList<CursorOrder> orders)
        where T : class
    {
        try
        {
            if (token.Length > MaxEncodedLength)
                throw Invalid($"The cursor exceeds the maximum length of {MaxEncodedLength} characters.");
            if (string.IsNullOrWhiteSpace(token))
                throw Invalid("The cursor is empty.");

            var payload = JsonSerializer.Deserialize<CursorPayload>(FromBase64Url(token), JsonOptions)
                ?? throw Invalid("The cursor payload is empty.");

            if (payload.Version != CurrentVersion)
                throw Invalid($"Cursor version '{payload.Version}' is not supported.");
            if (!string.Equals(payload.Entity, typeof(T).FullName ?? typeof(T).Name, StringComparison.Ordinal) ||
                !string.Equals(payload.Table, EntityMetadata<T>.TableName, StringComparison.Ordinal))
                throw Invalid("The cursor belongs to a different entity or table.");
            if (payload.Order is null)
                throw Invalid("The cursor ordering is missing.");
            if (payload.Order.Count != orders.Count)
                throw Invalid("The cursor ordering does not match the query ordering.");

            var values = new List<object?>(orders.Count);
            for (var i = 0; i < orders.Count; i++)
            {
                var expected = orders[i];
                var actual = payload.Order[i] ?? throw Invalid("The cursor contains an invalid ordering component.");
                if (!string.Equals(actual.Column, expected.Column.ColumnName, StringComparison.OrdinalIgnoreCase) ||
                    actual.Descending != expected.Descending)
                    throw Invalid("The cursor ordering does not match the query ordering.");

                if (actual.Value.ValueKind == JsonValueKind.Null)
                {
                    if (expected.Column.Property.PropertyType.IsValueType &&
                        Nullable.GetUnderlyingType(expected.Column.Property.PropertyType) is null)
                        throw Invalid($"Cursor column '{actual.Column}' cannot contain null.");
                    values.Add(null);
                }
                else
                {
                    values.Add(actual.Value.Deserialize(expected.Column.Property.PropertyType, JsonOptions));
                }
            }

            return values;
        }
        catch (CursorPagingException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException
                                      or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            throw Invalid("The cursor is malformed or contains an unsupported value.", ex);
        }
    }

    private static CursorPagingException Invalid(string message, Exception? inner = null) =>
        new(message, "mysql.invalid_cursor", inner);

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid Base64URL length.")
        };
        return Convert.FromBase64String(padded);
    }

    private sealed class CursorPayload
    {
        [JsonPropertyName("v")] public int Version { get; init; }
        [JsonPropertyName("e")] public string Entity { get; init; } = string.Empty;
        [JsonPropertyName("t")] public string Table { get; init; } = string.Empty;
        [JsonPropertyName("o")] public List<CursorPart>? Order { get; init; } = [];
    }

    private sealed class CursorPart
    {
        [JsonPropertyName("c")] public string Column { get; init; } = string.Empty;
        [JsonPropertyName("d")] public bool Descending { get; init; }
        [JsonPropertyName("v")] public JsonElement Value { get; init; }
    }
}
