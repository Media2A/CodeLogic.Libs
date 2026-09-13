namespace CL.PostgreSQL.Core;

/// <summary>Central PostgreSQL rendering helpers used by generated SQL.</summary>
internal static class PostgreSqlDialect
{
    /// <summary>The schema used when an entity does not declare one.</summary>
    public const string DefaultSchema = "public";

    /// <summary>
    /// Wraps an identifier in double quotes, escaping any embedded double quote by doubling
    /// it. Every generated identifier goes through here so a crafted column or table name
    /// cannot terminate the quoted identifier and inject SQL.
    /// <para>
    /// Note that quoting makes the identifier case-sensitive, which is why entity and column
    /// names must match the database exactly — PostgreSQL folds unquoted identifiers to
    /// lower case but preserves the case of quoted ones.
    /// </para>
    /// </summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>Quotes each dot-separated part of a qualified identifier (e.g. <c>public.users</c>).</summary>
    public static string QuoteMultipart(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        var parts = identifier.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Multipart identifiers cannot contain empty parts.", nameof(identifier));
        return string.Join('.', parts.Select(Quote));
    }

    /// <summary>Renders a schema-qualified table reference.</summary>
    public static string Qualify(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

    /// <summary>
    /// Escapes the LIKE metacharacters <c>%</c> and <c>_</c> (and the escape character
    /// itself) so a user-supplied search term matches literally. PostgreSQL's default LIKE
    /// escape character is the backslash, so no explicit <c>ESCAPE</c> clause is required.
    /// </summary>
    public static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// PostgreSQL's extended protocol caps a single statement at 65535 bound parameters.
    /// Batched inserts are chunked so they stay under it.
    /// </summary>
    public const int ParameterLimit = 65535;

    /// <summary>Headroom left for the non-row parameters a statement also binds.</summary>
    public const int ReservedParameters = 16;

    /// <summary>Largest number of rows that fit in one batch for a given column count.</summary>
    public static int MaxBatchRows(int columnsPerRow, int reserved = ReservedParameters) =>
        Math.Max(1, (ParameterLimit - Math.Max(0, reserved)) / Math.Max(1, columnsPerRow));

    /// <summary>
    /// Null-safe equality. MySQL spells this <c>&lt;=&gt;</c>; the SQL-standard form
    /// PostgreSQL implements is <c>IS NOT DISTINCT FROM</c>.
    /// </summary>
    public const string NullSafeEquals = "IS NOT DISTINCT FROM";
}
