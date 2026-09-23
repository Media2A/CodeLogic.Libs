using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Sync;

/// <summary>Which way a sync makes changes.</summary>
public enum StorageSyncDirection
{
    /// <summary>Copies files that are new or changed at the source; never deletes.</summary>
    Update = 0,
    /// <summary>Makes the destination match the source; deletes destination-only items when <see cref="StorageSyncOptions.DeleteExtraneous"/> is set.</summary>
    Mirror = 1,
    /// <summary>
    /// Changes flow both ways. With a baseline (<see cref="StorageSyncOptions.StateStore"/>) it is a three-way sync:
    /// edits and deletions made on one side are carried to the other, and changes on both sides are conflicts.
    /// Without one, files missing on a side are copied there and files that differ are conflicts.
    /// </summary>
    TwoWay = 2
}

/// <summary>What a two-way sync does when both sides changed the same file.</summary>
public enum StorageSyncConflictPolicy
{
    /// <summary>Plans a <see cref="StorageSyncActionKind.Conflict"/>; the plan cannot be applied until it is resolved.</summary>
    Block = 0,
    /// <summary>
    /// Keeps both: the source's version keeps the name on both sides, and the destination's version is kept on
    /// both sides as <c>name (conflict xxxxxxxx).ext</c>, named from its identity so repeated runs agree. For
    /// a delete against a modify, the modified file is kept.
    /// </summary>
    KeepBoth = 1,
    /// <summary>The later modification wins; equal or unknown times stay conflicts. A modify beats a delete.</summary>
    NewerWins = 2
}

/// <summary>How the two sides of a two-way sync changed one path.</summary>
public enum StorageSyncConflictKind
{
    /// <summary>Both sides edited a file they shared.</summary>
    BothModified = 0,
    /// <summary>Both sides created different files at the same path.</summary>
    BothCreated = 1,
    /// <summary>One side deleted a file the other side edited.</summary>
    DeleteVersusModify = 2
}

/// <summary>What a sync does to one path. The numbers are stable: kinds are only ever added at the end.</summary>
public enum StorageSyncActionKind
{
    /// <summary>Copies from source to destination.</summary>
    CopyToDestination = 0,
    /// <summary>Copies from destination to source.</summary>
    CopyToSource = 1,
    /// <summary>Deletes a file, or an empty directory, from the destination.</summary>
    DeleteFromDestination = 2,
    /// <summary>Creates a directory at the destination.</summary>
    CreateDirectory = 3,
    /// <summary>Deletes a file, or an empty directory, from the source (a two-way sync carrying a destination-side deletion).</summary>
    DeleteFromSource = 4,
    /// <summary>Creates a directory at the source.</summary>
    CreateDirectoryAtSource = 5,
    /// <summary>Renames the destination's version to <see cref="StorageSyncAction.TargetPath"/> (a kept conflict copy).</summary>
    RenameAtDestination = 6,
    /// <summary>A conflict left for a person; nothing is changed.</summary>
    Conflict = 7
}

/// <summary>How one planned step turned out.</summary>
public enum StorageSyncActionOutcome
{
    /// <summary>Not run: a dry run, a blocked conflict, or the run stopped first.</summary>
    NotRun = 0,
    /// <summary>Done.</summary>
    Applied = 1,
    /// <summary>Failed; see the error.</summary>
    Failed = 2,
    /// <summary>The item changed after the plan was made, so the step was not taken.</summary>
    Stale = 3,
    /// <summary>A deletion held back by a safety rule; see <see cref="StorageSyncAction.WithheldReason"/>.</summary>
    Withheld = 4
}

/// <summary>What identifies one version of a file: size, time, ETag, version, and a digest when known.</summary>
/// <param name="Size">Length in bytes.</param>
/// <param name="Modified">Modification time (the source time kept in metadata on stores that cannot set times).</param>
/// <param name="ETag">Provider entity tag.</param>
/// <param name="VersionId">Provider version.</param>
/// <param name="Sha256">Content digest, when computed.</param>
public sealed record StorageSyncIdentity(long? Size, DateTimeOffset? Modified, string? ETag, string? VersionId, string? Sha256 = null)
{
    internal static StorageSyncIdentity? Of(StorageItem? item) =>
        item is null || item.ItemType == StorageItemType.Directory
            ? null
            : new StorageSyncIdentity(item.Size, StorageCompare.EffectiveModified(item), item.ETag, item.VersionId);

