using System.Text;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>
/// Pages listings for the providers that have no native server-side paging.
/// </summary>
/// <remarks>
/// <para>
/// A continuation token names a cached listing snapshot plus the last path already delivered. The
/// first page walks the directory once, sorts it, and caches it; later pages resolve the snapshot
/// and resume immediately after the recorded path. Paging a listing of M entries therefore costs one
/// walk rather than one walk per page.
/// </para>
/// <para>
/// Ordering is ordinal by <see cref="StorageItem.Path"/>, unchanged from the offset-based tokens this
/// replaces. Because the resume point is a path rather than an index, entries added or removed
/// between pages shift the remainder of the listing instead of silently duplicating or skipping the
/// entries either side of the cut.
/// </para>
/// <para>
/// Tokens are opaque, single-use, and strictly increasing within a pass: the path they carry is the
/// last one delivered, and paths are sorted and de-duplicated, so no two pages of a listing can ever
/// produce the same token.
/// </para>
/// </remarks>
internal static class ProviderPaging
{
    private const string TokenVersion = "cl1";

    /// <summary>Builds one page of a listing, walking the source only when no snapshot is cached.</summary>
    /// <param name="scope">Stable identity of the listing, normally connection, path, and recursion mode.</param>
    /// <param name="options">Caller-supplied page size and continuation token.</param>
    /// <param name="collect">Produces the complete listing; invoked at most once per call.</param>
    /// <remarks>
    /// A token is only meaningful within the <paramref name="scope"/> that minted it. Presenting one
    /// to a different listing does not fail: the scope mismatch is treated as a cache miss, the other
    /// listing is walked, and it resumes from the path the token carries, yielding a listing
    /// truncated at that path. This is deliberate. Once a snapshot has been evicted, a foreign token
    /// is indistinguishable from an expired one, so rejecting the mismatch would make the same call
    /// fail or succeed depending on cache occupancy. Callers are expected to return a token to the
    /// listing that produced it.
    /// </remarks>
    /// <param name="cancellationToken">Token observed while walking the source.</param>
    /// <param name="cache">Snapshot store to use; defaults to the process-wide cache.</param>
    /// <returns>The requested page and the token for the page after it.</returns>
    public static async Task<Result<StoragePage>> CreateAsync(
        string scope,
        StorageListOptions options,
        Func<CancellationToken, Task<Result<IEnumerable<StorageItem>>>> collect,
        CancellationToken cancellationToken,
        ProviderListingCache? cache = null)
    {
        cache ??= ProviderListingCache.Shared;
        var cursor = DecodeToken(options.ContinuationToken);
        if (cursor.IsFailure) return Result<StoragePage>.Failure(cursor.Error!);

        var snapshotId = cursor.Value.SnapshotId;
        StorageItem[] items;
        var walked = false;
        if (snapshotId is null || !cache.TryGet(snapshotId, scope, out items))
        {
            // The snapshot expired, was evicted, or this is the first page. Re-walking is the
            // documented degradation: it costs another full listing but returns the same entries in
            // the same order, so the caller never observes the difference beyond latency.
            var collected = await collect(cancellationToken).ConfigureAwait(false);
            if (collected.IsFailure) return Result<StoragePage>.Failure(collected.Error!);
            items = Sort(collected.Value!);
            snapshotId = Guid.NewGuid().ToString("N");
            walked = true;
        }

        // The snapshot is cached unfiltered, so the same walk serves any filter and pages stay full.
        var visible = StorageListFilter.IsActive(options) ? [.. StorageListFilter.Apply(items, options)] : items;
        var start = cursor.Value.LastPath is null ? 0 : FirstAfter(visible, cursor.Value.LastPath);
        var length = Math.Min(options.PageSize, visible.Length - start);
        var page = new StorageItem[length];
        Array.Copy(visible, start, page, 0, length);
        var next = start + length < visible.Length ? EncodeToken(snapshotId, page[^1].Path) : null;

        // Only a listing that continues can ever be resumed, so a listing that fits in one page is
        // not stored at all. The cache is process-wide and bounded, and a caller walking many small
        // directories would otherwise churn snapshots that exist only to evict the large ones.
        if (walked && next is not null) cache.Store(snapshotId, scope, items);
        return Result<StoragePage>.Success(new StoragePage(page, next));
    }

    /// <summary>Builds the stable listing identity a continuation token is bound to.</summary>
    /// <param name="connectionId">Connection that produced the listing.</param>
    /// <param name="path">Directory being listed.</param>
    /// <param name="recursive">Whether descendants are included.</param>
    /// <returns>An identity string that differs whenever any of the inputs differ.</returns>
    public static string Scope(string connectionId, string path, bool recursive) =>
        $"{connectionId}|{(recursive ? '1' : '0')}|{path}";

    /// <summary>
    /// Sorts ordinally by path and drops repeated paths, which is what lets a path act as a cursor.
    /// </summary>
    private static StorageItem[] Sort(IEnumerable<StorageItem> source)
    {
        var ordered = source.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var unique = new List<StorageItem>(ordered.Length);
        foreach (var item in ordered)
        {
            if (unique.Count > 0 && string.Equals(unique[^1].Path, item.Path, StringComparison.Ordinal)) continue;
            unique.Add(item);
        }
        return unique.Count == ordered.Length ? ordered : unique.ToArray();
    }

    /// <summary>Finds the first index whose path sorts after the delivered cursor path.</summary>
    private static int FirstAfter(StorageItem[] items, string lastPath)
    {
        var low = 0;
        var high = items.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (StringComparer.Ordinal.Compare(items[middle].Path, lastPath) <= 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static string EncodeToken(string snapshotId, string lastPath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TokenVersion}\n{snapshotId}\n{lastPath}"));

    private static Result<Cursor> DecodeToken(string? token)
    {
        if (token is null) return Result<Cursor>.Success(default);
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            var version = text.IndexOf('\n');
            if (version < 0 || !text.AsSpan(0, version).SequenceEqual(TokenVersion))
                return Invalid();
            var separator = text.IndexOf('\n', version + 1);
            if (separator < 0) return Invalid();
            var snapshotId = text[(version + 1)..separator];
            // Everything after the second newline is the path, so paths containing newlines survive.
            var lastPath = text[(separator + 1)..];
            return snapshotId.Length == 0 ? Invalid() : Result<Cursor>.Success(new Cursor(snapshotId, lastPath));
        }
        catch (FormatException) { return Invalid(); }
    }

    private static Result<Cursor> Invalid() =>
        Result<Cursor>.Failure(StorageErrors.InvalidPath("The continuation token is invalid."));

    private readonly record struct Cursor(string? SnapshotId, string? LastPath);
}
