using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>How a transfer ended.</summary>
public enum StorageTransferOutcome
{
    /// <summary>Everything was written and, for a move, the source removed.</summary>
    Completed = 0,
    /// <summary>The conflict policy left the destination as it was; see <see cref="StorageTransferReport.SkipReason"/>.</summary>
    Skipped = 1,
    /// <summary>Nothing was committed and nothing needs attention; the destination is as it was.</summary>
    Failed = 2,
    /// <summary>
    /// The transfer stopped part-way and the state is mixed — for example the destination committed but the
    /// source could not be deleted, or a rollback did not finish. The report's state fields say exactly what.
    /// </summary>
    NeedsReconciliation = 3,
    /// <summary>
    /// The caller cancelled the transfer before anything was committed. Staging and backups were cleaned up,
    /// except the staged bytes of a resumable transfer, which <see cref="StorageTransferReport.ResumeToken"/> continues.
    /// </summary>
    Cancelled = 4
}

/// <summary>Why a conflict policy skipped a file.</summary>
public enum StorageSkipReason
{
    /// <summary><see cref="StorageConflictPolicy.Skip"/>: the destination exists.</summary>
    DestinationExists = 0,
    /// <summary><see cref="StorageConflictPolicy.OverwriteIfNewer"/>: the source is not newer.</summary>
    SourceNotNewer = 1,
    /// <summary><see cref="StorageConflictPolicy.OverwriteIfSizeDiffers"/>: the sizes are equal.</summary>
    SameSize = 2,
    /// <summary><see cref="StorageConflictPolicy.OverwriteIfNewerOrSizeDiffers"/>: neither newer nor a different size.</summary>
    Unchanged = 3,
    /// <summary><see cref="StorageConflictPolicy.Resume"/>: the destination is already complete.</summary>
    AlreadyComplete = 4
}

/// <summary>How firmly a destination condition (create-new, or replace-only-this-version) was enforced.</summary>
public enum StorageConditionEnforcement
{
    /// <summary>No condition applied.</summary>
    None = 0,
    /// <summary>The provider enforced it in the same operation that committed the data; no race is possible.</summary>
    Atomic = 1,
    /// <summary>
    /// Checked immediately before the commit. A writer that changes the destination in the short window
    /// between the check and the commit is not detected; the provider has no atomic form.
    /// </summary>
    CheckedBeforeCommit = 2
}

/// <summary>
/// Everything needed to continue a resumable transfer later, even from another process: where the partial
/// data is staged and which source it was read from. Store it as JSON and pass it back in
/// <see cref="StorageTransferOptions.ResumeToken"/>.
/// </summary>
public sealed record StorageResumeToken
{
    /// <summary>Gets the destination path the transfer commits to.</summary>
    public required string DestinationPath { get; init; }
    /// <summary>Gets the internal staging path holding the bytes written so far.</summary>
    public required string StagingPath { get; init; }
    /// <summary>Gets the number of bytes staged when the token was issued.</summary>
    public long BytesStaged { get; init; }
    /// <summary>Gets the source path, for copies and moves.</summary>
    public string? SourcePath { get; init; }
    /// <summary>Gets the source ETag the staged bytes were read from.</summary>
    public string? SourceETag { get; init; }
    /// <summary>Gets the source version the staged bytes were read from.</summary>
    public string? SourceVersionId { get; init; }
    /// <summary>Gets the source length.</summary>
    public long? SourceLength { get; init; }
    /// <summary>Gets the source modification time.</summary>
    public DateTimeOffset? SourceLastModified { get; init; }
}

/// <summary>What a copy or move did, including exactly what state it left when it did not finish.</summary>
public sealed record StorageTransferReport
{
    /// <summary>Gets how the transfer ended.</summary>
    public required StorageTransferOutcome Outcome { get; init; }
    /// <summary>Gets the failure, for <see cref="StorageTransferOutcome.Failed"/> and <see cref="StorageTransferOutcome.NeedsReconciliation"/>.</summary>
    public Error? Error { get; init; }
    /// <summary>Gets the normalized source path.</summary>
    public string SourcePath { get; init; } = string.Empty;
    /// <summary>Gets the destination path that was requested.</summary>
    public string DestinationPath { get; init; } = string.Empty;
    /// <summary>Gets the path actually written, which differs under <see cref="StorageConflictPolicy.Rename"/>.</summary>
    public string? WrittenPath { get; init; }
    /// <summary>Gets why a file was skipped.</summary>
    public StorageSkipReason? SkipReason { get; init; }
    /// <summary>Gets whether the source was a file, directory, or link.</summary>
    public StorageItemType? SourceType { get; init; }
    /// <summary>Gets the files written.</summary>
    public long Files { get; init; }
    /// <summary>Gets the directories created or reused.</summary>
    public long Directories { get; init; }
    /// <summary>Gets the content bytes written in this attempt.</summary>
    public long Bytes { get; init; }
    /// <summary>Gets the bytes that were already staged and reused when a transfer resumed.</summary>
    public long BytesResumed { get; init; }
    /// <summary>Gets files a conflict policy left untouched (directory transfers).</summary>
    public long SkippedFiles { get; init; }
    /// <summary>Gets the SHA-256 of a single file's content (hex), when verification ran or the digest was computed.</summary>
    public string? Sha256 { get; init; }
    /// <summary>Gets how the destination content was confirmed when <c>Verify</c> was set: <c>server</c> or <c>reread</c>.</summary>
    public string? VerifiedBy { get; init; }
    /// <summary>Gets the destination's ETag after a single-file commit.</summary>
    public string? DestinationETag { get; init; }
    /// <summary>Gets the destination's version after a single-file commit.</summary>
    public string? DestinationVersionId { get; init; }
    /// <summary>Gets whether content reached its final destination path.</summary>
    public bool DestinationCommitted { get; init; }
    /// <summary>Gets whether a move deleted its source; null for copies.</summary>
    public bool? SourceDeleted { get; init; }
    /// <summary>Gets a staging path left behind — kept for a resume, or one cleanup could not remove.</summary>
    public string? StagingLeftBehind { get; init; }
    /// <summary>Gets whether an overwritten destination was restored after a failure; null when nothing was replaced.</summary>
    public bool? BackupRestored { get; init; }
    /// <summary>Gets a backup of the previous destination that could not be removed or restored.</summary>
    public string? BackupLeftBehind { get; init; }
    /// <summary>Gets how a destination condition was enforced.</summary>
    public StorageConditionEnforcement ConditionEnforcement { get; init; }
    /// <summary>Gets a token to continue this transfer, when it failed after staging part of a resumable write.</summary>
    public StorageResumeToken? ResumeToken { get; init; }

    /// <summary>Gets whether the transfer completed or was skipped as the policy asked.</summary>
    public bool IsSuccess => Outcome is StorageTransferOutcome.Completed or StorageTransferOutcome.Skipped;

    /// <summary>Gets whether the transfer failed or needs reconciliation; see <see cref="Error"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Converts the report to a plain result.</summary>
    /// <returns>Success, or the report's error.</returns>
    public Result ToResult() => IsSuccess ? Result.Success() : Result.Failure(Error!);
}