    /// <summary>Whether an item is still this version: by version, else ETag, else size and time.</summary>
    internal bool Matches(StorageItem? item, TimeSpan tolerance)
    {
        if (item is null || item.ItemType == StorageItemType.Directory) return false;
        if (VersionId is not null && item.VersionId is not null) return VersionId == item.VersionId;
        if (ETag is not null && item.ETag is not null) return Registry.StagedWriter.SameETag(ETag, item.ETag);
        var modified = StorageCompare.EffectiveModified(item);
        return Size == item.Size &&
               (Modified is null || modified is null || (Modified.Value - modified.Value).Duration() <= tolerance);
    }

    /// <summary>Whether two identities describe the same version.</summary>
    internal bool SameVersionAs(StorageSyncIdentity? other, TimeSpan tolerance)
    {
        if (other is null) return false;
        if (VersionId is not null && other.VersionId is not null) return VersionId == other.VersionId;
        if (ETag is not null && other.ETag is not null) return Registry.StagedWriter.SameETag(ETag, other.ETag);
        return Size == other.Size && (Modified is null || other.Modified is null || (Modified.Value - other.Modified.Value).Duration() <= tolerance);
    }
}

/// <summary>What the last completed sync saw on each side of one path.</summary>
/// <param name="Source">The source's version of a file, or null when absent there (or a directory).</param>
/// <param name="Destination">The destination's version of a file, or null when absent there (or a directory).</param>
/// <param name="IsDirectory">Whether the path was a directory on both sides.</param>
public sealed record StorageSyncBaselineEntry(StorageSyncIdentity? Source, StorageSyncIdentity? Destination, bool IsDirectory = false);

/// <summary>The state after the last sync: per path, the version each side had. Its generation grows with every save.</summary>
/// <param name="Generation">Grows by one with every saved run; a plan made against an older generation is refused.</param>
/// <param name="Entries">Per-path identities.</param>
public sealed record StorageSyncBaseline(long Generation, IReadOnlyDictionary<string, StorageSyncBaselineEntry> Entries)
{
    /// <summary>An empty baseline, for a first run.</summary>
    public static StorageSyncBaseline Empty { get; } = new(0, new Dictionary<string, StorageSyncBaselineEntry>());
}

/// <summary>Keeps sync baselines between runs; implement it over a database to make two-way sync durable.</summary>
public interface IStorageSyncStateStore
{
    /// <summary>Returns the baseline for a sync, or null before its first run.</summary>
    Task<StorageSyncBaseline?> LoadAsync(string syncId, CancellationToken cancellationToken);

    /// <summary>Saves a baseline if the stored one still has <paramref name="expectedGeneration"/>.</summary>
    /// <returns><see langword="false"/> when another run saved in between.</returns>
    Task<bool> SaveAsync(string syncId, StorageSyncBaseline baseline, long expectedGeneration, CancellationToken cancellationToken);
}

/// <summary>Keeps baselines in memory.</summary>
public sealed class InMemoryStorageSyncStateStore : IStorageSyncStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StorageSyncBaseline> _baselines = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<StorageSyncBaseline?> LoadAsync(string syncId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_baselines.GetValueOrDefault(syncId));
    }

    /// <inheritdoc />
    public Task<bool> SaveAsync(string syncId, StorageSyncBaseline baseline, long expectedGeneration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var current = _baselines.GetValueOrDefault(syncId)?.Generation ?? 0;
            if (current != expectedGeneration) return Task.FromResult(false);
            _baselines[syncId] = baseline;
            return Task.FromResult(true);
        }
    }
}

