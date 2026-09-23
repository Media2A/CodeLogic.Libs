using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>How two items relate after a comparison.</summary>
public enum StorageDiffKind
{
    /// <summary>Exists only on the source side.</summary>
    OnlyInSource,
    /// <summary>Exists only on the destination side.</summary>
    OnlyInDestination,
    /// <summary>Exists on both sides with different content, by the chosen criteria.</summary>
    Different,
    /// <summary>Exists on both sides and matches, by the chosen criteria.</summary>
    Same
}

/// <summary>Why two files were judged different.</summary>
[Flags]
public enum StorageDiffReason
{
    /// <summary>No difference.</summary>
    None = 0,
    /// <summary>Sizes differ.</summary>
    Size = 1,
    /// <summary>The source was modified later than the destination.</summary>
    SourceNewer = 2,
    /// <summary>The destination was modified later than the source.</summary>
    DestinationNewer = 4,
    /// <summary>Checksums differ.</summary>
    Checksum = 8,
    /// <summary>One side is a file and the other a directory.</summary>
    Type = 16,
    /// <summary>A checksum was needed but the hashing budget ran out, so equality is unknown.</summary>
    Undecidable = 32
}

/// <summary>What a comparison looks at.</summary>
[Flags]
public enum StorageCompareBy
{
    /// <summary>File sizes.</summary>
    Size = 1,
    /// <summary>Modification times, within <see cref="StorageCompareOptions.TimeTolerance"/>.</summary>
    Time = 2,
    /// <summary>Content checksums: the server's where it keeps one, otherwise downloaded and hashed within the budget.</summary>
    Checksum = 4
}

/// <summary>Controls a directory comparison.</summary>
public sealed record StorageCompareOptions
{
    /// <summary>The metadata key that keeps a file's source modification time on stores that cannot set times.</summary>
    public const string ModifiedMetadataKey = "cl-mtime";

    /// <summary>Gets the criteria; size and time by default, like FileZilla's directory comparison.</summary>
    public StorageCompareBy CompareBy { get; init; } = StorageCompareBy.Size | StorageCompareBy.Time;
    /// <summary>Gets the slack allowed between modification times; FAT and many FTP servers store two-second or whole-second times.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Gets the checksum algorithm used with <see cref="StorageCompareBy.Checksum"/>.</summary>
    public StorageChecksumAlgorithm ChecksumAlgorithm { get; init; } = StorageChecksumAlgorithm.Md5;
    /// <summary>Gets whether hidden items (dot-files) take part.</summary>
    public bool IncludeHidden { get; init; } = true;
    /// <summary>Gets an optional <c>*</c>/<c>?</c> file-name filter; directories are always walked.</summary>
    public string? NamePattern { get; init; }
    /// <summary>
    /// Gets path globs a file must match to take part (<c>**</c> spans folders, <c>*</c> and <c>?</c> stay within
    /// one name, matched against the path relative to the compared folder, case-insensitively). Empty means all.
    /// </summary>
    public IReadOnlyList<string> Include { get; init; } = [];
    /// <summary>Gets path globs to leave out entirely; a folder matching <c>folder/**</c> is left out with its contents.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
    /// <summary>Gets how many files are hashed at once.</summary>
    public int HashConcurrency { get; init; } = 4;
    /// <summary>Gets the most files downloaded to hash in one comparison; beyond it a difference is <see cref="StorageDiffReason.Undecidable"/>.</summary>
    public int? MaxHashedFiles { get; init; }
    /// <summary>Gets the most bytes downloaded to hash in one comparison.</summary>
    public long? MaxHashedBytes { get; init; }
    /// <summary>
    /// Gets whether names differing only by case are one item. Null decides from the connections
    /// (<see cref="StorageFeature.CaseInsensitivePaths"/> on either side). When true, two such names on one side
    /// fail the comparison with <c>storage.conflict</c>.
    /// </summary>
    public bool? CaseInsensitive { get; init; }
    /// <summary>Gets how links are treated: skipped by default.</summary>
    public StorageLinkHandling LinkHandling { get; init; } = StorageLinkHandling.Skip;
    /// <summary>Gets the most items one side may hold; a larger tree fails instead of exhausting memory.</summary>
    public int MaxItems { get; init; } = 1_000_000;

