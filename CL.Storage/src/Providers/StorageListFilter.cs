using System.Text.RegularExpressions;
using CL.Storage.Models;

namespace CL.Storage.Providers;

/// <summary>Applies the item filters in <see cref="StorageListOptions"/> to listing results.</summary>
internal static class StorageListFilter
{
    private static readonly string[] InternalPrefixes = [".cl-storage-", ".clstorage-"];

    /// <summary>Returns whether a path is, or lies inside, a staging or backup item the library created.</summary>
    public static bool IsInternal(string path) =>
        path.Split('/').Any(segment => InternalPrefixes.Any(prefix => segment.StartsWith(prefix, StringComparison.Ordinal)));

    public static bool IsActive(StorageListOptions options) =>
        !options.IncludeInternal || !options.IncludeHidden || !string.IsNullOrEmpty(options.NamePattern);

    /// <summary>
    /// Filters listed items. Leaving hidden items out also leaves out everything inside a hidden folder (or a
    /// hidden link to one) that the same listing holds, so a recursive listing does not show <c>.git/config</c>.
    /// </summary>
    /// <param name="items">The listed items.</param>
    /// <param name="options">The listing's filters.</param>
    /// <param name="root">
    /// The listed folder, for listings that arrive in pages (object stores): every folder name between it and an
    /// item is then tested too, so <c>.git/b</c> stays hidden on a page that no longer holds <c>.git</c> itself.
    /// A hidden listed folder does not hide its own contents.
    /// </param>
    public static IEnumerable<StorageItem> Apply(IEnumerable<StorageItem> items, StorageListOptions options, string? root = null)
    {
        if (!IsActive(options))
            return items;
        var pattern = string.IsNullOrEmpty(options.NamePattern) ? null : Glob(options.NamePattern);
        HashSet<string>? hiddenFolders = null;
        if (!options.IncludeHidden)
        {
            var listed = items as IReadOnlyCollection<StorageItem> ?? [.. items];
            items = listed;
            hiddenFolders = new HashSet<string>(
                listed.Where(item => item.ItemType is StorageItemType.Directory or StorageItemType.Link && IsHidden(item)).Select(item => item.Path),
                StringComparer.Ordinal);
        }
        return items.Where(item =>
            (options.IncludeInternal || !IsInternal(item.Path)) &&
            (options.IncludeHidden || !(IsHidden(item) || InsideAny(item.Path, hiddenFolders!) || (root is not null && BelowDotFolder(item.Path, root)))) &&
            (pattern is null || pattern.IsMatch(item.Name)));
    }

    private static bool IsHidden(StorageItem item) => item.IsHidden || item.Name.StartsWith('.');

    private static bool InsideAny(string path, HashSet<string> folders)
    {
        if (folders.Count == 0) return false;
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (folders.Contains(path[..slash])) return true;
        }
        return false;
    }

    /// <summary>Whether a folder name between <paramref name="root"/> and the item starts with a dot.</summary>
    internal static bool BelowDotFolder(string path, string root)
    {
        var start = root.Length == 0 ? 0 : path.Length > root.Length && path[root.Length] == '/' && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? root.Length + 1 : -1;
        if (start < 0) return false;
        for (var slash = path.IndexOf('/', start); slash >= 0; slash = path.IndexOf('/', start))
        {
            if (path[start] == '.') return true;
            start = slash + 1;
        }
        return false;
    }

    /// <summary>Translates <c>*</c> and <c>?</c> wildcards into an anchored, case-insensitive expression.</summary>
    internal static Regex Glob(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
}