/// <summary>Controls a directory sync.</summary>
public sealed record StorageSyncOptions
{
    /// <summary>Gets which way changes flow.</summary>
    public StorageSyncDirection Direction { get; init; } = StorageSyncDirection.Update;
    /// <summary>Gets whether <see cref="StorageSyncDirection.Mirror"/> deletes items that exist only at the destination.</summary>
    public bool DeleteExtraneous { get; init; }
    /// <summary>Gets whether a two-way sync carries deletions to the other side (only ever through the baseline).</summary>
    public bool PropagateDeletes { get; init; } = true;
    /// <summary>Gets what a two-way sync does with changes on both sides.</summary>
    public StorageSyncConflictPolicy ConflictPolicy { get; init; } = StorageSyncConflictPolicy.Block;
    /// <summary>Gets where baselines are kept; required for three-way two-way syncs.</summary>
    [JsonIgnore]
    public IStorageSyncStateStore? StateStore { get; init; }
    /// <summary>Gets the name this sync's baseline is stored under.</summary>
    public string? SyncId { get; init; }
    /// <summary>Gets whether to only plan: nothing is changed and every action is returned as planned.</summary>
    public bool DryRun { get; init; }
    /// <summary>Gets whether copied files keep the source's modification time (set, or kept in metadata on object stores).</summary>
    public bool PreserveTimestamps { get; init; } = true;
    /// <summary>Gets whether each copy is verified (SHA-256 during the copy, then confirmed on the destination).</summary>
    public bool Verify { get; init; }
    /// <summary>Gets how many files are copied at once.</summary>
    public int MaxConcurrency { get; init; } = 4;
    /// <summary>Gets the comparison settings: criteria, filters, case rules, links, and hashing budget.</summary>
    public StorageCompareOptions Compare { get; init; } = new();
    /// <summary>Gets the most files one run may delete; beyond it every deletion is withheld.</summary>
    public int? MaxDeletes { get; init; }
    /// <summary>Gets the largest share (0–100) of a side's files one run may delete; beyond it every deletion is withheld.</summary>
    public double? MaxDeletePercent { get; init; }
    /// <summary>
    /// Gets whether deletions may run when a side is unexpectedly empty — no files where the other side (or the
    /// baseline) has them, as when a drive is not mounted or a root is wrong. Off by default.
    /// </summary>
    public bool AllowEmptySide { get; init; }
    /// <summary>Gets how often a step that failed transiently is retried in the same run.</summary>
    public int ItemRetries { get; init; } = 2;
    /// <summary>Gets whether the run continues after a step fails; otherwise the remaining steps are not run.</summary>
    public bool ContinueOnError { get; init; } = true;
    /// <summary>Gets whether a plan with blocked conflicts may still apply its other steps.</summary>
    public bool ApplyWithConflicts { get; init; }
    /// <summary>Gets an optional progress sink; reports carry the current file.</summary>
    [JsonIgnore]
    public IProgress<StorageTransferProgress>? Progress { get; init; }

    internal Result Validate()
    {
        if (MaxConcurrency is < 1 or > 64)
            return Result.Failure(StorageErrors.InvalidContent("MaxConcurrency must be between 1 and 64."));
        if (DeleteExtraneous && Direction != StorageSyncDirection.Mirror)
            return Result.Failure(StorageErrors.InvalidContent("DeleteExtraneous only applies to Mirror syncs."));
        if (MaxDeletes is < 0 || MaxDeletePercent is < 0 or > 100)
            return Result.Failure(StorageErrors.InvalidContent("MaxDeletes cannot be negative and MaxDeletePercent must be between 0 and 100."));
        if (ItemRetries is < 0 or > 20)
            return Result.Failure(StorageErrors.InvalidContent("ItemRetries must be between 0 and 20."));
        if ((StateStore is null) != (SyncId is null))
            return Result.Failure(StorageErrors.InvalidContent("StateStore and SyncId are set together."));
        return Compare.Validate();
    }
}

/// <summary>One planned sync step, with the versions it was planned against.</summary>
public sealed record StorageSyncAction
{
    /// <summary>Gets the path relative to the synced directories, as the source spells it.</summary>
    public required string RelativePath { get; init; }
    /// <summary>
    /// Gets the destination's spelling of the path when it differs from <see cref="RelativePath"/> only by case
    /// (one side ignores case); null when both sides spell it the same.
    /// </summary>
    public string? DestinationRelativePath { get; init; }
    /// <summary>Gets what is done.</summary>
    public required StorageSyncActionKind Kind { get; init; }
    /// <summary>Gets why, from the comparison.</summary>
    public StorageDiffReason Reason { get; init; }
    /// <summary>Gets the content size copied, when known.</summary>
    public long? Bytes { get; init; }
    /// <summary>Gets the source's version when planned; the step only runs while it is unchanged.</summary>
    public StorageSyncIdentity? Source { get; init; }
    /// <summary>Gets the destination's version when planned, or null when it was absent.</summary>
    public StorageSyncIdentity? Destination { get; init; }
    /// <summary>Gets the conflict, for <see cref="StorageSyncActionKind.Conflict"/> and steps resolving one.</summary>
    public StorageSyncConflictKind? Conflict { get; init; }
    /// <summary>Gets the other path of a rename, or the name a copy writes to.</summary>
    public string? TargetPath { get; init; }
    /// <summary>Gets why a deletion is held back, when a safety rule applies.</summary>
    public string? WithheldReason { get; init; }
}

