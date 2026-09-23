using System.Collections.ObjectModel;
using System.Text;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>Controls one provider-neutral directory listing page.</summary>
public sealed record StorageListOptions
{
    /// <summary>Gets whether descendants are returned in addition to direct children.</summary>
    public bool Recursive { get; init; }
    /// <summary>Gets the requested maximum number of provider entries in one page.</summary>
    public int PageSize { get; init; } = 1000;
    /// <summary>Gets the opaque continuation token returned by a previous page.</summary>
    public string? ContinuationToken { get; init; }
    /// <summary>
    /// Gets whether the library's own staging and backup items (<c>.cl-storage-*</c>, <c>.clstorage-*</c>)
    /// are listed. They exist only while a transfer runs, or after one was interrupted.
    /// </summary>
    public bool IncludeInternal { get; init; }
    /// <summary>Gets whether hidden items (dot-files, or items marked hidden) are listed.</summary>
    public bool IncludeHidden { get; init; } = true;
    /// <summary>
    /// Gets an optional case-insensitive name filter with <c>*</c> and <c>?</c> wildcards, such as <c>*.csv</c>.
    /// It applies to item names, so in a recursive listing directories that do not match are omitted but
    /// matching files inside them are still returned.
    /// </summary>
    public string? NamePattern { get; init; }

    /// <summary>Validates the requested page size.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate() => PageSize > 0
        ? Result.Success()
        : Result.Failure(StorageErrors.InvalidPath("PageSize must be greater than zero."));
}

/// <summary>Controls content upload, overwrite, metadata, and identity conditions.</summary>
public sealed record StorageUploadOptions
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private IReadOnlyDictionary<string, string> _metadata = EmptyMetadata;

    /// <summary>Gets whether an existing destination file may be replaced.</summary>
    public bool Overwrite { get; init; } = true;
    /// <summary>
    /// Gets how an existing destination is handled. When set it replaces <see cref="Overwrite"/>; when
    /// <see langword="null"/>, <see cref="Overwrite"/> decides. Conditional policies are resolved by the
    /// library's connections and helpers, not by a backend used on its own.
    /// </summary>
    public StorageConflictPolicy? ConflictPolicy { get; init; }
    /// <summary>Gets the source's modification time, compared by <see cref="StorageConflictPolicy.OverwriteIfNewer"/>.</summary>
    public DateTimeOffset? SourceLastModified { get; init; }
    /// <summary>
    /// Gets a caller-chosen identity for the source content. With <see cref="StorageConflictPolicy.Resume"/> it
    /// keys the staged bytes together with the length and <see cref="SourceLastModified"/>, so only the same
    /// source continues them. Resume needs <see cref="SourceLastModified"/>, or this identity marked with
    /// <see cref="SourceIdentityIsContentVersion"/>; a path alone is refused, as an edited file of the same length
    /// would continue the old prefix. <c>UploadFileAsync</c> sets the path and the time.
    /// </summary>
    public string? SourceIdentity { get; init; }
    /// <summary>
    /// Gets whether <see cref="SourceIdentity"/> changes whenever the content changes (a content hash, an ETag, a
    /// version id), so it identifies the content on its own and resume needs no <see cref="SourceLastModified"/>.
    /// Never set it for a name or a path.
    /// </summary>
    public bool SourceIdentityIsContentVersion { get; init; }
    /// <summary>Gets an optional progress sink, reported at most every 250 ms with speed and remaining time.</summary>
    public IProgress<StorageTransferProgress>? Progress { get; init; }
    /// <summary>Set once progress, speed limits, and conflict policy have been applied, so they are not applied twice.</summary>
    internal bool PipelineApplied { get; init; }
    /// <summary>Gets whether missing physical parent directories should be created.</summary>
    public bool CreateParents { get; init; } = true;
    /// <summary>Gets the optional MIME content type stored with the object.</summary>
    public string? ContentType { get; init; }
    /// <summary>Optional condition applied atomically by providers that advertise conditional updates.</summary>
    public StorageMutationCondition? Condition { get; init; }
    /// <summary>
    /// Gets the exact number of bytes the source must deliver. A source that ends early or runs longer fails
    /// the upload without committing anything.
    /// </summary>
    public long? ExpectedLength { get; init; }
    /// <summary>
    /// Gets whether to verify the upload: SHA-256 is computed while the content streams to a staging object,
    /// compared with <see cref="ExpectedSha256"/> when set, and the staged object is confirmed (by the
    /// server's SHA-256 where it keeps one, otherwise by reading it back) before it replaces the destination.
    /// </summary>
    public bool Verify { get; init; }
    /// <summary>Gets the SHA-256 (hex) the content must have; implies <see cref="Verify"/>.</summary>
    public string? ExpectedSha256 { get; init; }
    /// <summary>Gets an immutable snapshot of user metadata stored with the object.</summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = value is null || value.Count == 0
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>Validates headers, metadata, and compatible mutation settings.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate()
    {
        var contentType = StorageOptionValidation.OptionalHeaderValue(ContentType, nameof(ContentType));
        if (contentType.IsFailure) return contentType;
        var metadata = StorageOptionValidation.Metadata(Metadata);
        if (metadata.IsFailure) return metadata;
        var condition = Condition?.Validate() ?? Result.Success();
        if (condition.IsFailure) return condition;
        if (ExpectedLength is < 0)
            return Result.Failure(StorageErrors.InvalidContent("ExpectedLength cannot be negative."));
        if (ExpectedSha256 is not null && !StorageOptionValidation.IsSha256Hex(ExpectedSha256))
            return Result.Failure(StorageErrors.InvalidContent("ExpectedSha256 must be 64 hexadecimal characters."));
        if (Condition is { IsEmpty: false } && ConflictPolicy is not (null or StorageConflictPolicy.Overwrite))
            return Result.Failure(StorageErrors.InvalidContent(
                "A Condition replaces a known version, so it cannot be combined with a conflict policy other than Overwrite."));
        return !Overwrite && Condition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.InvalidPath(
                "Overwrite=false cannot be combined with an expected ETag or version condition."))
            : Result.Success();
    }
}

