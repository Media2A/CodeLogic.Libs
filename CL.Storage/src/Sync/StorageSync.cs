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
    Type = 16
}

/// <summary>What a comparison looks at.</summary>
[Flags]
public enum StorageCompareBy
{
    /// <summary>File sizes.</summary>
    Size = 1,
    /// <summary>Modification times, within <see cref="StorageCompareOptions.TimeTolerance"/>.</summary>
    Time = 2,
    /// <summary>Content checksums (server-side when available, otherwise downloaded and hashed; slow).</summary>
    Checksum = 4
}

/// <summary>Controls a directory comparison.</summary>
public sealed record StorageCompareOptions
{
    /// <summary>Gets the criteria; size and time by default, like FileZilla's directory comparison.</summary>
    public StorageCompareBy CompareBy { get; init; } = StorageCompareBy.Size | StorageCompareBy.Time;
    /// <summary>Gets the slack allowed between modification times; FAT and many FTP servers store two-second or whole-second times.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Gets the checksum algorithm used with <see cref="StorageCompareBy.Checksum"/>.</summary>
    public StorageChecksumAlgorithm ChecksumAlgorithm { get; init; } = StorageChecksumAlgorithm.Md5;
    /// <summary>Gets whether hidden items (dot-files) take part.</summary>
    public bool IncludeHidden { get; init; } = true;
    /// <summary>Gets an optional <c>*</c>/<c>?</c> name filter; directories are always walked.</summary>
    public string? NamePattern { get; init; }
}

/// <summary>One path compared on both sides.</summary>
/// <param name="RelativePath">Path relative to the compared directories.</param>
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
    /// <summary>Gets whether this entry is a directory on either side.</summary>
    public bool IsDirectory => (Source ?? Destination)?.ItemType == StorageItemType.Directory;
}

/// <summary>The result of comparing two directory trees.</summary>
/// <param name="Entries">Every compared path, sorted.</param>
public sealed record StorageDiff(IReadOnlyList<StorageDiffEntry> Entries)
{
    /// <summary>Gets whether the trees match by the chosen criteria.</summary>
    public bool Identical => Entries.All(entry => entry.Kind == StorageDiffKind.Same);
}

/// <summary>Which way a sync makes changes.</summary>
public enum StorageSyncDirection
{
    /// <summary>Copies files that are new or changed at the source; never deletes.</summary>
    Update,
    /// <summary>Makes the destination match the source; deletes destination-only items when <see cref="StorageSyncOptions.DeleteExtraneous"/> is set.</summary>
    Mirror,
    /// <summary>Copies each file to the side where it is missing or older; never deletes.</summary>
    TwoWay
}

/// <summary>Controls a directory sync.</summary>
public sealed record StorageSyncOptions
{
    /// <summary>Gets which way changes flow.</summary>
    public StorageSyncDirection Direction { get; init; } = StorageSyncDirection.Update;
    /// <summary>Gets whether <see cref="StorageSyncDirection.Mirror"/> deletes items that exist only at the destination.</summary>
    public bool DeleteExtraneous { get; init; }
    /// <summary>Gets whether to only plan: nothing is changed and every action is returned as planned.</summary>
    public bool DryRun { get; init; }
    /// <summary>Gets whether copied files get the source's modification time, when the destination supports it.</summary>
    public bool PreserveTimestamps { get; init; } = true;
    /// <summary>Gets how many files are copied at once.</summary>
    public int MaxConcurrency { get; init; } = 4;
    /// <summary>Gets the comparison settings.</summary>
    public StorageCompareOptions Compare { get; init; } = new();
    /// <summary>Gets an optional progress sink; reports carry the current file.</summary>
    public IProgress<StorageTransferProgress>? Progress { get; init; }
}

/// <summary>What a sync does to one path.</summary>
public enum StorageSyncActionKind
{
    /// <summary>Copies from source to destination.</summary>
    CopyToDestination,
    /// <summary>Copies from destination to source (two-way sync).</summary>
    CopyToSource,
    /// <summary>Deletes from the destination (mirror with deletes).</summary>
    DeleteFromDestination,
    /// <summary>Creates a directory at the destination.</summary>
    CreateDirectory
}

/// <summary>One planned or performed sync step.</summary>
/// <param name="RelativePath">Path relative to the synced directories.</param>
/// <param name="Kind">What is done.</param>
/// <param name="Reason">Why, from the comparison.</param>
/// <param name="Bytes">Content size copied, when known.</param>
/// <param name="Error">Failure, when the step failed.</param>
public sealed record StorageSyncAction(string RelativePath, StorageSyncActionKind Kind, StorageDiffReason Reason, long? Bytes, Error? Error = null);