/// <summary>A sync plan: every step, the versions they depend on, and a digest to approve it by.</summary>
public sealed record StorageSyncPlan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Gets the source directory.</summary>
    public required string SourceRoot { get; init; }
    /// <summary>Gets the destination directory.</summary>
    public required string DestinationRoot { get; init; }
    /// <summary>Gets the sync's baseline name, when it has one.</summary>
    public string? SyncId { get; init; }
    /// <summary>Gets the baseline generation the plan was made against.</summary>
    public long BaselineGeneration { get; init; }
    /// <summary>Gets the direction.</summary>
    public StorageSyncDirection Direction { get; init; }
    /// <summary>Gets the conflict policy.</summary>
    public StorageSyncConflictPolicy ConflictPolicy { get; init; }
    /// <summary>Gets the steps, in the order they run.</summary>
    public IReadOnlyList<StorageSyncAction> Actions { get; init; } = [];
    /// <summary>Gets the files that already matched.</summary>
    public int Unchanged { get; init; }
    /// <summary>
    /// Gets, for a two-way sync with a baseline, the paths both sides agreed on when the plan was made, with the
    /// versions seen then. The baseline saved after the run records these and what the run's copies wrote —
    /// never what happens to be there afterwards, so an edit made during the run is still seen next time.
    /// </summary>
    public IReadOnlyDictionary<string, StorageSyncBaselineEntry> Agreed { get; init; } = new Dictionary<string, StorageSyncBaselineEntry>();
    /// <summary>Gets notes for a person, such as why deletions are withheld.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Gets when the plan was made.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Gets the SHA-256 over everything above; approve a plan by this value.</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>Gets the conflicts left for a person.</summary>
    [JsonIgnore]
    public IReadOnlyList<StorageSyncAction> Conflicts => [.. Actions.Where(action => action.Kind == StorageSyncActionKind.Conflict)];

    /// <summary>Gets whether the plan can be applied without <see cref="StorageSyncOptions.ApplyWithConflicts"/>.</summary>
    [JsonIgnore]
    public bool IsApprovable => Conflicts.Count == 0;

    /// <summary>Serializes the plan so it can be shown, stored, and approved later.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a plan written by <see cref="ToJson"/>.</summary>
    public static StorageSyncPlan FromJson(string json) =>
        JsonSerializer.Deserialize<StorageSyncPlan>(json, Json) ?? throw new JsonException("The plan is empty.");

    /// <summary>Computes the digest of the plan's content (everything but the digest itself).</summary>
    public string ComputeDigest() =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this with { Digest = string.Empty }, Json))));
}

/// <summary>How one planned step turned out.</summary>
/// <param name="Action">The step.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">The failure, for failed and stale steps.</param>
/// <param name="Attempts">How many times it was tried.</param>
public sealed record StorageSyncActionResult(StorageSyncAction Action, StorageSyncActionOutcome Outcome, Error? Error = null, int Attempts = 0);

/// <summary>The outcome of a sync, or its plan for a dry run.</summary>
public sealed record StorageSyncReport
{
    /// <summary>Gets the plan that ran.</summary>
    public required StorageSyncPlan Plan { get; init; }
    /// <summary>Gets each step's outcome, in plan order.</summary>
    public IReadOnlyList<StorageSyncActionResult> Results { get; init; } = [];
    /// <summary>Gets whether nothing was changed on purpose.</summary>
    public bool DryRun { get; init; }
    /// <summary>Gets whether the run was cancelled; the results say what was done before.</summary>
    public bool Cancelled { get; init; }
    /// <summary>Gets whether the baseline was saved after the run.</summary>
    public bool BaselineSaved { get; init; }

    /// <summary>Gets the planned steps.</summary>
    public IReadOnlyList<StorageSyncAction> Actions => Plan.Actions;
    /// <summary>Gets the files that already matched.</summary>
    public int Unchanged => Plan.Unchanged;
    /// <summary>Gets the steps that failed.</summary>
    public IReadOnlyList<StorageSyncActionResult> Failed => [.. Results.Where(result => result.Outcome == StorageSyncActionOutcome.Failed)];
    /// <summary>Gets the steps skipped because their item changed after the plan was made.</summary>
    public IReadOnlyList<StorageSyncActionResult> Stale => [.. Results.Where(result => result.Outcome == StorageSyncActionOutcome.Stale)];
    /// <summary>Gets the deletions held back by a safety rule.</summary>
    public IReadOnlyList<StorageSyncActionResult> Withheld => [.. Results.Where(result => result.Outcome == StorageSyncActionOutcome.Withheld)];
    /// <summary>Gets the conflicts left for a person.</summary>
    public IReadOnlyList<StorageSyncAction> Conflicts => Plan.Conflicts;
    /// <summary>Gets the number of files copied in either direction.</summary>
    public int Copied => Results.Count(result => result.Outcome == StorageSyncActionOutcome.Applied && result.Action.Kind is StorageSyncActionKind.CopyToDestination or StorageSyncActionKind.CopyToSource);
    /// <summary>Gets the number of items deleted on either side.</summary>
    public int Deleted => Results.Count(result => result.Outcome == StorageSyncActionOutcome.Applied && result.Action.Kind is StorageSyncActionKind.DeleteFromDestination or StorageSyncActionKind.DeleteFromSource);
}