/// <summary>Controls range, buffering, and exact-version downloads.</summary>
public sealed record StorageDownloadOptions
{
    /// <summary>Gets an optional progress sink, reported as the returned stream is read.</summary>
    public IProgress<StorageTransferProgress>? Progress { get; init; }
    /// <summary>Gets the zero-based byte offset at which reading begins.</summary>
    public long Offset { get; init; }
    /// <summary>Gets the requested byte count, or <see langword="null"/> to read through end of content.</summary>
    public long? Length { get; init; }
    /// <summary>Gets the maximum bytes allowed by buffered download helpers.</summary>
    public long? MaxBufferedBytes { get; init; }
    /// <summary>Optional provider version/generation identifier to read.</summary>
    public string? VersionId { get; init; }

    /// <summary>Validates range arithmetic, buffer bounds, and the optional version token.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate()
    {
        if (Offset < 0)
            return Result.Failure(StorageErrors.InvalidPath("Offset cannot be negative."));
        if (Length is <= 0)
            return Result.Failure(StorageErrors.InvalidPath("Length must be greater than zero."));
        if (MaxBufferedBytes is <= 0)
            return Result.Failure(StorageErrors.InvalidPath("MaxBufferedBytes must be greater than zero."));
        if (Length.HasValue && Offset > long.MaxValue - Length.Value)
            return Result.Failure(StorageErrors.InvalidPath("The requested range overflows Int64."));
        return StorageOptionValidation.OptionalToken(VersionId, nameof(VersionId));
    }
}