/// <summary>The outcome of a sync, or its plan for a dry run.</summary>
/// <param name="Actions">Steps performed (or planned), including failed ones.</param>
/// <param name="Unchanged">Files that already matched.</param>
/// <param name="DryRun">Whether nothing was changed.</param>
public sealed record StorageSyncReport(IReadOnlyList<StorageSyncAction> Actions, int Unchanged, bool DryRun)
{
    /// <summary>Gets the steps that failed.</summary>
    public IReadOnlyList<StorageSyncAction> Failed => [.. Actions.Where(action => action.Error is not null)];
    /// <summary>Gets the number of files copied in either direction.</summary>
    public int Copied => Actions.Count(action => action.Error is null && action.Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource);
    /// <summary>Gets the number of items deleted.</summary>
    public int Deleted => Actions.Count(action => action.Error is null && action.Kind == StorageSyncActionKind.DeleteFromDestination);
}

/// <summary>Compares and synchronizes directory trees across any two storage connections.</summary>
public static class StorageSync
{
    /// <summary>Compares two directory trees recursively.</summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection; may be the same as <paramref name="source"/>.</param>
    /// <param name="destinationPath">Destination directory; a missing one compares as empty.</param>
    /// <param name="options">Comparison criteria.</param>
    /// <param name="cancellationToken">Token used to cancel the listings and checksums.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public static async Task<Result<StorageDiff>> CompareAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new StorageCompareOptions();
        var sourceItems = await ListTreeAsync(source, sourcePath, options, required: true, cancellationToken).ConfigureAwait(false);
        if (sourceItems.IsFailure) return Result<StorageDiff>.Failure(sourceItems.Error!);
        var destinationItems = await ListTreeAsync(destination, destinationPath, options, required: false, cancellationToken).ConfigureAwait(false);
        if (destinationItems.IsFailure) return Result<StorageDiff>.Failure(destinationItems.Error!);

        var entries = new List<StorageDiffEntry>();
        foreach (var path in sourceItems.Value!.Keys.Union(destinationItems.Value!.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            sourceItems.Value.TryGetValue(path, out var left);
            destinationItems.Value.TryGetValue(path, out var right);
            if (left is null) { entries.Add(new StorageDiffEntry(path, StorageDiffKind.OnlyInDestination, StorageDiffReason.None, null, right)); continue; }
            if (right is null) { entries.Add(new StorageDiffEntry(path, StorageDiffKind.OnlyInSource, StorageDiffReason.None, left, null)); continue; }
            var reasons = await DifferenceAsync(source, left, destination, right, options, cancellationToken).ConfigureAwait(false);
            if (reasons.IsFailure) return Result<StorageDiff>.Failure(reasons.Error!);
            entries.Add(new StorageDiffEntry(path, reasons.Value == StorageDiffReason.None ? StorageDiffKind.Same : StorageDiffKind.Different, reasons.Value, left, right));
        }
        return Result<StorageDiff>.Success(new StorageDiff(entries));
    }

