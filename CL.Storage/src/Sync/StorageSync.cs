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
        var (actions, unchanged) = options.Direction == StorageSyncDirection.TwoWay
            ? PlanTwoWay(diff.Value!, baseline, options, warnings)
            : PlanOneWay(diff.Value!, options, warnings);
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
            saved = await SaveBaselineAsync(source, destination, plan, run, options, CancellationToken.None).ConfigureAwait(false);
        return Result<StorageSyncReport>.Success(new StorageSyncReport
        {
            Plan = plan,
            Results = run.Results,
            Cancelled = cancelled,
            BaselineSaved = saved
        });
    }

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
        var recursivelyDeleted = new List<string>();
        foreach (var entry in diff.Entries)
        {
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
                    if (recursivelyDeleted.Any(root => entry.RelativePath.StartsWith(root + "/", StringComparison.Ordinal))) break;
                    if (entry.IsDirectory)
                    {
                        // A folder holding items the filters left out keeps them: only its included items go.
                        if (diff.ExcludedDestination.Any(path => path.StartsWith(entry.RelativePath + "/", StringComparison.Ordinal))) break;
                        recursivelyDeleted.Add(entry.RelativePath);
                    }
                    actions.Add(new StorageSyncAction
                    {
                        RelativePath = entry.RelativePath,
                        Kind = StorageSyncActionKind.DeleteFromDestination,
                        Destination = StorageSyncIdentity.Of(entry.Destination)
                    });
                    break;
                case StorageDiffKind.Different when entry.Reasons.HasFlag(StorageDiffReason.Type):
                    warnings.Add($"'{entry.RelativePath}' is a file on one side and a directory on the other; it was left alone.");
                    break;
                case StorageDiffKind.Different when !entry.IsDirectory:
                    // Update and mirror never copy an older source over a newer destination unless the content differs too.
                    if (entry.Reasons == StorageDiffReason.DestinationNewer) break;
                    actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination));
                    break;
            }
        }
        return (actions, unchanged);
    }

    private static StorageSyncAction Copy(StorageDiffEntry entry, StorageSyncActionKind kind, StorageSyncConflictKind? conflict = null) => new()
    {
        RelativePath = entry.RelativePath,
        Kind = kind,
        Reason = entry.Reasons,
        Bytes = kind == StorageSyncActionKind.CopyToDestination ? entry.Source?.Size : entry.Destination?.Size,
        Source = StorageSyncIdentity.Of(entry.Source),
        Destination = StorageSyncIdentity.Of(entry.Destination),
        Conflict = conflict
    };

    private enum Change { Absent, Unchanged, Created, Modified, Deleted }

    private static Change Classify(StorageSyncIdentity? before, StorageItem? now, TimeSpan tolerance) =>
        (before, now) switch
        {
            (null, null) => Change.Absent,
            (null, _) => Change.Created,
            (_, null) => Change.Deleted,
            _ => before.Matches(now, tolerance) ? Change.Unchanged : Change.Modified
        };

    private static (List<StorageSyncAction> Actions, int Unchanged) PlanTwoWay(StorageDiff diff, StorageSyncBaseline baseline, StorageSyncOptions options, List<string> warnings)
    {
        var tolerance = options.Compare.TimeTolerance;
        var actions = new List<StorageSyncAction>();
        var unchanged = 0;
        var hasBaseline = options.StateStore is not null;
        var baselineByKey = baseline.Entries.ToDictionary(pair => Key(pair.Key, diff.CaseInsensitive), pair => (Path: pair.Key, Entry: pair.Value), StringComparer.Ordinal);
        var entriesByKey = diff.Entries.Where(entry => !entry.IsDirectory).ToDictionary(entry => Key(entry.RelativePath, diff.CaseInsensitive), StringComparer.Ordinal);

        foreach (var key in entriesByKey.Keys.Union(baselineByKey.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            entriesByKey.TryGetValue(key, out var entry);
            var base_ = baselineByKey.TryGetValue(key, out var b) ? b.Entry : null;
            var path = entry?.RelativePath ?? b.Path;
            if (entry is { Reasons: var r } && r.HasFlag(StorageDiffReason.Type))
            {
                warnings.Add($"'{path}' is a file on one side and a directory on the other; it was left alone.");
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
                    if (entry is not null) unchanged++;
                    break;
                case (Change.Created or Change.Modified, Change.Unchanged or Change.Absent):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToDestination));
                    break;
                case (Change.Unchanged or Change.Absent, Change.Created or Change.Modified):
                    actions.Add(Copy(entry!, StorageSyncActionKind.CopyToSource));
                    break;
                case (Change.Deleted, Change.Unchanged):
                    actions.Add(options.PropagateDeletes
                        ? new StorageSyncAction { RelativePath = path, Kind = StorageSyncActionKind.DeleteFromDestination, Destination = StorageSyncIdentity.Of(entry!.Destination) }
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
                    if (entry!.Kind == StorageDiffKind.Same) { unchanged++; break; }
                    Resolve(actions, entry, left == Change.Modified && right == Change.Modified ? StorageSyncConflictKind.BothModified : StorageSyncConflictKind.BothCreated, options);
                    break;
                case (Change.Deleted, Change.Created or Change.Modified):
                case (Change.Created or Change.Modified, Change.Deleted):
                    ResolveDeleteVersusModify(actions, entry!, deletedOnSource: left == Change.Deleted, options);
                    break;
                default:
                    if (entry is not null && entry.Kind == StorageDiffKind.Same) unchanged++;
                    break;
            }
        }
        return (actions, unchanged);
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
                var conflictPath = ConflictPath(entry.RelativePath, StorageSyncIdentity.Of(entry.Destination)!);
                var theirs = StorageSyncIdentity.Of(entry.Destination);
                actions.Add(new StorageSyncAction { RelativePath = entry.RelativePath, Kind = StorageSyncActionKind.RenameAtDestination, TargetPath = conflictPath, Destination = theirs, Conflict = kind });
                actions.Add(Copy(entry, StorageSyncActionKind.CopyToDestination, kind) with { Destination = null });
                actions.Add(new StorageSyncAction { RelativePath = conflictPath, Kind = StorageSyncActionKind.CopyToSource, Conflict = kind, Bytes = entry.Destination?.Size, TargetPath = entry.RelativePath });
                return;
        }
        actions.Add(new StorageSyncAction
        {
            RelativePath = entry.RelativePath,
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
            Kind = StorageSyncActionKind.Conflict,
            Source = StorageSyncIdentity.Of(entry.Source),
            Destination = StorageSyncIdentity.Of(entry.Destination),
            Conflict = StorageSyncConflictKind.DeleteVersusModify
        });
    }

    /// <summary><c>name (conflict xxxxxxxx).ext</c>, from the kept version's identity so repeated runs pick the same name.</summary>
    internal static string ConflictPath(string path, StorageSyncIdentity identity)
    {
        var seed = $"{identity.Size}|{identity.Modified?.UtcTicks}|{identity.ETag}|{identity.VersionId}";
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..8];
        return ConflictResolverName(path, $"conflict {tag}");
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
        int FilesUnder(StorageTree tree, string relative) =>
            tree.Items.TryGetValue(relative, out var item) && item.ItemType == StorageItemType.Directory
                ? tree.Items.Count(pair => pair.Value.ItemType == StorageItemType.File && pair.Key.StartsWith(relative + "/", StringComparison.Ordinal))
                : 1;
        var destinationDeletes = deletes.Where(action => action.Kind == StorageSyncActionKind.DeleteFromDestination).Sum(action => FilesUnder(destination, action.RelativePath));
        var sourceDeletes = deletes.Where(action => action.Kind == StorageSyncActionKind.DeleteFromSource).Sum(action => FilesUnder(source, action.RelativePath));
        var sourceFiles = Files(source);
        var destinationFiles = Files(destination);

        string? reason = null;
        if (!options.AllowEmptySide)
        {
            if (sourceFiles == 0 && (destinationFiles > 0 || baseline.Entries.Count > 0))
                reason = "the source is unexpectedly empty";
            else if (destinationFiles == 0 && options.Direction == StorageSyncDirection.TwoWay && baseline.Entries.Count > 0)
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

    /// <summary>Directories first, then renames, copies, and deletions (deepest first).</summary>
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
        return [.. actions
            .Select((action, index) => (action, index))
            .OrderBy(pair => Rank(pair.action.Kind))
            .ThenBy(pair => Rank(pair.action.Kind) == 4 ? -pair.action.RelativePath.Count(c => c == '/') : 0)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.action)];
    }

    // ---------------------------------------------------------------- applying

    /// <summary>Runs a plan's steps and records their outcomes.</summary>
    private sealed class SyncRun(IStorageService source, IStorageService destination, StorageSyncPlan plan, StorageSyncOptions options)
    {
        private readonly StorageSyncActionResult[] _results = [.. plan.Actions.Select(action => new StorageSyncActionResult(action,
            action.WithheldReason is not null ? StorageSyncActionOutcome.Withheld : StorageSyncActionOutcome.NotRun))];

        public IReadOnlyList<StorageSyncActionResult> Results => _results;

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
            while (true)
            {
                attempts++;
                result = await StepAsync(action, tolerance, stop.Token).ConfigureAwait(false);
                if (result.IsSuccess || !StorageErrorInfo.IsTransient(result.Error) || attempts > options.ItemRetries || stop.IsCancellationRequested)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempts - 1)), stop.Token).ConfigureAwait(false);
            }
            var outcome = result.IsSuccess
                ? StorageSyncActionOutcome.Applied
                : IsStale(result.Error) ? StorageSyncActionOutcome.Stale : StorageSyncActionOutcome.Failed;
            _results[index] = new StorageSyncActionResult(action, outcome, result.Error, attempts);
            if (outcome != StorageSyncActionOutcome.Applied && !options.ContinueOnError)
                await stop.CancelAsync().ConfigureAwait(false);
        }

        private string SourcePath(string relative) => Join(plan.SourceRoot, relative);
        private string DestinationPath(string relative) => Join(plan.DestinationRoot, relative);

        private async Task<Result> StepAsync(StorageSyncAction action, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            switch (action.Kind)
            {
                case StorageSyncActionKind.CreateDirectory:
                    return await destination.CreateDirectoryAsync(DestinationPath(action.RelativePath), cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CreateDirectoryAtSource:
                    return await source.CreateDirectoryAsync(SourcePath(action.RelativePath), cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.RenameAtDestination:
                {
                    var current = await Current(destination, DestinationPath(action.RelativePath), cancellationToken).ConfigureAwait(false);
                    if (!action.Destination!.Matches(current, tolerance)) return Stale(action.RelativePath);
                    return await destination.MoveAsync(DestinationPath(action.RelativePath), DestinationPath(action.TargetPath!),
                        new StorageTransferOptions { Overwrite = false }, cancellationToken).ConfigureAwait(false);
                }
                case StorageSyncActionKind.CopyToDestination:
                    return await CopyAsync(source, SourcePath(action.RelativePath), action.Source, destination, DestinationPath(action.RelativePath), action.Destination, checkFrom: true, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.CopyToSource:
                    // A kept conflict copy was renamed in this run, which may give it a new identity on object stores.
                    var chained = action.Conflict is not null && action.TargetPath is not null;
                    return await CopyAsync(destination, DestinationPath(action.RelativePath), action.Destination, source, SourcePath(action.RelativePath), action.Source, checkFrom: !chained, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.DeleteFromDestination:
                    return await DeleteAsync(destination, DestinationPath(action.RelativePath), action.Destination, tolerance, cancellationToken).ConfigureAwait(false);
                case StorageSyncActionKind.DeleteFromSource:
                    return await DeleteAsync(source, SourcePath(action.RelativePath), action.Source, tolerance, cancellationToken).ConfigureAwait(false);
                default:
                    return Result.Success();
            }
        }

        /// <summary>Copies one file through a staged, conditional (and optionally verified) write.</summary>
        private async Task<Result> CopyAsync(
            IStorageService from,
            string fromPath,
            StorageSyncIdentity? planned,
            IStorageService to,
            string toPath,
            StorageSyncIdentity? plannedTarget,
            bool checkFrom,
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
            var (promoted, _) = await StagedWriter.PromoteAsync(to, written.Content!.StagingPath, toPath, overwrite: plannedTarget is not null, condition: null, createParents: true, cancellationToken).ConfigureAwait(false);
            if (promoted.IsFailure)
            {
                await StagedWriter.DeleteAsync(to, written.Content.StagingPath).ConfigureAwait(false);
                return promoted;
            }
            if (options.PreserveTimestamps && modified is not null && to.Capabilities.Supports(StorageFeature.SetTimestamps))
                // Best effort: a failure only means the next comparison relies on size and "source newer".
                _ = await to.SetTimestampsAsync(toPath, modified, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }

        private static async Task<Result> DeleteAsync(IStorageService storage, string path, StorageSyncIdentity? planned, TimeSpan tolerance, CancellationToken cancellationToken)
        {
            var current = await Current(storage, path, cancellationToken).ConfigureAwait(false);
            if (current is null) return Result.Success();
            var isDirectory = current.ItemType == StorageItemType.Directory;
            if (!isDirectory && planned is not null && !planned.Matches(current, tolerance))
                return Stale(path);
            var condition = !isDirectory && planned is not null && storage.Capabilities.Supports(StorageFeature.ConditionalDelete) && (planned.ETag is not null || planned.VersionId is not null)
                ? new StorageMutationCondition { ExpectedETag = planned.ETag, ExpectedVersionId = planned.VersionId }
                : null;
            return await storage.DeleteAsync(path, new StorageDeleteOptions { Recursive = isDirectory, IgnoreMissing = true, Condition = condition }, cancellationToken).ConfigureAwait(false);
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
    /// Records what both sides hold now: every path present on both sides, plus the old entry of any path
    /// whose step did not complete (a conflict, a failure, a withheld deletion), so the next run sees it again.
    /// </summary>
    private static async Task<bool> SaveBaselineAsync(
        IStorageService source,
        IStorageService destination,
        StorageSyncPlan plan,
        SyncRun run,
        StorageSyncOptions options,
        CancellationToken cancellationToken)
    {
        var store = options.StateStore!;
        var previous = await store.LoadAsync(options.SyncId!, cancellationToken).ConfigureAwait(false) ?? StorageSyncBaseline.Empty;
        var filter = new PathFilter(options.Compare);
        var left = await StorageCompare.ListTreeAsync(source, plan.SourceRoot, options.Compare, filter, required: true, cancellationToken).ConfigureAwait(false);
        var right = await StorageCompare.ListTreeAsync(destination, plan.DestinationRoot, options.Compare, filter, required: false, cancellationToken).ConfigureAwait(false);
        if (left.IsFailure || right.IsFailure) return false;

        var unresolved = run.Results
            .Where(result => result.Outcome != StorageSyncActionOutcome.Applied || result.Action.Kind == StorageSyncActionKind.Conflict)
            .SelectMany(result => new[] { result.Action.RelativePath, result.Action.TargetPath }.Where(path => path is not null).Cast<string>())
            .ToHashSet(StringComparer.Ordinal);
        var entries = new Dictionary<string, StorageSyncBaselineEntry>(StringComparer.Ordinal);
        foreach (var path in left.Value!.Items.Keys.Union(right.Value!.Items.Keys, StringComparer.Ordinal))
        {
            if (unresolved.Contains(path))
            {
                if (previous.Entries.TryGetValue(path, out var kept)) entries[path] = kept;
                continue;
            }
            left.Value.Items.TryGetValue(path, out var a);
            right.Value.Items.TryGetValue(path, out var b);
            if (a is { ItemType: StorageItemType.File } && b is { ItemType: StorageItemType.File })
                entries[path] = new StorageSyncBaselineEntry(StorageSyncIdentity.Of(a), StorageSyncIdentity.Of(b));
            else if (previous.Entries.TryGetValue(path, out var old) && (a is not null || b is not null))
                entries[path] = old;
        }
        foreach (var path in unresolved.Where(path => !entries.ContainsKey(path) && previous.Entries.ContainsKey(path)))
            entries[path] = previous.Entries[path];
        return await store.SaveAsync(options.SyncId!, new StorageSyncBaseline(previous.Generation + 1, entries), plan.BaselineGeneration, cancellationToken).ConfigureAwait(false);
    }

    private static string Join(string root, string relative) =>
        root.Length == 0 ? relative : $"{root}/{relative}";

    private sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
    }
}