internal static class StorageOptionValidation
{
    public static Result Metadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.Count > 1_024)
            return Result.Failure(StorageErrors.TooLarge("Metadata cannot contain more than 1,024 entries."));

        var totalBytes = 0;
        foreach (var (name, value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 ||
                name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            {
                return Result.Failure(StorageErrors.InvalidPath(
                    "Metadata names must contain only ASCII letters, digits, '.', '_' or '-' and be at most 256 characters."));
            }
            if (value is null || value.Any(char.IsControl))
                return Result.Failure(StorageErrors.InvalidPath("Metadata values cannot be null or contain control characters."));

            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value));
            if (totalBytes > 64 * 1024)
                return Result.Failure(StorageErrors.TooLarge("Combined metadata exceeds the 64 KiB portable limit."));
        }
        return Result.Success();
    }

    public static int MetadataSizeBytes(IReadOnlyDictionary<string, string> metadata) =>
        metadata.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value));

    public static Result OptionalHeaderValue(string? value, string name)
    {
        if (value is null) return Result.Success();
        return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            ? Result.Failure(StorageErrors.InvalidPath($"{name} cannot be empty or contain control characters."))
            : Result.Success();
    }

    public static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    public static Result OptionalToken(string? value, string name)
    {
        if (value is null) return Result.Success();
        return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            ? Result.Failure(StorageErrors.InvalidPath($"{name} cannot be empty or contain control characters."))
            : Result.Success();
    }
}

/// <summary>Controls recursive, idempotent, and conditional deletion.</summary>
public sealed record StorageDeleteOptions
{
    /// <summary>Gets whether a directory tree may be deleted recursively.</summary>
    public bool Recursive { get; init; }
    /// <summary>Gets whether a missing target is treated as an idempotent success.</summary>
    public bool IgnoreMissing { get; init; }
    /// <summary>Optional condition applied atomically by providers that advertise conditional deletes.</summary>
    public StorageMutationCondition? Condition { get; init; }

    /// <summary>Validates the optional identity condition.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate() => Condition?.Validate() ?? Result.Success();
}

/// <summary>Requires the current item to match one or both provider-neutral identity tokens.</summary>
public sealed record StorageMutationCondition
{
    /// <summary>Gets the entity tag that must match the current object.</summary>
    public string? ExpectedETag { get; init; }
    /// <summary>Gets the version or generation token that must match the current object.</summary>
    public string? ExpectedVersionId { get; init; }

    /// <summary>Gets whether no identity token has been supplied.</summary>
    public bool IsEmpty => ExpectedETag is null && ExpectedVersionId is null;

    /// <summary>Validates supplied identity tokens for portable header use.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate()
    {
        var etag = StorageOptionValidation.OptionalToken(ExpectedETag, nameof(ExpectedETag));
        return etag.IsFailure
            ? etag
            : StorageOptionValidation.OptionalToken(ExpectedVersionId, nameof(ExpectedVersionId));
    }
}