    /// <summary>
    /// Synchronizes two directory trees. Each file is copied with a staged, atomic upload; failures of single
    /// files are collected in the report instead of stopping the sync.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection; may be the same as <paramref name="source"/>.</param>
    /// <param name="destinationPath">Destination directory; created when missing.</param>
    /// <param name="options">Direction, deletes, dry run, and comparison settings.</param>
    /// <param name="cancellationToken">Token used to cancel the sync.</param>
    /// <returns>The steps taken, or planned for a dry run.</returns>
    public static async Task<Result<StorageSyncReport>> SyncAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new StorageSyncOptions();
        if (options.MaxConcurrency is < 1 or > 64)
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("MaxConcurrency must be between 1 and 64."));
        if (options.DeleteExtraneous && options.Direction != StorageSyncDirection.Mirror)
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("DeleteExtraneous only applies to Mirror syncs."));

        var diff = await source.CompareAsync(sourcePath, destination, destinationPath, options.Compare, cancellationToken).ConfigureAwait(false);
        if (diff.IsFailure) return Result<StorageSyncReport>.Failure(diff.Error!);
        var plan = Plan(diff.Value!, options);
        var unchanged = diff.Value!.Entries.Count(entry => entry.Kind == StorageDiffKind.Same && !entry.IsDirectory);
        if (options.DryRun)
            return Result<StorageSyncReport>.Success(new StorageSyncReport(plan, unchanged, DryRun: true));

        var done = new StorageSyncAction[plan.Count];
        // Directories first (parents before children), then files in parallel, then deletes deepest first.
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan[i].Kind != StorageSyncActionKind.CreateDirectory) continue;
            var created = await destination.CreateDirectoryAsync(Join(destinationPath, plan[i].RelativePath), cancellationToken).ConfigureAwait(false);
            done[i] = plan[i] with { Error = created.Error };
        }
        var entries = diff.Value.Entries.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, plan.Count).Where(i => plan[i].Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource),
            new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var action = plan[i];
                var entry = entries[action.RelativePath];
                var result = action.Kind == StorageSyncActionKind.CopyToDestination
                    ? await CopyFileAsync(source, Join(sourcePath, action.RelativePath), entry.Source!, destination, Join(destinationPath, action.RelativePath), options, token).ConfigureAwait(false)
                    : await CopyFileAsync(destination, Join(destinationPath, action.RelativePath), entry.Destination!, source, Join(sourcePath, action.RelativePath), options, token).ConfigureAwait(false);
                done[i] = action with { Error = result.Error };
            }).ConfigureAwait(false);
        // A failed copy means the destination does not match the source yet, so deleting "extra" items
        // could remove the only good copy of something; deletes wait for a clean run.
        var failedCopies = done.Count(action => action is { Error: not null, Kind: StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource or StorageSyncActionKind.CreateDirectory });
        foreach (var i in Enumerable.Range(0, plan.Count).Where(i => plan[i].Kind == StorageSyncActionKind.DeleteFromDestination)
                     .OrderByDescending(i => plan[i].RelativePath.Count(c => c == '/')))
        {
            if (failedCopies > 0)
            {
                done[i] = plan[i] with { Error = StorageErrors.PartialFailure($"Not deleted because {failedCopies} copy or directory step(s) failed in this run.") };
                continue;
            }
            var deleted = await destination.DeleteAsync(Join(destinationPath, plan[i].RelativePath),
                new StorageDeleteOptions { Recursive = true, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
            done[i] = plan[i] with { Error = deleted.Error };
        }
        return Result<StorageSyncReport>.Success(new StorageSyncReport(done, unchanged, DryRun: false));
    }

    internal static IReadOnlyList<StorageSyncAction> Plan(StorageDiff diff, StorageSyncOptions options)
    {
        var actions = new List<StorageSyncAction>();
        var deletedRoots = new List<string>();
        foreach (var entry in diff.Entries)
        {
            switch (entry.Kind)
            {
                case StorageDiffKind.OnlyInSource:
                    actions.Add(entry.IsDirectory
                        ? new StorageSyncAction(entry.RelativePath, StorageSyncActionKind.CreateDirectory, StorageDiffReason.None, null)
                        : new StorageSyncAction(entry.RelativePath, StorageSyncActionKind.CopyToDestination, StorageDiffReason.None, entry.Source!.Size));
                    break;
                case StorageDiffKind.OnlyInDestination when options.Direction == StorageSyncDirection.TwoWay && !entry.IsDirectory:
                    actions.Add(new StorageSyncAction(entry.RelativePath, StorageSyncActionKind.CopyToSource, StorageDiffReason.None, entry.Destination!.Size));
                    break;
                case StorageDiffKind.OnlyInDestination when options.DeleteExtraneous:
                    // A deleted directory takes its contents with it, so its children need no action of their own.
                    if (deletedRoots.Any(root => entry.RelativePath.StartsWith(root + "/", StringComparison.Ordinal))) break;
                    if (entry.IsDirectory) deletedRoots.Add(entry.RelativePath);
                    actions.Add(new StorageSyncAction(entry.RelativePath, StorageSyncActionKind.DeleteFromDestination, StorageDiffReason.None, null));
                    break;
                case StorageDiffKind.Different when !entry.IsDirectory:
                    var towardSource = options.Direction == StorageSyncDirection.TwoWay && entry.Reasons.HasFlag(StorageDiffReason.DestinationNewer);
                    // Update and mirror never copy an older source over a newer destination unless the content differs too.
                    if (options.Direction is StorageSyncDirection.Update or StorageSyncDirection.Mirror &&
                        entry.Reasons == StorageDiffReason.DestinationNewer) break;
                    actions.Add(towardSource
                        ? new StorageSyncAction(entry.RelativePath, StorageSyncActionKind.CopyToSource, entry.Reasons, entry.Destination!.Size)
                        : new StorageSyncAction(entry.RelativePath, StorageSyncActionKind.CopyToDestination, entry.Reasons, entry.Source!.Size));
                    break;
            }
        }
        return actions;
    }

    private static async Task<Result<StorageDiffReason>> DifferenceAsync(
        IStorageService source,
        StorageItem left,
        IStorageService destination,
        StorageItem right,
        StorageCompareOptions options,
        CancellationToken cancellationToken)
    {
        if (left.ItemType != right.ItemType)
            return Result<StorageDiffReason>.Success(StorageDiffReason.Type);
        if (left.ItemType == StorageItemType.Directory)
            return Result<StorageDiffReason>.Success(StorageDiffReason.None);
        var reasons = StorageDiffReason.None;
        if (options.CompareBy.HasFlag(StorageCompareBy.Size) && left.Size != right.Size)
            reasons |= StorageDiffReason.Size;
        if (options.CompareBy.HasFlag(StorageCompareBy.Time) && left.LastModified is { } l && right.LastModified is { } r)
        {
            if (l - r > options.TimeTolerance) reasons |= StorageDiffReason.SourceNewer;
            else if (r - l > options.TimeTolerance) reasons |= StorageDiffReason.DestinationNewer;
        }
        if (options.CompareBy.HasFlag(StorageCompareBy.Checksum) && (reasons & StorageDiffReason.Size) == 0)
        {
            var a = await source.ComputeChecksumAsync(left.Path, options.ChecksumAlgorithm, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (a.IsFailure) return Result<StorageDiffReason>.Failure(a.Error!);
            var b = await destination.ComputeChecksumAsync(right.Path, options.ChecksumAlgorithm, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (b.IsFailure) return Result<StorageDiffReason>.Failure(b.Error!);
            if (!string.Equals(a.Value!.HexValue, b.Value!.HexValue, StringComparison.OrdinalIgnoreCase))
                reasons |= StorageDiffReason.Checksum;
            else
                // Equal content wins over differing times: a checksum match means the file needs no copy.
                reasons &= ~(StorageDiffReason.SourceNewer | StorageDiffReason.DestinationNewer);
        }
        return Result<StorageDiffReason>.Success(reasons);
    }

    private static async Task<Result<Dictionary<string, StorageItem>>> ListTreeAsync(
        IStorageService storage,
        string root,
        StorageCompareOptions options,
        bool required,
        CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        var normalizedRoot = StoragePath.Normalize(root);
        if (normalizedRoot.IsFailure) return Result<Dictionary<string, StorageItem>>.Failure(normalizedRoot.Error!);
        var listOptions = new StorageListOptions { Recursive = true, IncludeHidden = options.IncludeHidden };
        var pattern = string.IsNullOrEmpty(options.NamePattern) ? null : Providers.StorageListFilter.Glob(options.NamePattern);
        await foreach (var item in storage.EnumerateItemsAsync(normalizedRoot.Value!, listOptions, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure)
            {
                if (!required && item.Error!.Code == StorageErrors.NotFoundCode) break;
                return Result<Dictionary<string, StorageItem>>.Failure(item.Error!);
            }
            var relative = Relative(normalizedRoot.Value!, item.Value!.Path);
            if (relative is null or { Length: 0 }) continue;
            if (pattern is not null && item.Value.ItemType == StorageItemType.File && !pattern.IsMatch(item.Value.Name)) continue;
            items[relative] = item.Value;
        }
        return Result<Dictionary<string, StorageItem>>.Success(items);
    }

    private static async Task<Result> CopyFileAsync(
        IStorageService from,
        string fromPath,
        StorageItem fromItem,
        IStorageService to,
        string toPath,
        StorageSyncOptions options,
        CancellationToken cancellationToken)
    {
        var download = await from.DownloadAsync(fromPath, new StorageDownloadOptions
        {
            Progress = options.Progress is { } progress ? new PathProgress(progress, fromPath) : null
        }, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result.Failure(download.Error!);
        await using (var stream = download.Value!)
        {
            var upload = await to.UploadAsync(toPath, stream, new StorageUploadOptions
            {
                Overwrite = true,
                CreateParents = true,
                ContentType = fromItem.ContentType
            }, cancellationToken).ConfigureAwait(false);
            if (upload.IsFailure) return Result.Failure(upload.Error!);
        }
        if (options.PreserveTimestamps && fromItem.LastModified is { } modified &&
            to is IStorageAttributeService && to.Capabilities.Supports(StorageFeature.SetTimestamps))
        {
            // Best effort: a failure only means the next comparison relies on size and "source newer".
            _ = await to.SetTimestampsAsync(toPath, modified, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return Result.Success();
    }

    private static string? Relative(string root, string path)
    {
        if (root.Length == 0) return path;
        if (string.Equals(root, path, StringComparison.Ordinal)) return string.Empty;
        return path.StartsWith(root + "/", StringComparison.Ordinal) ? path[(root.Length + 1)..] : null;
    }

    private static string Join(string root, string relative)
    {
        var normalized = root.Replace('\\', '/').Trim('/');
        return normalized.Length == 0 ? relative : $"{normalized}/{relative}";
    }

    private sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
    }
}
