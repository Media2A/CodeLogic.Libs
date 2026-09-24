namespace CL.Storage.Providers;

/// <summary>
/// ETag comparison as RFC 9110 §8.8.3.2 defines it. A weak validator (<c>W/"…"</c>, such as the local provider's
/// synthetic one) says only that two representations are probably equivalent, so it can prove that something
/// changed but never that it did not: a strong comparison, which resume identities and "unchanged since read"
/// checks need, never matches it.
/// </summary>
internal static class StorageETags
{
    /// <summary>Whether a validator is weak (<c>W/"…"</c>).</summary>
    public static bool IsWeak(string? etag) => etag is not null && etag.TrimStart().StartsWith("W/", StringComparison.Ordinal);

    /// <summary>
    /// The strong comparison: both validators present, neither weak, and equal once quotes are ignored. Use it where a
    /// match must prove the content is the same (resume, unchanged while streaming, delete only if unchanged).
    /// </summary>
    public static bool StrongEquals(string? a, string? b) =>
        a is not null && b is not null && !IsWeak(a) && !IsWeak(b) && string.Equals(Opaque(a), Opaque(b), StringComparison.Ordinal);

    /// <summary>
    /// The weak comparison: equal once a <c>W/</c> prefix and quotes are ignored. A mismatch proves a change; a match
    /// of weak validators does not prove there was none.
    /// </summary>
    public static bool WeakEquals(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Opaque(a), Opaque(b), StringComparison.Ordinal);

    private static string Opaque(string etag)
    {
        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal)) value = value[2..];
        return value.Trim('"');
    }
}