/// <summary>Controls destination overwrite, parent creation, and metadata handling for copy or move.</summary>
public sealed record StorageTransferOptions
{
    /// <summary>Gets whether an existing destination file may be replaced.</summary>
    public bool Overwrite { get; init; } = true;
    /// <summary>
    /// Gets how each existing destination file is handled. When set it replaces <see cref="Overwrite"/>.
    /// Conditional policies are decided per file, so directory transfers relay through the client, and a
    /// move deletes only the source files that were actually transferred.
    /// </summary>
    public StorageConflictPolicy? ConflictPolicy { get; init; }
    /// <summary>Gets whether missing physical destination parents should be created.</summary>
    public bool CreateParents { get; init; } = true;
    /// <summary>Gets how user metadata is handled across provider boundaries.</summary>
    public StorageMetadataPreservation MetadataPreservation { get; init; } = StorageMetadataPreservation.BestEffort;
    /// <summary>Gets an optional progress sink for relayed transfers; bytes accumulate across the files of a directory.</summary>
    public IProgress<StorageTransferProgress>? Progress { get; init; }
    /// <summary>
    /// Gets how symbolic links are treated when a transfer relays content through the client. A native
    /// same-connection directory move (a rename on Local, FTP, SFTP, or WebDAV) moves the links inside it as they
    /// are, whatever this is set to.
    /// </summary>
    public StorageLinkHandling LinkHandling { get; init; } = StorageLinkHandling.Reject;
    /// <summary>
    /// Gets the version the destination must still have for a single-file transfer to replace it: the ETag
    /// and/or version the caller saw. The check applies again right before the staged content is promoted.
    /// A missing destination fails the condition. <see cref="StorageTransferReport.ConditionEnforcement"/>
    /// says whether the provider enforced it atomically.
    /// </summary>
    public StorageMutationCondition? DestinationCondition { get; init; }
    /// <summary>Gets the exact source version to read (providers with versioning).</summary>
    public string? SourceVersionId { get; init; }
    /// <summary>
    /// Gets the ETag the source must have. It is checked before reading and again after the content has
    /// streamed; a changed source fails with <c>storage.conflict</c> without committing.
    /// </summary>
    public string? ExpectedSourceETag { get; init; }
    /// <summary>Gets the exact length the single-file source must have; a shorter or longer source fails without committing.</summary>
    public long? ExpectedSourceLength { get; init; }
    /// <summary>
    /// Gets whether to verify each file: SHA-256 is computed during the relay, compared with
    /// <see cref="ExpectedSha256"/> when set, and the staged copy confirmed (server SHA-256, or read back)
    /// before it is promoted. The digest is returned in <see cref="StorageTransferReport.Sha256"/>.
    /// </summary>
    public bool Verify { get; init; }
    /// <summary>Gets the SHA-256 (hex) a single-file source must have; implies <see cref="Verify"/>.</summary>
    public string? ExpectedSha256 { get; init; }
    /// <summary>
    /// Gets a token from an earlier report to continue that transfer's staged data. The source must still
    /// be the same version; otherwise the staged data is discarded and the transfer starts again. A token does
    /// not allow replacing the destination by itself: use it with <see cref="StorageConflictPolicy.Resume"/> or
    /// with <see cref="Overwrite"/>; <see cref="Validate"/> refuses it with <c>Overwrite = false</c>. A token
    /// naming any file other than its own part file for this destination is ignored, never deleted.
    /// </summary>
    public StorageResumeToken? ResumeToken { get; init; }
    /// <summary>Gets whether a directory transfer lists the source first so progress reports carry totals.</summary>
    public bool PreScan { get; init; }

    /// <summary>
    /// Called, and awaited, when the transfer reaches a phase that matters after a crash: before anything
    /// is written, before the destination is touched, and before the source of a move is deleted. The
    /// transfer queue records it so a restart knows whether the destination may have changed.
    /// </summary>
    internal Func<Queue.StorageTransferPhase, CancellationToken, Task>? PhaseChanged { get; init; }

    /// <summary>Gets whether an option that only makes sense for one file was set.</summary>
    internal bool HasSingleFileGuarantees =>
        DestinationCondition is { IsEmpty: false } || SourceVersionId is not null || ExpectedSourceETag is not null ||
        ExpectedSourceLength is not null || ExpectedSha256 is not null || ResumeToken is not null;

    /// <summary>Gets whether any single-file guarantee (condition, pinning, length, verification, resume) was requested.</summary>
    internal bool RequiresGuarantees =>
        DestinationCondition is { IsEmpty: false } || SourceVersionId is not null || ExpectedSourceETag is not null ||
        ExpectedSourceLength is not null || Verify || ExpectedSha256 is not null || ResumeToken is not null ||
        ConflictPolicy == StorageConflictPolicy.Resume;

