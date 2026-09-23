using System.Text;
using CL.Storage.Models;

namespace CL.Storage.Providers;

/// <summary>
/// Infers folders from object keys in recursive object-store listings. A folder in S3, Azure, or Swift
/// often has no marker object ("a/b/" exists only because "a/b/file" does), so a recursive listing that
/// only reports markers would hide it from sync and comparison.
/// </summary>
/// <remarks>
/// Keys usually arrive in lexicographic order, so every key under a folder is contiguous: a folder is new
/// exactly when the previous key was not inside it. The previous key is carried across pages in the
/// continuation token, so a folder is reported once even when its keys straddle a page boundary. Only a key
/// inside the folder counts: a file named like the folder (<c>a/b</c> beside <c>a/b/c</c>) does not hide it.
/// A folder is never left out, but it can be reported again (on a later page) when keys arrive out of order,
/// as S3 Express directory buckets list them, or when a folder marker ends a page; and pages are sorted each
/// on their own, so an inferred folder can follow items that sort after it. Build trees by path, not by order.
/// </remarks>
internal static class ImplicitDirectories
{
    private const string TokenPrefix = "cl1:";

    /// <summary>Adds the folders above <paramref name="path"/> (and below <paramref name="root"/>) that <paramref name="previous"/> did not already imply.</summary>
    public static void AddParents(ICollection<StorageItem> items, string path, string root, string? previous, Func<string, StorageItem> directory)
    {
        var parents = new List<string>();
        var parent = path.TrimEnd('/');
        while (true)
        {
            var slash = parent.LastIndexOf('/');
            if (slash < 0) break;
            parent = parent[..slash];
            if (parent.Length <= root.Length) break;
            parents.Add(parent);
        }
        for (var i = parents.Count - 1; i >= 0; i--)
        {
            var folder = parents[i];
            if (previous is not null && previous.StartsWith(folder + "/", StringComparison.Ordinal))
                continue;
            items.Add(directory(folder));
        }
    }

    /// <summary>Wraps a provider token with the last path of the page.</summary>
    public static string? Wrap(string? nativeToken, string? lastPath) =>
        string.IsNullOrEmpty(nativeToken)
            ? null
            : $"{TokenPrefix}{Convert.ToBase64String(Encoding.UTF8.GetBytes(lastPath ?? string.Empty))}:{nativeToken}";

    /// <summary>Splits a token from <see cref="Wrap"/>; any other token is passed through as the provider's own.</summary>
    public static (string? NativeToken, string? LastPath) Unwrap(string? token)
    {
        if (token is null || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return (token, null);
        var rest = token[TokenPrefix.Length..];
        var colon = rest.IndexOf(':');
        if (colon < 0) return (token, null);
        try
        {
            var last = Encoding.UTF8.GetString(Convert.FromBase64String(rest[..colon]));
            var native = rest[(colon + 1)..];
            return (native.Length == 0 ? null : native, last.Length == 0 ? null : last);
        }
        catch (FormatException)
        {
            return (token, null);
        }
    }
}
