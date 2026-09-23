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

    public static IEnumerable<StorageItem> Apply(IEnumerable<StorageItem> items, StorageListOptions options)
    {
        if (!IsActive(options))
            return items;
        var pattern = string.IsNullOrEmpty(options.NamePattern) ? null : Glob(options.NamePattern);
        return items.Where(item =>
            (options.IncludeInternal || !IsInternal(item.Path)) &&
            (options.IncludeHidden || !(item.IsHidden || item.Name.StartsWith('.'))) &&
            (pattern is null || pattern.IsMatch(item.Name)));
    }

    /// <summary>Translates <c>*</c> and <c>?</c> wildcards into an anchored, case-insensitive expression.</summary>
    internal static Regex Glob(string pattern) => new(
        "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
}