    internal Result Validate()
    {
        if (TimeTolerance < TimeSpan.Zero) return Result.Failure(StorageErrors.InvalidContent("TimeTolerance cannot be negative."));
        if (HashConcurrency is < 1 or > 64) return Result.Failure(StorageErrors.InvalidContent("HashConcurrency must be between 1 and 64."));
        if (MaxItems < 1) return Result.Failure(StorageErrors.InvalidContent("MaxItems must be positive."));
        if (LinkHandling == StorageLinkHandling.Recreate)
            return Result.Failure(StorageErrors.InvalidContent("Comparisons and syncs cannot recreate links; use Skip, Follow, or Reject."));
        return Enum.IsDefined(LinkHandling) ? Result.Success() : Result.Failure(StorageErrors.InvalidContent("LinkHandling is invalid."));
    }
}

/// <summary>One path compared on both sides.</summary>
/// <param name="RelativePath">Path relative to the compared directories (the source's spelling when case-insensitive).</param>
/// <param name="Kind">How the two sides relate.</param>
/// <param name="Reasons">Why they differ, for <see cref="StorageDiffKind.Different"/>.</param>
/// <param name="Source">The source item, when it exists.</param>
/// <param name="Destination">The destination item, when it exists.</param>
public sealed record StorageDiffEntry(
    string RelativePath,
    StorageDiffKind Kind,
    StorageDiffReason Reasons,
    StorageItem? Source,
    StorageItem? Destination)
{
    /// <summary>
    /// Gets the destination's spelling of the path when it differs from <see cref="RelativePath"/> only by case
    /// (a case-insensitive comparison); null when both sides spell it the same.
    /// </summary>
    public string? DestinationRelativePath { get; init; }

    /// <summary>Gets whether this entry is a directory on either side.</summary>
    public bool IsDirectory => (Source ?? Destination)?.ItemType == StorageItemType.Directory;
}

/// <summary>The result of comparing two directory trees.</summary>
/// <param name="Entries">Every compared path, sorted.</param>
public sealed record StorageDiff(IReadOnlyList<StorageDiffEntry> Entries)
{
    /// <summary>Gets whether the trees match by the chosen criteria.</summary>
    public bool Identical => Entries.All(entry => entry.Kind == StorageDiffKind.Same);

    /// <summary>Gets paths left out by the <c>Exclude</c>/<c>Include</c> filters, per side, for safe folder deletes.</summary>
    internal IReadOnlySet<string> ExcludedSource { get; init; } = new HashSet<string>();
    internal IReadOnlySet<string> ExcludedDestination { get; init; } = new HashSet<string>();
    internal bool CaseInsensitive { get; init; }
}

/// <summary>A listed tree: included items keyed by path, and the paths the filters left out.</summary>
internal sealed record StorageTree(Dictionary<string, StorageItem> Items, HashSet<string> Excluded, bool Missing);

/// <summary>Compares directory trees across any two storage connections.</summary>
public static class StorageCompare
{
    /// <summary>Compares two directory trees recursively.</summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection; may be the same as <paramref name="source"/>.</param>
    /// <param name="destinationPath">Destination directory; a missing one compares as empty.</param>
    /// <param name="options">Comparison criteria, filters, and budgets.</param>
    /// <param name="cancellationToken">Token used to cancel the listings and checksums.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public static async Task<Result<StorageDiff>> CompareAsync(
        IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new StorageCompareOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageDiff>.Failure(valid.Error!);
        var filter = new PathFilter(options);
        var sourceTree = await ListTreeAsync(source, sourcePath, options, filter, required: true, cancellationToken).ConfigureAwait(false);
        if (sourceTree.IsFailure) return Result<StorageDiff>.Failure(sourceTree.Error!);
        var destinationTree = await ListTreeAsync(destination, destinationPath, options, filter, required: false, cancellationToken).ConfigureAwait(false);
        if (destinationTree.IsFailure) return Result<StorageDiff>.Failure(destinationTree.Error!);
        return await DiffAsync(source, sourceTree.Value!, destination, destinationTree.Value!, options, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Result<StorageDiff>> DiffAsync(
        IStorageService source,
        StorageTree sourceTree,
        IStorageService destination,
        StorageTree destinationTree,
        StorageCompareOptions options,
        CancellationToken cancellationToken)
    {
        var insensitive = options.CaseInsensitive ??
            (source.Capabilities.Supports(StorageFeature.CaseInsensitivePaths) || destination.Capabilities.Supports(StorageFeature.CaseInsensitivePaths));
        var left = Key(sourceTree.Items, insensitive, "source");
        if (left.IsFailure) return Result<StorageDiff>.Failure(left.Error!);
        var right = Key(destinationTree.Items, insensitive, "destination");
        if (right.IsFailure) return Result<StorageDiff>.Failure(right.Error!);

        var entries = new List<StorageDiffEntry>();
        var pairs = new List<(string Path, StorageItem Left, StorageItem Right)>();
        var spelling = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in left.Value!.Keys.Union(right.Value!.Keys, StringComparer.Ordinal))
        {
            left.Value.TryGetValue(key, out var a);
            right.Value.TryGetValue(key, out var b);
            var path = a?.Relative ?? b!.Relative;
            if (a is null) { entries.Add(new StorageDiffEntry(path, StorageDiffKind.OnlyInDestination, StorageDiffReason.None, null, b!.Item)); continue; }
            if (b is null) { entries.Add(new StorageDiffEntry(path, StorageDiffKind.OnlyInSource, StorageDiffReason.None, a.Item, null)); continue; }
            pairs.Add((path, a.Item, b.Item));
            if (!string.Equals(a.Relative, b.Relative, StringComparison.Ordinal)) spelling[path] = b.Relative;
        }

        var reasons = await CompareFilesAsync(source, destination, pairs, options, cancellationToken).ConfigureAwait(false);
        if (reasons.IsFailure) return Result<StorageDiff>.Failure(reasons.Error!);
        foreach (var (path, a, b) in pairs)
        {
            var reason = reasons.Value![path];
            entries.Add(new StorageDiffEntry(path, reason == StorageDiffReason.None ? StorageDiffKind.Same : StorageDiffKind.Different, reason, a, b)
            {
                DestinationRelativePath = spelling.GetValueOrDefault(path)
            });
        }
        entries.Sort((x, y) => string.CompareOrdinal(x.RelativePath, y.RelativePath));
        return Result<StorageDiff>.Success(new StorageDiff(entries)
        {
            ExcludedSource = sourceTree.Excluded,
            ExcludedDestination = destinationTree.Excluded,
            CaseInsensitive = insensitive
        });
    }

