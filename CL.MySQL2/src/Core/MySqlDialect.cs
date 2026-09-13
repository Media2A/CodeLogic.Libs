namespace CL.MySQL2.Core;

/// <summary>Central MySQL rendering helpers used by generated SQL.</summary>
internal static class MySqlDialect
{
    /// <summary>
    /// Wraps an identifier in backticks, escaping any embedded backtick by doubling it.
    /// Every generated identifier goes through here so a crafted column or table name
    /// cannot terminate the quoted identifier and inject SQL.
    /// </summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";
    }

    /// <summary>Quotes each dot-separated part of a qualified identifier (e.g. <c>db.table</c>).</summary>
    public static string QuoteMultipart(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var parts = identifier.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Multipart identifiers cannot contain empty parts.", nameof(identifier));
        return string.Join('.', parts.Select(Quote));
    }

    /// <summary>
    /// Escapes the LIKE metacharacters <c>%</c> and <c>_</c> (and the escape character
    /// itself) so a user-supplied search term matches literally. Pair with
    /// <c>ESCAPE '\\'</c>, which is MySQL's default escape character for LIKE.
    /// </summary>
    public static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
