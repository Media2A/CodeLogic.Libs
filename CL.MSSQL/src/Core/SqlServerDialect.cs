using System.Text;

namespace CL.MSSQL.Core;

/// <summary>Central SQL Server rendering helpers used by generated SQL.</summary>
internal static class SqlServerDialect
{
    public const int ParameterLimit = 2100;
    public const int ReservedParameters = 16;

    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public static string QuoteMultipart(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var parts = identifier.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Multipart identifiers cannot contain empty parts.", nameof(identifier));
        return string.Join('.', parts.Select(Quote));
    }

    public static string Qualify(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

    public static string EscapeLike(string value) => value
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    public static int MaxBatchRows(int columnsPerRow, int reserved = ReservedParameters) =>
        Math.Max(1, (ParameterLimit - Math.Max(0, reserved)) / Math.Max(1, columnsPerRow));
}
