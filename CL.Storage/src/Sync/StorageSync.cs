using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>
/// Synchronizes directory trees across any two storage connections, in two steps: a plan that can be shown,
/// stored, and approved by its digest, then an apply that re-checks every item against the plan. Two-way syncs
/// with a baseline are three-way: edits and deletions made on one side are carried over, and changes on both
/// sides are conflicts resolved by <see cref="StorageSyncOptions.ConflictPolicy"/>.
/// </summary>
public static class StorageSync
{
    /// <summary>Compares two directory trees recursively.</summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; a missing one compares as empty.</param>
    /// <param name="options">Comparison criteria, filters, and budgets.</param>
    /// <param name="cancellationToken">Token used to cancel the listings and checksums.</param>
    /// <returns>Every path on either side with its relation.</returns>
    public static Task<Result<StorageDiff>> CompareAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageCompareOptions? options = null,
        CancellationToken cancellationToken = default) =>
        StorageCompare.CompareAsync(source, sourcePath, destination, destinationPath, options, cancellationToken);

    /// <summary>
    /// Plans a sync without changing anything. The plan lists every step with the versions it depends on,
    /// withholds deletions a safety rule forbids (see <see cref="StorageSyncPlan.Warnings"/>), and carries a
    /// digest; pass that digest to <see cref="ApplySyncAsync"/> to run exactly this plan.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; created when missing.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
    /// <param name="cancellationToken">Token used to cancel the listings.</param>
    /// <returns>The plan.</returns>
    public static async Task<Result<StorageSyncPlan>> PlanSyncAsync(
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
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageSyncPlan>.Failure(valid.Error!);
        var sourceRoot = StoragePath.Normalize(sourcePath);
        if (sourceRoot.IsFailure) return Result<StorageSyncPlan>.Failure(sourceRoot.Error!);
        var destinationRoot = StoragePath.Normalize(destinationPath);
        if (destinationRoot.IsFailure) return Result<StorageSyncPlan>.Failure(destinationRoot.Error!);

        var baseline = StorageSyncBaseline.Empty;
        if (options.StateStore is { } store)
            baseline = await store.LoadAsync(options.SyncId!, cancellationToken).ConfigureAwait(false) ?? StorageSyncBaseline.Empty;

        var filter = new PathFilter(options.Compare);
        var sourceTree = await StorageCompare.ListTreeAsync(source, sourceRoot.Value!, options.Compare, filter, required: true, cancellationToken).ConfigureAwait(false);
        if (sourceTree.IsFailure) return Result<StorageSyncPlan>.Failure(sourceTree.Error!);
        var destinationTree = await StorageCompare.ListTreeAsync(destination, destinationRoot.Value!, options.Compare, filter, required: false, cancellationToken).ConfigureAwait(false);
        if (destinationTree.IsFailure) return Result<StorageSyncPlan>.Failure(destinationTree.Error!);
        var diff = await StorageCompare.DiffAsync(source, sourceTree.Value!, destination, destinationTree.Value!, options.Compare, cancellationToken).ConfigureAwait(false);
        if (diff.IsFailure) return Result<StorageSyncPlan>.Failure(diff.Error!);

        var warnings = new List<string>();
        List<StorageSyncAction> actions;
        int unchanged;
        var agreed = new Dictionary<string, StorageSyncBaselineEntry>(StringComparer.Ordinal);
        if (options.Direction == StorageSyncDirection.TwoWay)
            (actions, unchanged, agreed) = PlanTwoWay(diff.Value!, baseline, options, warnings);
        else
            (actions, unchanged) = PlanOneWay(diff.Value!, options, warnings);
        actions = ApplyDeletionSafety(actions, diff.Value!, baseline, sourceTree.Value!, destinationTree.Value!, options, warnings);

        var plan = new StorageSyncPlan
        {
            SourceRoot = sourceRoot.Value!,
            DestinationRoot = destinationRoot.Value!,
            SyncId = options.SyncId,
            BaselineGeneration = baseline.Generation,
            Direction = options.Direction,
            ConflictPolicy = options.ConflictPolicy,
            Actions = Order(actions),
            Unchanged = unchanged,
            Agreed = options.StateStore is not null ? agreed : new Dictionary<string, StorageSyncBaselineEntry>(),
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Result<StorageSyncPlan>.Success(plan with { Digest = plan.ComputeDigest() });
    }

    /// <summary>
    /// Applies an approved plan. It is refused when its digest differs from <paramref name="approvedDigest"/>
    /// (it was changed after approval), when its baseline moved on (another run synced in between), or when it
    /// has blocked conflicts (unless <see cref="StorageSyncOptions.ApplyWithConflicts"/>). Every step first
    /// checks that its items are still the versions planned; a changed item's step is reported
    /// <see cref="StorageSyncActionOutcome.Stale"/> and not taken.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory; must be the plan's.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; must be the plan's.</param>
    /// <param name="plan">The plan from <see cref="PlanSyncAsync"/>.</param>
    /// <param name="approvedDigest">The digest that was approved.</param>
    /// <param name="options">The options the plan was made with (store, verification, retries, concurrency).</param>
    /// <param name="cancellationToken">Stops the run; the report keeps what was done.</param>
    /// <returns>Each step's outcome.</returns>
    public static async Task<Result<StorageSyncReport>> ApplySyncAsync(
        this IStorageService source,
        string sourcePath,
        IStorageService destination,
        string destinationPath,
        StorageSyncPlan plan,
        string approvedDigest,
        StorageSyncOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new StorageSyncOptions();
        var valid = options.Validate();
        if (valid.IsFailure) return Result<StorageSyncReport>.Failure(valid.Error!);
        if (!string.Equals(plan.ComputeDigest(), plan.Digest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(approvedDigest, plan.Digest, StringComparison.OrdinalIgnoreCase))
            return Result<StorageSyncReport>.Failure(StorageErrors.Conflict("The plan was changed after it was made, or a different plan was approved."));
        if (StoragePath.Normalize(sourcePath).Value != plan.SourceRoot || StoragePath.Normalize(destinationPath).Value != plan.DestinationRoot)
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("The plan was made for different directories."));
        if (!plan.IsApprovable && !options.ApplyWithConflicts)
            return Result<StorageSyncReport>.Failure(StorageErrors.Conflict($"The plan has {plan.Conflicts.Count} unresolved conflict(s)."));
        if (!string.Equals(plan.SyncId, options.SyncId, StringComparison.Ordinal))
            return Result<StorageSyncReport>.Failure(StorageErrors.InvalidContent("The plan was made for a different SyncId than the options name."));

        // Two applies of one sync in this process would both pass the baseline check; the second waits its turn
        // and is then refused by it. (Across processes the baseline's generation still refuses the later save.)
        var gate = options.SyncId is { } syncId ? Gates.GetOrAdd(syncId, _ => new SemaphoreSlim(1, 1)) : null;
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (options.StateStore is { } store)
            {
                var current = await store.LoadAsync(options.SyncId!, cancellationToken).ConfigureAwait(false);
                if ((current?.Generation ?? 0) != plan.BaselineGeneration)
                    return Result<StorageSyncReport>.Failure(StorageErrors.Conflict("Another run synced these directories after the plan was made; plan again."));
            }

            var run = new SyncRun(source, destination, plan, options);
            var cancelled = false;
            try
            {
                await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
            }
            var saved = false;
            if (options.StateStore is not null && plan.Direction == StorageSyncDirection.TwoWay)
                saved = await SaveBaselineAsync(plan, run, options, CancellationToken.None).ConfigureAwait(false);
            return Result<StorageSyncReport>.Success(new StorageSyncReport
            {
                Plan = plan,
                Results = run.Results,
                Cancelled = cancelled,
                BaselineSaved = saved
            });
        }
        finally
        {
            gate?.Release();
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Plans and applies in one call. With <see cref="StorageSyncOptions.DryRun"/> the plan is returned and
    /// nothing changes. A plan with blocked conflicts is applied only with <see cref="StorageSyncOptions.ApplyWithConflicts"/>.
    /// </summary>
    /// <param name="source">Source connection.</param>
    /// <param name="sourcePath">Source directory.</param>
    /// <param name="destination">Destination connection.</param>
    /// <param name="destinationPath">Destination directory; created when missing.</param>
    /// <param name="options">Direction, conflicts, baseline, filters, and safety limits.</param>
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
        options ??= new StorageSyncOptions();
        var plan = await source.PlanSyncAsync(sourcePath, destination, destinationPath, options, cancellationToken).ConfigureAwait(false);
        if (plan.IsFailure) return Result<StorageSyncReport>.Failure(plan.Error!);
        if (options.DryRun)
        {
            return Result<StorageSyncReport>.Success(new StorageSyncReport
            {
                Plan = plan.Value!,
                DryRun = true,
                Results = [.. plan.Value!.Actions.Select(action => new StorageSyncActionResult(action,
                    action.WithheldReason is null ? StorageSyncActionOutcome.NotRun : StorageSyncActionOutcome.Withheld))]
            });
        }
        return await source.ApplySyncAsync(sourcePath, destination, destinationPath, plan.Value!, plan.Value!.Digest, options, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- planning

    private static (List<StorageSyncAction> Actions, int Unchanged) PlanOneWay(StorageDiff diff, StorageSyncOptions options, List<string> warnings)
    {
        var actions = new List<StorageSyncAction>();
        var unchanged = 0;
        var clashes = TypeClashes(diff);
        foreach (var entry in diff.Entries)
        {
            // Everything below a path that is a file on one side and a directory on the other is left alone with it.
            if (Under(clashes, entry.RelativePath, diff.CaseInsensitive)) continue;
            switch (entry.Kind)
            {
                case StorageDiffKind.Same:
                    if (!entry.IsDirectory) unchanged++;
                    break;
                case StorageDiffKind.OnlyInSource:
                    actions.Add(entry.IsDirectory
                        ? new StorageSyncAction { RelativePath = entry.RelativePath, Kind = StorageSyncActionKind.CreateDirectory }
                        : Copy(entry, StorageSyncActionKind.CopyToDestination));
                    break;
                case StorageDiffKind.OnlyInDestination when options.DeleteExtraneous:
                    // Files go one by one, each checked at apply time; a folder goes only once empty, and never
                    // while it holds items the filters left out.
                    if (entry.IsDirectory && diff.ExcludedDestination.Any(path => path.StartsWith(entry.RelativePath + "/", StringComparison.Ordinal))) break;
                    actions.Add(new StorageSyncAction
                    {
                        RelativePath = entry.RelativePath,
                        Kind = StorageSyncActionKind.DeleteFromDestination,
                        Destination = StorageSyncIdentity.Of(entry.Destination)
                    });
                    break;
                case StorageDiffKind.Different when entry.Reasons.HasFlag(StorageDiffReason.Type):
                    warnings.Add($"'{entry.RelativePath}' is a file on one side and a directory on the other; it and everything below it were left alone.");
                    break;
                case StorageDiffKind.Different when !entry.IsDirectory:
                    // Update and mirror never replace a newer destination with an older source.
                    if (entry.Reasons.HasFlag(StorageDiffReason.DestinationNewer))
                    {
                        if (entry.Reasons != StorageDiffReason.DestinationNewer)
                            warnings.Add($"'{entry.RelativePath}' is newer at the destination and differs; it was left alone.");
                        break;
                    }
                    actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination));
                    break;
            }
        }
        return (actions, unchanged);
    }

    private static StorageSyncAction Copy(StorageDiffEntry entry, StorageSyncActionKind kind, StorageSyncConflictKind? conflict = null) => new()
    {
        RelativePath = entry.RelativePath,
        DestinationRelativePath = entry.DestinationRelativePath,
        Kind = kind,
        Reason = entry.Reasons,
        Bytes = kind == StorageSyncActionKind.CopyToDestination ? entry.Source?.Size : entry.Destination?.Size,
        Source = StorageSyncIdentity.Of(entry.Source),
        Destination = StorageSyncIdentity.Of(entry.Destination),
        Conflict = conflict
    };

    private static List<string> TypeClashes(StorageDiff diff) =>
        [.. diff.Entries.Where(entry => entry.Reasons.HasFlag(StorageDiffReason.Type)).Select(entry => entry.RelativePath)];

    private static bool Under(List<string> roots, string path, bool insensitive)
    {
        var comparison = insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return roots.Any(root => path.StartsWith(root + "/", comparison));
    }

    private enum Change { Absent, Unchanged, Created, Modified, Deleted }

    private static Change Classify(StorageSyncIdentity? before, StorageItem? now, TimeSpan tolerance) =>
        (before, now) switch
        {
            (null, null) => Change.Absent,
            (null, _) => Change.Created,
            (_, null) => Change.Deleted,
            _ => before.Matches(now, tolerance) ? Change.Unchanged : Change.Modified
        };

    private static (List<StorageSyncAction> Actions, int Unchanged, Dictionary<string, StorageSyncBaselineEntry> Agreed) PlanTwoWay(
        StorageDiff diff, StorageSyncBaseline baseline, StorageSyncOptions options, List<string> warnings)
    {
        var tolerance = options.Compare.TimeTolerance;
        var actions = new List<StorageSyncAction>();
        var agreed = new Dictionary<string, StorageSyncBaselineEntry>(StringComparer.Ordinal);
        var unchanged = 0;
        var hasBaseline = options.StateStore is not null;
        var insensitive = diff.CaseInsensitive;
        var baselineByKey = new Dictionary<string, (string Path, StorageSyncBaselineEntry Entry)>(StringComparer.Ordinal);
        foreach (var (path, value) in baseline.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!baselineByKey.TryAdd(Key(path, insensitive), (path, value)))
                warnings.Add($"The baseline holds '{baselineByKey[Key(path, insensitive)].Path}' and '{path}', the same name where case is ignored; the first was used.");
        }
        var entriesByKey = diff.Entries.ToDictionary(entry => Key(entry.RelativePath, insensitive), StringComparer.Ordinal);
        var clashes = TypeClashes(diff);

        foreach (var key in entriesByKey.Keys.Union(baselineByKey.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            entriesByKey.TryGetValue(key, out var entry);
            var base_ = baselineByKey.TryGetValue(key, out var b) ? b.Entry : null;
            var path = entry?.RelativePath ?? b.Path;
            if (Under(clashes, path, insensitive)) continue;
            if (entry is { Reasons: var r } && r.HasFlag(StorageDiffReason.Type))
            {
                warnings.Add($"'{path}' is a file on one side and a directory on the other; it and everything below it were left alone.");
                continue;
            }
            if (entry?.IsDirectory ?? base_?.IsDirectory ?? false)
            {
                PlanDirectory(actions, agreed, entry, path, hasBaseline && base_ is { IsDirectory: true }, options);
                continue;
            }

            var left = Classify(base_?.Source, entry?.Source, tolerance);
            var right = Classify(base_?.Destination, entry?.Destination, tolerance);
            if (!hasBaseline)
            {
                // Without a baseline nothing can be known to be deleted: missing files are copied, differences conflict.
                left = entry?.Source is null ? Change.Absent : Change.Created;
                right = entry?.Destination is null ? Change.Absent : Change.Created;
            }

            switch (left, right)
            {
                case (Change.Unchanged or Change.Absent, Change.Unchanged or Change.Absent) when entry?.Kind != StorageDiffKind.OnlyInSource && entry?.Kind != StorageDiffKind.OnlyInDestination:
                    if (entry is null) break;
                    // Neither side changed since the baseline, yet their content differs: the baseline cannot be
                    // trusted for this path, so it is a conflict rather than silently "in sync".
                    if (entry.Kind == StorageDiffKind.Different && (entry.Reasons & (StorageDiffReason.Size | StorageDiffReason.Checksum)) != 0)
                    {
                        Resolve(actions, entry, StorageSyncConflictKind.BothModified, options);
                        break;
                    }
                    unchanged++;
                    Agree(agreed, entry);
                    break;
                case (Change.Created or Change.Modified, Change.Unchanged or Change.Absent):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToDestination));
                    break;
                case (Change.Unchanged or Change.Absent, Change.Created or Change.Modified):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToSource));
                    break;
                case (Change.Deleted, Change.Unchanged):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, DestinationRelativePath = entry!.DestinationRelativePath, Kind = StorageSyncActionKind.DeleteFromDestination, Destination = StorageSyncIdentity.Of(entry.Destination) }
                        : Copy(entry!, StorageSyncActionKind.CopyToSource));
                    break;
                case (Change.Unchanged, Change.Deleted):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromSource, Source = StorageSyncIdentity.Of(entry!.Source) }
                        : Copy(entry!, StorageSyncActionKind.CopyToDestination));
                    break;
                case (Change.Deleted, Change.Deleted):
                case (Change.Deleted, Change.Absent):
                case (Change.Absent, Change.Deleted):
                    break;
                case (Change.Created, Change.Created) or (Change.Modified, Change.Modified) or (Change.Created, Change.Modified) or (Change.Modified, Change.Created):
                    if (entry!.Kind == StorageDiffKind.Same)
                    {
                        unchanged++;
                        Agree(agreed, entry);
                        break;
                    }
                    Resolve(actions, entry, left == Change.Modified && right == Change.Modified ? StorageSyncConflictKind.BothModified : StorageSyncConflictKind.BothCreated, options);
                    break;
                case (Change.Deleted, Change.Created or Change.Modified):
                case (Change.Created or Change.Modified, Change.Deleted):
                    ResolveDeleteVersusModify(actions, entry!, deletedOnSource: left == Change.Deleted, options);
                    break;
                default:
                    if (entry is not null && entry.Kind == StorageDiffKind.Same)
                    {
                        unchanged++;
                        Agree(agreed, entry);
                    }
                    break;
            }
        }
        DropUnsafeDirectoryDeletes(actions, diff, insensitive);
        return (actions, unchanged, agreed);
    }

    private static void Agree(Dictionary<string, StorageSyncBaselineEntry> agreed, StorageDiffEntry entry) =>
        agreed[entry.RelativePath] = new StorageSyncBaselineEntry(StorageSyncIdentity.Of(entry.Source), StorageSyncIdentity.Of(entry.Destination));

    /// <summary>
    /// Plans a directory in a two-way sync: created on the side that lacks it, or — when the baseline had it on
    /// both sides — deleted where the other side deleted it (only once everything in it goes too).
    /// </summary>
    private static void PlanDirectory(
        List<StorageSyncAction> actions,
        Dictionary<string, StorageSyncBaselineEntry> agreed,
        StorageDiffEntry? entry,
        string path,
        bool wasOnBothSides,
        StorageSyncOptions options)
    {
        var atSource = entry?.Source is not null;
        var atDestination = entry?.Destination is not null;
        if (atSource && atDestination)
        {
            agreed[path] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
            return;
        }
        if (atSource)
        {
            actions.Add(wasOnBothSides && options.PropagateDeletes
                ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromSource }
                : new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.CreateDirectory });
        }
        else if (atDestination)
        {
            actions.Add(wasOnBothSides && options.PropagateDeletes
                ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromDestination }
                : new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.CreateDirectoryAtSource });
        }
    }

    /// <summary>
    /// Keeps a directory deletion only when everything inside it on that side is deleted in the same plan;
    /// otherwise the directory stays (and the kept items inside are synced as usual).
    /// </summary>
    private static void DropUnsafeDirectoryDeletes(List<StorageSyncAction> actions, StorageDiff diff, bool insensitive)
    {
        var comparison = insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directories = diff.Entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).ToHashSet(insensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var kind in new[] { StorageSyncActionKind.DeleteFromSource, StorageSyncActionKind.DeleteFromDestination })
        {
            var onSource = kind == StorageSyncActionKind.DeleteFromSource;
            var deleted = actions.Where(action => action.Kind == kind).Select(action => action.RelativePath).ToHashSet(insensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var directory in actions.Where(action => action.Kind == kind && directories.Contains(action.RelativePath))
                         .OrderByDescending(action => action.RelativePath.Count(c => c == '/')).ToList())
            {
                var prefix = directory.RelativePath + "/";
                var contents = diff.Entries.Where(entry => entry.RelativePath.StartsWith(prefix, comparison) && (onSource ? entry.Source : entry.Destination) is not null);
                var excluded = (onSource ? diff.ExcludedSource : diff.ExcludedDestination).Any(path => path.StartsWith(prefix, comparison));
                if (!excluded && contents.All(entry => deleted.Contains(entry.RelativePath))) continue;
                actions.Remove(directory);
                deleted.Remove(directory.RelativePath);
            }
        }
    }

    private static void Resolve(List<StorageSyncAction> actions, StorageDiffEntry entry, StorageSyncConflictKind kind, StorageSyncOptions options)
    {
        switch (options.ConflictPolicy)
        {
            case StorageSyncConflictPolicy.NewerWins:
                var a = StorageCompare.EffectiveModified(entry.Source!);
                var b = StorageCompare.EffectiveModified(entry.Destination!);
                if (a is { } left && b is { } right && (left - right).Duration() > options.Compare.TimeTolerance)
                {
                    actions.Add(Copy(entry, left > right ? StorageSyncActionKind.CopyToDestination : StorageSyncActionKind.CopyToSource, kind));
                    return;
                }
                break;
            case StorageSyncConflictPolicy.KeepBoth:
                var theirs = StorageSyncIdentity.Of(entry.Destination);
                var conflictPath = ConflictPath(entry.RelativePath, theirs!);
                actions.Add(new StorageSyncAction { RelativePath = entry.RelativePath, DestinationRelativePath = entry.DestinationRelativePath, Kind = StorageSyncActionKind.RenameAtDestination, TargetPath = conflictPath, Destination = theirs, Conflict = kind });
                actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination, kind) with { Destination = null });
                actions.Add(new StorageSyncAction { RelativePath = conflictPath, Kind = StorageSyncActionKind.CopyToSource, Conflict = kind, Bytes = entry.Destination?.Size, Destination = theirs, TargetPath = entry.RelativePath });
                return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = entry.RelativePath,
            DestinationRelativePath = entry.DestinationRelativePath,
            Kind = StorageSyncActionKind.Conflict,
            Reason = entry.Reasons,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = kind
        });
    }

    private static void ResolveDeleteVersusModify(List<StorageSyncAction> actions, StorageDiffEntry entry, bool deletedOnSource, StorageSyncOptions options)
    {
        if (options.ConflictPolicy is StorageSyncConflictPolicy.KeepBoth or StorageSyncConflictPolicy.NewerWins)
        {
            // Keep the edit: copy the modified file back to the side that deleted it.
            actions.Add(Copy(entry, deletedOnSource ? StorageSyncActionKind.CopyToSource : StorageSyncActionKind.CopyToDestination, StorageSyncConflictKind.DeleteVersusModify));
            return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = entry.RelativePath,
            DestinationRelativePath = entry.DestinationRelativePath,
            Kind = StorageSyncActionKind.Conflict,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = StorageSyncConflictKind.DeleteVersusModify
        });
    }

    /// <summary><c>name (conflict xxxxxxxx).ext</c>, from the kept version's identity so repeated runs pick the same name.</summary>
    internal static string ConflictPath(string path, StorageSyncIdentity identity, int attempt = 1)
    {
        var seed = $"{identity.Size}|{identity.Modified?.UtcTicks}|{identity.ETag}|{identity.VersionId}";
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..8];
        return ConflictResolverName(path, attempt == 1 ? $"conflict {tag}" : $"conflict {tag} {attempt}");
    }

    private static string ConflictResolverName(string path, string label)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        return $"{directory}{stem} ({label}){extension}";
    }

    private static string Key(string path, bool insensitive) => insensitive ? path.ToUpperInvariant() : path;

    /// <summary>Withholds every deletion when a limit or an empty side makes the run look unsafe.</summary>
    private static List<StorageSyncAction> ApplyDeletionSafety(
        List<StorageSyncAction> actions,
        StorageDiff diff,
        StorageSyncBaseline baseline,
        StorageTree source,
        StorageTree destination,
        StorageSyncOptions options,
        List<string> warnings)
    {
        var deletes = actions.Where(action => action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource).ToList();
        if (deletes.Count == 0) return actions;

        static int Files(StorageTree tree) => tree.Items.Values.Count(item => item.ItemType == StorageItemType.File);
        // Every file is its own step; a directory step only removes an emptied folder, so it counts for nothing.
        var destinationDeletes = deletes.Count(action => action.Kind == StorageSyncActionKind.DeleteFromDestination && action.Destination is not null);
        var sourceDeletes = deletes.Count(action => action.Kind == StorageSyncActionKind.DeleteFromSource && action.Source is not null);
        var sourceFiles = Files(source);
        var destinationFiles = Files(destination);

        string? reason = null;
        var baselineFiles = baseline.Entries.Values.Count(entry => !entry.IsDirectory);
        if (!options.AllowEmptySide)
        {
            if (sourceFiles == 0 && (destinationFiles > 0 || baselineFiles > 0))
                reason = "the source is unexpectedly empty";
            else if (destinationFiles == 0 && options.Direction == StorageSyncDirection.TwoWay && baselineFiles > 0)
                reason = "the destination is unexpectedly empty";
        }
        var total = destinationDeletes + sourceDeletes;
        if (reason is null && options.MaxDeletes is { } maxDeletes && total > maxDeletes)
            reason = $"{total} deletions exceed MaxDeletes ({maxDeletes})";
        if (reason is null && options.MaxDeletePercent is { } maxPercent)
        {
            var destinationShare = destinationFiles == 0 ? 0 : destinationDeletes * 100.0 / destinationFiles;
            var sourceShare = sourceFiles == 0 ? 0 : sourceDeletes * 100.0 / sourceFiles;
            var share = Math.Max(destinationShare, sourceShare);
            if (share > maxPercent)
                reason = $"deleting {share.ToString("0.#", CultureInfo.InvariantCulture)}% of a side exceeds MaxDeletePercent ({maxPercent.ToString(CultureInfo.InvariantCulture)}%)";
        }
        if (reason is null) return actions;
        warnings.Add($"All {deletes.Count} deletion(s) were withheld because {reason}.");
        return [.. actions.Select(action => action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource
            ? action with { WithheldReason = reason }
            : action)];
    }

    /// <summary>Directories first (shallowest first), then renames, copies, and deletions (deepest first).</summary>
    private static IReadOnlyList<StorageSyncAction> Order(List<StorageSyncAction> actions)
    {
        static int Rank(StorageSyncActionKind kind) => kind switch
        {
            StorageSyncActionKind.CreateDirectory or StorageSyncActionKind.CreateDirectoryAtSource => 0,
            StorageSyncActionKind.RenameAtDestination => 1,
            StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource => 2,
            StorageSyncActionKind.Conflict => 3,
            _ => 4
        };
        static int Depth(StorageSyncAction action) => action.RelativePath.Count(c => c == '/');
        return [.. actions
            .Select((action, index) => (action, index))
            .OrderBy(pair => Rank(pair.action.Kind))
            .ThenBy(pair => Rank(pair.action.Kind) switch { 0 => Depth(pair.action), 4 => -Depth(pair.action), _ => 0 })
            .ThenBy(pair => pair.index)
            .Select(pair => pair.action)];
    }

    // ---------------------------------------------------------------- applying

    /// <summary>Runs a plan's steps and records their outcomes, and what each applied step left on both sides.</summary>
    private sealed class SyncRun(IStorageService source, IStorageService destination, StorageSyncPlan plan, StorageSyncOptions options)
    {
        private readonly StorageSyncActionResult[] _results = [.. plan.Actions.Select(action => new StorageSyncActionResult(action,
            action.WithheldReason is not null ? StorageSyncActionOutcome.Withheld : StorageSyncActionOutcome.NotRun))];
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _renamed = new(StringComparer.Ordinal);

        public IReadOnlyList<StorageSyncActionResult> Results => _results;

        /// <summary>Paths an applied step brought into sync, with the versions it wrote or created.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, StorageSyncBaselineEntry> Synced { get; } = new(StringComparer.Ordinal);

        /// <summary>Paths an applied step deleted.</summary>
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Removed { get; } = new(StringComparer.Ordinal);

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var indexes = Enumerable.Range(0, plan.Actions.Count).ToList();
            var tolerance = options.Compare.TimeTolerance;

            // Directories and renames run in order; a rename must land before the copies that follow it.
            foreach (var i in indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.CreateDirectory or StorageSyncActionKind.CreateDirectoryAtSource or StorageSyncActionKind.RenameAtDestination))
            {
                if (stop.IsCancellationRequested) break;
                await RunAsync(i, stop, tolerance).ConfigureAwait(false);
            }
            await Parallel.ForEachAsync(
                indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource),
                new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency },
                async (i, _) =>
                {
                    if (!stop.IsCancellationRequested) await RunAsync(i, stop, tolerance).ConfigureAwait(false);
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // A failed step means the two sides do not match yet, so deleting could remove the only good copy.
            var failed = _results.Count(result => result.Outcome is StorageSyncActionOutcome.Failed or StorageSyncActionOutcome.Stale);
            foreach (var i in indexes.Where(i => plan.Actions[i].Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource))
            {
                if (stop.IsCancellationRequested) break;
                if (_results[i].Outcome == StorageSyncActionOutcome.Withheld) continue;
                if (failed > 0)
                {
                    _results[i] = _results[i] with
                    {
                        Outcome = StorageSyncActionOutcome.Withheld,
                        Action = plan.Actions[i] with { WithheldReason = $"{failed} other step(s) failed in this run" }
                    };
                    continue;
                }
                await RunAsync(i, stop, tolerance).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task RunAsync(int index, CancellationTokenSource stop, TimeSpan tolerance)
        {
            var action = plan.Actions[index];
            var attempts = 0;
            Result result;
            try
            {
                while (true)
                {
                    attempts++;
                    result = await StepAsync(action, tolerance, stop.Token).ConfigureAwait(false);
                    if (result.IsSuccess || !StorageErrorInfo.IsTransient(result.Error) || attempts > options.ItemRetries || stop.IsCancellationRequested)
                        break;
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempts - 1)), stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped by the caller, or by another step failing with ContinueOnError off: this step stays NotRun.
                return;
            }
            var outcome = result.IsSuccess
                ? StorageSyncActionOutcome.Applied
                : IsStale(result.Error) ? StorageSyncActionOutcome.Stale : StorageSyncActionOutcome.Failed;
            _results[index] = new StorageSyncActionResult(action, outcome, result.Error, attempts);
            if (outcome != StorageSyncActionOutcome.Applied && !options.ContinueOnError)
                await stop.CancelAsync().ConfigureAwait(false);
        }

        private string SourcePath(string relative) => Join(plan.SourceRoot, relative);
        private string DestinationPath(StorageSyncAction action) => Join(plan.DestinationRoot, action.DestinationRelativePath ?? action.RelativePath);
        private string DestinationPath(string relative) => Join(plan.DestinationRoot, relative);

        private async Task<Result> StepAsync(StorageSyncAction action, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case StorageSyncActionKind.CreateDirectory:
                {
                    var created = await destination.CreateDirectoryAsync(DestinationPath(action), cancellationToken).ConfigureAwait(false);
                    if (created.IsSuccess) Synced[action.RelativePath] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
                    return created;
                }
                case StorageSyncActionKind.CreateDirectoryAtSource:
                {
                    var created = await source.CreateDirectoryAsync(SourcePath(action.RelativePath), cancellationToken).ConfigureAwait(false);
                    if (created.IsSuccess) Synced[action.RelativePath] = new StorageSyncBaselineEntry(null, null, IsDirectory: true);
                    return created;
                }
                case StorageSyncActionKind.RenameAtDestination:
                    return await RenameAsync(action, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToDestination:
                    return await CopyAsync(source, SourcePath(action.RelativePath), action.Source, destination, DestinationPath(action), action.Destination,
                        checkFrom: true, towardDestination: true, action.RelativePath, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToSource:
                {
                    // A kept conflict copy was renamed in this run (possibly to a numbered name), which may also give
                    // it a new identity on object stores.
                    var chained = action.Conflict is not null && action.TargetPath is not null;
                    var relative = chained ? _renamed.GetValueOrDefault(action.RelativePath, action.RelativePath) : action.RelativePath;
                    var from = chained ? DestinationPath(relative) : DestinationPath(action);
                    return await CopyAsync(destination, from, action.Destination, source, SourcePath(relative), chained ? null : action.Source,
                        checkFrom: !chained, towardDestination: false, relative, tolerance, cancellationToken).ConfigureAwait(false);
                }
                case StorageSyncActionKind.DeleteFromDestination:
                {
                    var deleted = await DeleteAsync(destination, DestinationPath(action), action.Destination, tolerance, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsSuccess) Removed[action.RelativePath] = true;
                    return deleted;
                }
                case StorageSyncActionKind.DeleteFromSource:
                {
                    var deleted = await DeleteAsync(source, SourcePath(action.RelativePath), action.Source, tolerance, cancellationToken).ConfigureAwait(false);
                    if (deleted.IsSuccess) Removed[action.RelativePath] = true;
                    return deleted;
                }
                default:
                    return Result.Success();
            }
        }

        /// <summary>
        /// Moves the destination's version aside as a kept conflict copy. The planned name is taken when it is free
        /// on both sides; otherwise a numbered one, so an old conflict copy never blocks the run.
        /// </summary>
        private async Task<Result> RenameAsync(StorageSyncAction action, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            var current = await Current(destination, DestinationPath(action), cancellationToken).ConfigureAwait(false);
            if (!action.Destination!.Matches(current, tolerance)) return Stale(action.RelativePath);
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                var target = attempt == 1 ? action.TargetPath! : ConflictPath(action.RelativePath, action.Destination, attempt);
                if (await Current(destination, DestinationPath(target), cancellationToken).ConfigureAwait(false) is not null ||
                    await Current(source, SourcePath(target), cancellationToken).ConfigureAwait(false) is not null)
                    continue;
                var moved = await destination.MoveAsync(DestinationPath(action), DestinationPath(target),
                    new StorageTransferOptions { Overwrite = false }, cancellationToken).ConfigureAwait(false);
                if (moved.IsSuccess) _renamed[action.TargetPath!] = target;
                return moved;
            }
            return Result.Failure(StorageErrors.Conflict($"No free name was found for the conflict copy of '{action.RelativePath}'."));
        }

        /// <summary>Copies one file through a staged, conditional (and optionally verified) write, and records the pair.</summary>
        private async Task<Result> CopyAsync(
            IStorageService from,
            string fromPath,
            StorageSyncIdentity? planned,
            IStorageService to,
            string toPath,
            StorageSyncIdentity? plannedTarget,
            bool checkFrom,
            bool towardDestination,
            string relative,
            TimeSpan tolerance,
            CancellationToken cancellationToken)
        {
            var fromItem = await Current(from, fromPath, cancellationToken).ConfigureAwait(false);
            if (fromItem is null || fromItem.ItemType != StorageItemType.File || (checkFrom && planned is not null && !planned.Matches(fromItem, tolerance)))
                return Stale(fromPath);
            var target = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
            if (plannedTarget is null ? target is not null : !plannedTarget.Matches(target, tolerance))
                return Stale(toPath);

            var modified = StorageCompare.EffectiveModified(fromItem);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            var keepTimeInMetadata = options.PreserveTimestamps && modified is not null &&
                !to.Capabilities.Supports(StorageFeature.SetTimestamps) && to.Capabilities.Supports(StorageFeature.MetadataWrite);
            if (keepTimeInMetadata)
                metadata[StorageCompareOptions.ModifiedMetadataKey] = modified!.Value.ToString("O", CultureInfo.InvariantCulture);

            var written = await StagedWriter.WriteAsync(
                to,
                new StagedWriteRequest
                {
                    Path = toPath,
                    Upload = new StorageUploadOptions
                    {
                        CreateParents = true,
                        ContentType = fromItem.ContentType,
                        Metadata = metadata,
                        Progress = options.Progress is { } progress ? new PathProgress(progress, fromPath) : null
                    },
                    ExpectedLength = fromItem.Size,
                    Verify = options.Verify
                },
                (offset, token) => from.DownloadAsync(fromPath, new StorageDownloadOptions { Offset = offset, VersionId = fromItem.VersionId }, token),
                cancellationToken).ConfigureAwait(false);
            if (written.Cancelled) cancellationToken.ThrowIfCancellationRequested();
            if (!written.IsSuccess) return Result.Failure(written.Error!);

            // Without a version the source is read again: it must not have changed while it streamed.
            if (fromItem.VersionId is null && fromItem.ETag is not null)
            {
                var after = await Current(from, fromPath, cancellationToken).ConfigureAwait(false);
                if (after is null || !StagedWriter.SameETag(fromItem.ETag, after.ETag))
                {
                    await StagedWriter.DeleteAsync(to, written.Content!.StagingPath).ConfigureAwait(false);
                    return Stale(fromPath);
                }
            }
            // The target must still be what was planned right before it is replaced.
            if (plannedTarget is not null)
            {
                var again = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
                if (!plannedTarget.Matches(again, tolerance))
                {
                    await StagedWriter.DeleteAsync(to, written.Content!.StagingPath).ConfigureAwait(false);
                    return Stale(toPath);
                }
            }
            Result promoted;
            try
            {
                (promoted, _) = await StagedWriter.PromoteAsync(to, written.Content!.StagingPath, toPath, overwrite: plannedTarget is not null, condition: null, createParents: true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StagedWriter.DeleteAsync(to, written.Content!.StagingPath).ConfigureAwait(false);
                throw;
            }
            if (promoted.IsFailure)
            {
                await StagedWriter.DeleteAsync(to, written.Content.StagingPath).ConfigureAwait(false);
                return promoted;
            }
            if (options.PreserveTimestamps && modified is not null && to.Capabilities.Supports(StorageFeature.SetTimestamps))
                // Best effort: a failure only means the next comparison relies on size and "source newer".
                _ = await to.SetTimestampsAsync(toPath, modified, cancellationToken: cancellationToken).ConfigureAwait(false);

            // The pair the baseline records: the version read, and the version written (if it is still ours).
            var result = await Current(to, toPath, cancellationToken).ConfigureAwait(false);
            if (result is not null && result.Size == fromItem.Size)
            {
                var read = StorageSyncIdentity.Of(fromItem);
                var wrote = StorageSyncIdentity.Of(result);
                Synced[relative] = towardDestination ? new StorageSyncBaselineEntry(read, wrote) : new StorageSyncBaselineEntry(wrote, read);
            }
            return Result.Success();
        }

        /// <summary>
        /// Deletes a file while it is the planned version, or a directory only while it is empty: anything that
        /// appeared in it after the plan keeps it, and the step is reported stale.
        /// </summary>
        private static async Task<Result> DeleteAsync(IStorageService storage, string path, StorageSyncIdentity? planned, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            var current = await Current(storage, path, cancellationToken).ConfigureAwait(false);
            if (current is null) return Result.Success();
            if (current.ItemType == StorageItemType.Directory)
            {
                var listed = await storage.ListAsync(path, new StorageListOptions { PageSize = 1, IncludeInternal = true, IncludeHidden = true }, cancellationToken).ConfigureAwait(false);
                if (listed.IsFailure) return Result.Failure(listed.Error!);
                if (listed.Value!.Items.Count > 0) return Stale(path);
                return await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
            }
            if (planned is not null && !planned.Matches(current, tolerance))
                return Stale(path);
            var condition = planned is not null && storage.Capabilities.Supports(StorageFeature.ConditionalDelete) && (planned.ETag is not null || planned.VersionId is not null)
                ? new StorageMutationCondition { ExpectedETag = planned.ETag, ExpectedVersionId = planned.VersionId }
                : null;
            return await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = false, IgnoreMissing = true, Condition = condition }, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<StorageItem?> Current(IStorageService storage, string path, CancellationToken cancellationToken)
        {
            var info = await storage.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            return info.IsSuccess ? info.Value : null;
        }

        private static Result Stale(string path) =>
            Result.Failure(StorageErrors.Conflict($"'{path}' changed after the plan was made; the step was not taken.", $"{StaleKey}=true"));

        private static bool IsStale(Error? error) => error is not null && StorageErrorInfo.TryGetDetail(error, StaleKey, out _);
    }

    /// <summary>Details key marking a step skipped because its item changed after planning.</summary>
    private const string StaleKey = "stale";

    // ---------------------------------------------------------------- baseline

    /// <summary>
    /// Records what the run knows to be in sync: the pairs both sides agreed on when the plan was made, and the
    /// pairs the run's own steps produced. A path whose step did not complete keeps its previous entry, so the
    /// next run sees it again. Nothing is taken from a listing made after the run, so an edit made meanwhile is
    /// not mistaken for the synced version.
    /// </summary>
    private static async Task<bool> SaveBaselineAsync(StorageSyncPlan plan, SyncRun run, StorageSyncOptions options, CancellationToken cancellationToken)
    {
        var store = options.StateStore!;
        var previous = await store.LoadAsync(options.SyncId!, cancellationToken).ConfigureAwait(false) ?? StorageSyncBaseline.Empty;
        var entries = new Dictionary<string, StorageSyncBaselineEntry>(plan.Agreed, StringComparer.Ordinal);
        foreach (var result in run.Results.Where(result => result.Outcome != StorageSyncActionOutcome.Applied || result.Action.Kind == StorageSyncActionKind.Conflict))
        {
            foreach (var path in new[] { result.Action.RelativePath, result.Action.TargetPath }.Where(path => path is not null).Cast<string>())
            {
                if (previous.Entries.TryGetValue(path, out var kept)) entries[path] = kept;
                else entries.Remove(path);
            }
        }
        foreach (var path in run.Removed.Keys) entries.Remove(path);
        foreach (var (path, entry) in run.Synced) entries[path] = entry;
        return await store.SaveAsync(options.SyncId!, new StorageSyncBaseline(previous.Generation + 1, entries), plan.BaselineGeneration, cancellationToken).ConfigureAwait(false);
    }

    private static string Join(string root, string relative) =>
        root.Length == 0 ? relative : $"{root}/{relative}";

    private sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
    }
}