    /// <summary>
    /// The effective modification time: the source time kept in metadata on stores that cannot set times. The
    /// kept time is trusted only when it is not later than the object itself (plus a minute of clock skew): a
    /// copy is always written after its source was modified, so a later value is not one this library wrote.
    /// </summary>
    internal static DateTimeOffset? EffectiveModified(StorageItem item)
    {
        if (!item.Metadata.TryGetValue(StorageCompareOptions.ModifiedMetadataKey, out var kept) ||
            !DateTimeOffset.TryParse(kept, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            return item.LastModified;
        return item.LastModified is { } written && parsed > written + TimeSpan.FromMinutes(1) ? item.LastModified : parsed;
    }

    private sealed record Keyed(string Relative, StorageItem Item);

    private static Result<Dictionary<string, Keyed>> Key(Dictionary<string, StorageItem> items, bool insensitive, string side)
    {
        var keyed = new Dictionary<string, Keyed>(StringComparer.Ordinal);
        foreach (var (relative, item) in items)
        {
            var key = insensitive ? relative.ToUpperInvariant() : relative;
            if (keyed.TryGetValue(key, out var existing))
                return Result<Dictionary<string, Keyed>>.Failure(StorageErrors.Conflict(
                    $"The {side} has '{existing.Relative}' and '{relative}', which are the same name where case is ignored.",
                    $"caseCollision={existing.Relative}|{relative}"));
            keyed[key] = new Keyed(relative, item);
        }
        return Result<Dictionary<string, Keyed>>.Success(keyed);
    }

    /// <summary>Decides every file pair; object-store times and checksums are resolved in parallel and on a budget.</summary>
    private static async Task<Result<Dictionary<string, StorageDiffReason>>> CompareFilesAsync(
        IStorageService source,
        IStorageService destination,
        List<(string Path, StorageItem Left, StorageItem Right)> pairs,
        StorageCompareOptions options,
        CancellationToken cancellationToken)
    {
        var reasons = new Dictionary<string, StorageDiffReason>(StringComparer.Ordinal);
        var needsMetadata = new List<int>();
        for (var i = 0; i < pairs.Count; i++)
        {
            var (path, a, b) = pairs[i];
            reasons[path] = Basic(a, b, options);
            // Stores that cannot set times keep the source's time in metadata, which listings often omit.
            if ((reasons[path] & (StorageDiffReason.SourceNewer | StorageDiffReason.DestinationNewer)) != 0 &&
                (reasons[path] & StorageDiffReason.Size) == 0 &&
                (NeedsMetadata(source, a) || NeedsMetadata(destination, b)))
                needsMetadata.Add(i);
        }
        await Parallel.ForEachAsync(needsMetadata, new ParallelOptions { MaxDegreeOfParallelism = options.HashConcurrency, CancellationToken = cancellationToken }, async (i, token) =>
        {
            var (path, a, b) = pairs[i];
            if (NeedsMetadata(source, a) && await source.GetInfoAsync(a.Path, token).ConfigureAwait(false) is { IsSuccess: true } full) a = full.Value!;
            if (NeedsMetadata(destination, b) && await destination.GetInfoAsync(b.Path, token).ConfigureAwait(false) is { IsSuccess: true } other) b = other.Value!;
            lock (reasons) reasons[path] = Basic(a, b, options);
            lock (pairs) pairs[i] = (path, a, b);
        }).ConfigureAwait(false);

        if (!options.CompareBy.HasFlag(StorageCompareBy.Checksum))
            return Result<Dictionary<string, StorageDiffReason>>.Success(reasons);

        var candidates = pairs.Where(pair => pair.Left.ItemType == StorageItemType.File && pair.Right.ItemType == StorageItemType.File &&
            (reasons[pair.Path] & (StorageDiffReason.Size | StorageDiffReason.Type)) == 0).ToList();
        var budget = new HashBudget(options.MaxHashedFiles, options.MaxHashedBytes);
        Error? failure = null;
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = options.HashConcurrency, CancellationToken = cancellationToken }, async (pair, token) =>
        {
            var a = await DigestAsync(source, pair.Left, options, budget, token).ConfigureAwait(false);
            var b = await DigestAsync(destination, pair.Right, options, budget, token).ConfigureAwait(false);
            lock (reasons)
            {
                if (a.IsFailure || b.IsFailure) { failure ??= (a.Error ?? b.Error); return; }
                var current = reasons[pair.Path];
                if (a.Value is null || b.Value is null)
                    reasons[pair.Path] = current | StorageDiffReason.Undecidable;
                else if (!string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase))
                    reasons[pair.Path] = current | StorageDiffReason.Checksum;
                else
                    // Equal content wins over differing times: a checksum match means the file needs no copy.
                    reasons[pair.Path] = current & ~(StorageDiffReason.SourceNewer | StorageDiffReason.DestinationNewer);
            }
        }).ConfigureAwait(false);
        return failure is null
            ? Result<Dictionary<string, StorageDiffReason>>.Success(reasons)
            : Result<Dictionary<string, StorageDiffReason>>.Failure(failure);
    }

    private static bool NeedsMetadata(IStorageService storage, StorageItem item) =>
        item.ItemType == StorageItemType.File &&
        storage.Capabilities.Supports(StorageFeature.MetadataRead) &&
        !storage.Capabilities.Supports(StorageFeature.SetTimestamps) &&
        !item.Metadata.ContainsKey(StorageCompareOptions.ModifiedMetadataKey);

    private static StorageDiffReason Basic(StorageItem left, StorageItem right, StorageCompareOptions options)
    {
        if (left.ItemType != right.ItemType)
            return StorageDiffReason.Type;
        if (left.ItemType is StorageItemType.Directory)
            return StorageDiffReason.None;
        if (left.ItemType is StorageItemType.Link)
            return string.Equals(left.LinkTarget, right.LinkTarget, StringComparison.Ordinal) ? StorageDiffReason.None : StorageDiffReason.Checksum;
        var reasons = StorageDiffReason.None;
        if (options.CompareBy.HasFlag(StorageCompareBy.Size) && left.Size != right.Size)
            reasons |= StorageDiffReason.Size;
        if (options.CompareBy.HasFlag(StorageCompareBy.Time) && EffectiveModified(left) is { } l && EffectiveModified(right) is { } r)
        {
            if (l - r > options.TimeTolerance) reasons |= StorageDiffReason.SourceNewer;
            else if (r - l > options.TimeTolerance) reasons |= StorageDiffReason.DestinationNewer;
        }
        return reasons;
    }

    /// <summary>A file's digest: the server's when it keeps one, otherwise computed if the budget allows; null when neither.</summary>
    private static async Task<Result<string?>> DigestAsync(IStorageService storage, StorageItem item, StorageCompareOptions options, HashBudget budget, CancellationToken cancellationToken)
    {
        var server = await storage.GetServerChecksumAsync(item.Path, options.ChecksumAlgorithm, cancellationToken).ConfigureAwait(false);
        if (server.IsSuccess) return Result<string?>.Success(server.Value!.HexValue);
        if (!budget.TryTake(item.Size ?? 0)) return Result<string?>.Success(null);
        var computed = await storage.ComputeChecksumAsync(item.Path, options.ChecksumAlgorithm, mode: StorageChecksumMode.ComputeOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        return computed.IsSuccess ? Result<string?>.Success(computed.Value!.HexValue) : Result<string?>.Failure(computed.Error!);
    }

    private sealed class HashBudget(int? files, long? bytes)
    {
        private int _files;
        private long _bytes;

        public bool TryTake(long size)
        {
            lock (this)
            {
                if (files is { } maxFiles && _files + 1 > maxFiles) return false;
                if (bytes is { } maxBytes && _bytes + size > maxBytes) return false;
                _files++;
                _bytes += size;
                return true;
            }
        }
    }

    /// <summary>Lists a tree once, applying hidden/name/include/exclude filters and link handling.</summary>
    internal static async Task<Result<StorageTree>> ListTreeAsync(
        IStorageService storage,
        string root,
        StorageCompareOptions options,
        PathFilter filter,
        bool required,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var normalizedRoot = StoragePath.Normalize(root);
        if (normalizedRoot.IsFailure) return Result<StorageTree>.Failure(normalizedRoot.Error!);
        var listOptions = new StorageListOptions { Recursive = true, IncludeHidden = options.IncludeHidden };
        var pattern = string.IsNullOrEmpty(options.NamePattern) ? null : Providers.StorageListFilter.Glob(options.NamePattern);
        var missing = false;
        await foreach (var item in storage.EnumerateItemsAsync(normalizedRoot.Value!, listOptions, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure)
            {
                // A missing destination is empty; any other listing failure makes the tree unusable.
                if (!required && item.Error!.Code == StorageErrors.NotFoundCode && items.Count == 0) { missing = true; break; }
                return Result<StorageTree>.Failure(item.Error!);
            }
            var relative = Relative(normalizedRoot.Value!, item.Value!.Path);
            if (relative is null or { Length: 0 }) continue;
            var current = item.Value;
            if (filter.Excludes(relative, current.ItemType == StorageItemType.Directory) ||
                (pattern is not null && current.ItemType == StorageItemType.File && !pattern.IsMatch(current.Name)))
            {
                excluded.Add(relative);
                continue;
            }
            if (current.ItemType == StorageItemType.Link)
            {
                switch (options.LinkHandling)
                {
                    case StorageLinkHandling.Skip:
                        excluded.Add(relative);
                        continue;
                    case StorageLinkHandling.Reject:
                        return Result<StorageTree>.Failure(StorageErrors.Unsupported(
                            $"'{current.Path}' is a link. Set LinkHandling to skip, follow, or recreate links."));
                    case StorageLinkHandling.Follow:
                        var target = await storage.GetInfoAsync(current.Path, cancellationToken).ConfigureAwait(false);
                        if (target.IsFailure || target.Value!.ItemType != StorageItemType.File) { excluded.Add(relative); continue; }
                        current = target.Value with { Path = current.Path, Name = current.Name };
                        break;
                }
            }
            items[relative] = current;
            if (items.Count > options.MaxItems)
                return Result<StorageTree>.Failure(StorageErrors.TooLarge(
                    $"'{normalizedRoot.Value}' holds more than {options.MaxItems} items; raise MaxItems or narrow the filters."));
        }
        return Result<StorageTree>.Success(new StorageTree(items, excluded, missing));
    }

    internal static string? Relative(string root, string path)
    {
        if (root.Length == 0) return path;
        if (string.Equals(root, path, StringComparison.Ordinal)) return string.Empty;
        return path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..] : null;
    }
}

/// <summary>Include and exclude globs over relative paths: <c>**</c> spans folders, <c>*</c> and <c>?</c> stay in one name.</summary>
internal sealed class PathFilter
{
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;

    public PathFilter(StorageCompareOptions options)
    {
        _include = [.. options.Include.Select(Compile)];
        _exclude = [.. options.Exclude.Select(Compile)];
    }

    public bool Excludes(string relativePath, bool isDirectory)
    {
        // A folder is excluded when "folder/**"-style patterns cover it, so its contents go with it.
        if (_exclude.Any(glob => glob.IsMatch(relativePath) || (isDirectory && glob.IsMatch(relativePath + "/"))))
            return true;
        return !isDirectory && _include.Length > 0 && !_include.Any(glob => glob.IsMatch(relativePath));
    }

    internal static Regex Compile(string glob)
    {
        var pattern = new StringBuilder("^");
        var text = glob.Replace('\\', '/').TrimStart('/');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var slashAfter = i + 2 < text.Length && text[i + 2] == '/';
                pattern.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') pattern.Append("[^/]*");
            else if (c == '?') pattern.Append("[^/]");
            else pattern.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(pattern.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