    /// <summary>Validates the metadata-preservation and link-handling modes.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate()
    {
        if (!Enum.IsDefined(MetadataPreservation))
            return Result.Failure(StorageErrors.InvalidPath("MetadataPreservation is invalid."));
        if (ConflictPolicy is { } policy && !Enum.IsDefined(policy))
            return Result.Failure(StorageErrors.InvalidPath("ConflictPolicy is invalid."));
        var condition = DestinationCondition?.Validate() ?? Result.Success();
        if (condition.IsFailure) return condition;
        if (ExpectedSourceLength is < 0)
            return Result.Failure(StorageErrors.InvalidContent("ExpectedSourceLength cannot be negative."));
        if (ExpectedSha256 is not null && !StorageOptionValidation.IsSha256Hex(ExpectedSha256))
            return Result.Failure(StorageErrors.InvalidContent("ExpectedSha256 must be 64 hexadecimal characters."));
        if (DestinationCondition is { IsEmpty: false } && (!Overwrite || ConflictPolicy is not (null or StorageConflictPolicy.Overwrite)))
            return Result.Failure(StorageErrors.InvalidContent("DestinationCondition replaces a known version, so it needs Overwrite and no other conflict policy."));
        var sourceVersion = StorageOptionValidation.OptionalToken(SourceVersionId, nameof(SourceVersionId));
        if (sourceVersion.IsFailure) return sourceVersion;
        var sourceETag = StorageOptionValidation.OptionalToken(ExpectedSourceETag, nameof(ExpectedSourceETag));
        if (sourceETag.IsFailure) return sourceETag;
        // A token continues staged bytes; it never allows replacing a destination the options would not replace.
        if (ResumeToken is not null && (ConflictPolicy == StorageConflictPolicy.Fail || (ConflictPolicy is null && !Overwrite)))
            return Result.Failure(StorageErrors.InvalidContent(
                "A ResumeToken does not allow replacing the destination: use ConflictPolicy Resume (or Overwrite), not Overwrite=false."));
        return Enum.IsDefined(LinkHandling)
            ? Result.Success()
            : Result.Failure(StorageErrors.InvalidPath("LinkHandling is invalid."));
    }
}

/// <summary>How an existing destination file is handled, like FileZilla's "target file already exists" choices.</summary>
public enum StorageConflictPolicy
{
    /// <summary>Fails with <c>storage.conflict</c>.</summary>
    Fail = 0,
    /// <summary>Replaces the destination.</summary>
    Overwrite = 1,
    /// <summary>Leaves the destination untouched.</summary>
    Skip = 2,
    /// <summary>Replaces the destination only when the source is newer (2-second tolerance; unknown times count as newer).</summary>
    OverwriteIfNewer = 3,
    /// <summary>Replaces the destination only when the sizes differ (unknown sizes count as different).</summary>
    OverwriteIfSizeDiffers = 4,
    /// <summary>Replaces the destination when the source is newer or the sizes differ.</summary>
    OverwriteIfNewerOrSizeDiffers = 5,
    /// <summary>Writes to the first free name <c>name (1).ext</c>, <c>name (2).ext</c>, … instead.</summary>
    Rename = 6,
    /// <summary>
    /// Writes through a resumable staging object and replaces the destination only when it is complete. When
    /// an earlier attempt left staged data for the same destination and the same source (same length,
    /// time, ETag, or version), only the missing tail is read and appended to it; the destination itself is
    /// never half-written. Appending needs <see cref="StorageFeature.Append"/> on the destination (otherwise
    /// the staging object is rewritten from the start), and uploads need a seekable source.
    /// </summary>
    Resume = 7
}

/// <summary>How relayed transfers treat symbolic links.</summary>
public enum StorageLinkHandling
{
    /// <summary>Fails the transfer when it meets a link, because link targets are provider-specific.</summary>
    Reject = 0,
    /// <summary>Leaves links out of the transfer.</summary>
    Skip = 1,
    /// <summary>Copies the content of the file a link points to. Links to directories are refused, which also rules out loops.</summary>
    Follow = 2,
    /// <summary>
    /// Creates an equivalent link at the destination. A target inside the transferred directory is remapped
    /// to the copy; the source must support <see cref="StorageFeature.ReadLinks"/> and the destination
    /// <see cref="StorageFeature.CreateLinks"/>.
    /// </summary>
    Recreate = 3
}
