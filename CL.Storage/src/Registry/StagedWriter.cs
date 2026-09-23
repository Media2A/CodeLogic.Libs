using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>What to write through a staging object, and what to check before it may be promoted.</summary>
internal sealed record StagedWriteRequest
{
    /// <summary>The final destination path the staged content will be promoted to.</summary>
    public required string Path { get; init; }
    /// <summary>Content type, metadata, parent creation, and progress for the staging upload.</summary>
    public StorageUploadOptions Upload { get; init; } = new();
    /// <summary>The exact number of bytes the source must deliver.</summary>
    public long? ExpectedLength { get; init; }
    /// <summary>Whether to hash the content and confirm the staged object.</summary>
    public bool Verify { get; init; }
    /// <summary>The digest the content must have.</summary>
    public string? ExpectedSha256 { get; init; }
    /// <summary>When set, staging is resumable: its name derives from this key, and it survives failures.</summary>
    public string? ResumeKey { get; init; }
    /// <summary>An explicit resumable staging path, from a resume token.</summary>
    public string? StagingPath { get; init; }

    public bool Resumable => ResumeKey is not null || StagingPath is not null;
}

/// <summary>Staged content ready to promote.</summary>
internal sealed record StagedContent(string StagingPath, long Bytes, long BytesResumed, string? Sha256, string? VerifiedBy);

/// <summary>The outcome of writing to staging: the content, or the error and what was left behind.</summary>
internal sealed record StagedWriteResult(StagedContent? Content, Error? Error, string? StagingLeft, long BytesStaged)
{
    /// <summary>Whether the caller cancelled; a resumable write's staged bytes are kept, anything else removed.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Why a staging object that should have been removed was not.</summary>
    public Error? CleanupError { get; init; }

    /// <summary>
    /// Whether the write used a resumable staging object, so <see cref="StagingLeft"/> is a prefix a later
    /// attempt may continue. False when the request was not resumable, or when its part file was in use by
    /// another transfer and a private staging object was used instead.
    /// </summary>
    public bool Resumable { get; init; }

    public bool IsSuccess => Content is not null;
}

/// <summary>What a promote did: its result, how a condition was enforced, and what the provider left behind.</summary>
/// <param name="Result">Success when the destination holds the staged content.</param>
/// <param name="Enforcement">How a create-only or version condition was enforced.</param>
/// <param name="Touched">
/// Whether the destination may have been changed: false when the promote was refused before the provider was
/// asked to move anything (a failed condition check), so the destination is exactly as it was.
/// </param>
/// <param name="LeftBehind">
/// Internal objects the provider could not remove after it committed (its own backup or staging copy); the
/// destination is committed even though the provider reported an error.
/// </param>
internal sealed record PromoteOutcome(Result Result, StorageConditionEnforcement Enforcement, bool Touched, IReadOnlyList<string> LeftBehind);

/// <summary>Tags progress with the path a caller knows instead of an internal staging name.</summary>
internal sealed class PathProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
{
    public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
}

/// <summary>Opens the source for reading from <paramref name="offset"/>; the caller disposes the stream.</summary>
internal delegate Task<Result<Stream>> StagedSourceOpener(long offset, CancellationToken cancellationToken);

/// <summary>
/// Writes content through a staging object next to the destination: hashed and counted while it streams,
/// checked against an expected length and digest, confirmed on the destination, and only then promoted.
/// The destination is never half-written. Resumable writes keep the staging object on failure under a name
/// derived from the destination and the source's identity, so a later attempt, even from another process,
/// appends only the missing tail.
/// </summary>
internal static class StagedWriter
{
    internal const long PauseWriterThreshold = 1_048_576;
    internal const long ResumeWriterThreshold = 524_288;
    internal const int SegmentSize = 65_536;
    private const int StagingNameAttempts = 8;
    internal const string ResumablePrefix = ".cl-storage-part-";

    /// <summary>The deterministic staging path for a resumable write.</summary>
    public static string ResumableStagingPath(string destinationPath, string resumeKey)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{destinationPath}\n{resumeKey}")));
        return Combine(Parent(destinationPath), $"{ResumablePrefix}{digest[..32]}.tmp");
    }

    /// <summary>A resume key describing a source's identity; a changed source gets a different key.</summary>
    public static string SourceKey(string? sourcePath, long? length, DateTimeOffset? modified, string? eTag, string? versionId) =>
        string.Join('|', sourcePath ?? string.Empty, length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            modified?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, eTag ?? string.Empty, versionId ?? string.Empty);

    /// <summary>
    /// Writes through staging. Cancellation is reported, not thrown: the staging object is removed, or kept
    /// with its size for a resumable write, and <see cref="StagedWriteResult.Cancelled"/> is set.
    /// </summary>
    public static async Task<StagedWriteResult> WriteAsync(
        IStorageService destination,
        StagedWriteRequest request,
        StagedSourceOpener open,
        CancellationToken cancellationToken)
    {
        var parent = Parent(request.Path);
        var staging = request.StagingPath ?? (request.ResumeKey is { } key ? ResumableStagingPath(request.Path, key) : null);
        string? part = null;
        if (staging is not null)
        {
            // One writer per part file in this process. A second transfer of the same source to the same
            // destination (two queued jobs for one file) writes a private staging object instead of appending
            // into the bytes the first one is writing; it is not resumable, but neither corrupts the other.
            part = PartKey(destination, staging);
            if (!ActiveParts.TryAdd(part, 0))
            {
                part = null;
                staging = null;
                request = request with { ResumeKey = null, StagingPath = null };
            }
        }
        try
        {
            if (staging is null)
            {
                Result<string> allocated;
                try { allocated = await AllocateStagingPathAsync(destination, parent, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return CancelledResult(null, 0); }
                if (allocated.IsFailure) return new StagedWriteResult(null, allocated.Error, null, 0);
                staging = allocated.Value!;
            }
            StagedWriteResult result;
            try
            {
                result = await WriteToStagingAsync(destination, request, open, staging, parent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!request.Resumable)
                {
                    var deleted = await DeleteAsync(destination, staging).ConfigureAwait(false);
                    return CancelledResult(deleted.IsFailure ? staging : null, 0);
                }
                var kept = await TryGetInfoAsync(destination, staging).ConfigureAwait(false);
                result = kept.IsSuccess ? CancelledResult(staging, kept.Value!.Size ?? 0) : CancelledResult(null, 0);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A throwing provider, source, or progress sink: the staging object is cleaned up (or kept, for a
                // resumable write) exactly as for a failure it reported.
                result = await FailAsync(destination, request, staging, StorageErrors.FromException(error, "Write transfer staging object"), contentIsWrong: false).ConfigureAwait(false);
            }
            return result with { Resumable = request.Resumable };
        }
        finally
        {
            if (part is not null) ActiveParts.TryRemove(part, out _);
        }
    }

    /// <summary>Part files being written in this process, so two writers never append into one.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveParts = new(StringComparer.Ordinal);

    private static string PartKey(IStorageService destination, string staging) =>
        $"{destination.Provider}\n{destination.Root}\n{staging}";

    /// <summary>Whether a resumable part file is being written by a transfer in this process.</summary>
    internal static bool IsPartInUse(IStorageService destination, string staging) => ActiveParts.ContainsKey(PartKey(destination, staging));

    private static StagedWriteResult CancelledResult(string? stagingLeft, long bytes) =>
        new(null, StorageErrors.Cancelled("The write was cancelled."), stagingLeft, bytes) { Cancelled = true };

    private static async Task<Result<StorageItem>> TryGetInfoAsync(IStorageService destination, string path)
    {
        try { return await destination.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Read transfer staging object")); }
    }

    private static async Task<StagedWriteResult> WriteToStagingAsync(
        IStorageService destination,
        StagedWriteRequest request,
        StagedSourceOpener open,
        string staging,
        string parent,
        CancellationToken cancellationToken)
    {
        var verify = request.Verify || request.ExpectedSha256 is not null;
        long offset = 0;
        using var hash = verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        if (request.Resumable)
        {
            var resumed = await ResumableOffsetAsync(destination, staging, request.ExpectedLength, cancellationToken).ConfigureAwait(false);
            if (resumed.IsFailure)
                return resumed.Error!.Code == StorageErrors.ConflictCode
                    ? new StagedWriteResult(null, resumed.Error, null, 0)
                    : await FailAsync(destination, request, staging, resumed.Error!, contentIsWrong: false).ConfigureAwait(false);
            offset = resumed.Value;
        }

        // Everything is staged already: the source is not opened at its end, which servers answer with
        // "range not satisfiable", so a fully staged resume can still complete.
        var complete = offset > 0 && request.ExpectedLength == offset;
        Result<Stream> opened;
        if (offset > 0 && hash is not null)
        {
            // Verification must not take the staged prefix on trust: the source's own first bytes are read and
            // hashed, and must match what is staged, before only the rest is appended.
            opened = await OpenVerifiedTailAsync(destination, staging, offset, hash, open, cancellationToken).ConfigureAwait(false);
            if (opened.IsSuccess && opened.Value is null)
            {
                await DeleteAsync(destination, staging).ConfigureAwait(false);
                offset = 0;
                complete = false;
                hash.GetHashAndReset();
                opened = await open(0, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (complete)
        {
            opened = Result<Stream>.Success(Stream.Null);
        }
        else
        {
            opened = await open(offset, cancellationToken).ConfigureAwait(false);
        }
        if (opened.IsFailure)
            return await FailAsync(destination, request, staging, opened.Error!, contentIsWrong: false).ConfigureAwait(false);

        var remaining = request.ExpectedLength is { } expected ? expected - offset : (long?)null;
        Result<long> written;
        GuardedReadStream guard;
        await using (var source = opened.Value!)
        {
            guard = new GuardedReadStream(source, hash, remaining);
            var stagingUpload = request.Upload with
            {
                Overwrite = true,
                ConflictPolicy = null,
                Condition = null,
                ExpectedLength = null,
                Verify = false,
                ExpectedSha256 = null,
                CreateParents = request.Upload.CreateParents
            };
            // A resumable write appends straight into its staging object where the provider can, so the bytes
            // that arrived before a failure stay there; a normal upload would stage them away and discard them.
            var direct = request.Resumable && destination is IStorageAppendService && destination.Capabilities.Supports(StorageFeature.Append);
            if (direct && offset == 0 && parent.Length > 0 && request.Upload.CreateParents)
            {
                // Appending does not create missing folders everywhere (SFTP), unlike an upload.
                var created = await destination.CreateDirectoryAsync(parent, cancellationToken).ConfigureAwait(false);
                if (created.IsFailure)
                    return await FailAsync(destination, request, staging, created.Error!, contentIsWrong: false).ConfigureAwait(false);
            }
            try
            {
                if (complete)
                {
                    // Nothing to append; a source that still has bytes is longer than expected.
                    try { _ = await guard.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false); }
                    catch (SourceTooLongException) { }
                    written = Result<long>.Success(0);
                }
                else
                {
                    written = offset == 0 && !direct
                        ? await RelayAsync(guard, destination, staging, stagingUpload, cancellationToken).ConfigureAwait(false)
                        : await AppendAsync(destination, staging, guard, request.Upload, request.ExpectedLength, request.Path, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cleaned up (or kept, for resume) by WriteAsync.
                throw;
            }
            catch (Exception) when (guard.Exceeded)
            {
                // The provider let the guard's "too long" escape; it is reported as the conflict it is.
                written = Result<long>.Failure(StorageErrors.Conflict("The source delivered more bytes than expected."));
            }
        }

        if (guard.Exceeded)
            return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                $"The source delivered more than the expected {request.ExpectedLength} bytes.",
                $"expectedLength={request.ExpectedLength}"), contentIsWrong: true).ConfigureAwait(false);
        if (written.IsFailure)
            return await FailAsync(destination, request, staging, written.Error!, contentIsWrong: false).ConfigureAwait(false);

        var total = offset + written.Value;
        if (request.ExpectedLength is { } length && total != length)
            // A short source leaves a good prefix a resume can continue; a longer one tripped the guard above.
            return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                $"The source delivered {total} bytes; {length} were expected.",
                $"expectedLength={length};actualLength={total}"), contentIsWrong: total > length).ConfigureAwait(false);

        if (request.Resumable)
        {
            // A part file is shared by name: a writer in another process appending to it at the same time would
            // leave more (or other) bytes than this writer accounted for. Such a file is never promoted.
            var stagedInfo = await destination.GetInfoAsync(staging, cancellationToken).ConfigureAwait(false);
            if (stagedInfo.IsFailure)
                return await FailAsync(destination, request, staging, stagedInfo.Error!, contentIsWrong: false).ConfigureAwait(false);
            if (stagedInfo.Value!.Size is { } stagedSize && stagedSize != total)
                return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                    "The staged data changed while it was written; another transfer may be writing the same part file.",
                    $"expectedLength={total};actualLength={stagedSize}"), contentIsWrong: true).ConfigureAwait(false);
        }

        string? digest = null;
        string? verifiedBy = null;
        if (hash is not null)
        {
            digest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (request.ExpectedSha256 is { } expectedDigest && !string.Equals(expectedDigest, digest, StringComparison.OrdinalIgnoreCase))
                return await FailAsync(destination, request, staging, StorageErrors.Conflict(
                    "The content does not have the expected SHA-256.",
                    $"expectedSha256={expectedDigest.ToLowerInvariant()};actualSha256={digest}"), contentIsWrong: true).ConfigureAwait(false);
            var confirmed = await ConfirmAsync(destination, staging, digest, total, cancellationToken).ConfigureAwait(false);
            if (confirmed.IsFailure)
                // Only a mismatch proves the staged bytes wrong; a confirm that could not run keeps them.
                return await FailAsync(destination, request, staging, confirmed.Error!, contentIsWrong: confirmed.Error!.Code == StorageErrors.ConflictCode).ConfigureAwait(false);
            verifiedBy = confirmed.Value;
        }

        return new StagedWriteResult(new StagedContent(staging, written.Value, offset, digest, verifiedBy), null, null, total);
    }

    /// <summary>
    /// Replaces or creates the destination with the staged object. A create (<paramref name="overwrite"/>
    /// false) is atomic where the destination declares <see cref="StorageFeature.ConditionalCreate"/>; a
    /// version condition is re-checked immediately before the move.
    /// </summary>
    public static async Task<(Result Result, StorageConditionEnforcement Enforcement)> PromoteAsync(
        IStorageService destination,
        string stagingPath,
        string path,
        bool overwrite,
        StorageMutationCondition? condition,
        bool createParents,
        CancellationToken cancellationToken)
    {
        var outcome = await PromoteCoreAsync(destination, stagingPath, path, overwrite, condition, createParents, cancellationToken).ConfigureAwait(false);
        return (outcome.Result, outcome.Enforcement);
    }

    /// <summary>
    /// Promotes the staged object and says what happened. A version condition is checked first and then passed
    /// to the provider's move, which enforces it in the same request where it can (and answers
    /// <c>storage.unsupported</c> where it cannot, in which case the checked-before move is used). A provider
    /// error that says the destination was committed (<see cref="StorageErrorInfo.DestinationStateKey"/>
    /// <c>=complete</c>) is a success with the internal objects it left behind, never a failure to roll back.
    /// </summary>
    public static async Task<PromoteOutcome> PromoteCoreAsync(
        IStorageService destination,
        string stagingPath,
        string path,
        bool overwrite,
        StorageMutationCondition? condition,
        bool createParents,
        CancellationToken cancellationToken)
    {
        var enforcement = StorageConditionEnforcement.None;
        var options = new StorageTransferOptions { Overwrite = overwrite, CreateParents = createParents };
        if (condition is { IsEmpty: false })
        {
            var check = await CheckConditionAsync(destination, path, condition, cancellationToken).ConfigureAwait(false);
            if (check.IsFailure) return new PromoteOutcome(check, StorageConditionEnforcement.CheckedBeforeCommit, Touched: false, []);
            enforcement = await StorageConditionEnforcements.ForAsync(destination, StorageConditionKind.MatchVersion, serverSideCopy: true, cancellationToken).ConfigureAwait(false);
            options = options with { Overwrite = true, DestinationCondition = condition };
        }
        else if (!overwrite)
        {
            enforcement = await StorageConditionEnforcements.ForAsync(destination, StorageConditionKind.CreateOnly, serverSideCopy: true, cancellationToken).ConfigureAwait(false);
        }
        var moved = await destination.MoveAsync(stagingPath, path, options, cancellationToken).ConfigureAwait(false);
        if (moved.IsFailure && options.DestinationCondition is not null && moved.Error!.Code == StorageErrors.UnsupportedCode)
        {
            // The provider cannot enforce the condition in its move: it was checked just before, which is all
            // this connection offers.
            enforcement = StorageConditionEnforcement.CheckedBeforeCommit;
            moved = await destination.MoveAsync(stagingPath, path, options with { DestinationCondition = null }, cancellationToken).ConfigureAwait(false);
        }
        if (moved.IsFailure && StorageErrorInfo.DestinationCommitted(moved.Error))
            return new PromoteOutcome(Result.Success(), enforcement, Touched: true, LeftBehind(moved.Error));
        return new PromoteOutcome(moved, enforcement, Touched: true, []);
    }

    /// <summary>Every <see cref="StorageErrorInfo.LeftBehindKey"/> entry in an error's details.</summary>
    internal static IReadOnlyList<string> LeftBehind(Error? error)
    {
        if (error?.Details is not { Length: > 0 } details) return [];
        var prefix = StorageErrorInfo.LeftBehindKey + "=";
        return [.. details.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.StartsWith(prefix, StringComparison.Ordinal) && part.Length > prefix.Length)
            .Select(part => part[prefix.Length..])];
    }

    /// <summary>Appends <c>key=value</c> details to an error, keeping what the provider put there.</summary>
    internal static Error AppendDetails(Error error, string details) =>
        string.IsNullOrEmpty(details) ? error
        : error.WithDetails(string.IsNullOrEmpty(error.Details) ? details : $"{error.Details};{details}");

    /// <summary>
    /// Confirms a promoted destination: it has the staged length and, where the server keeps a SHA-256, the
    /// digest that was verified. A rename does not change content, so nothing is read back. A mismatch is a
    /// <c>storage.conflict</c> carrying <c>destinationState=complete</c>: the destination was committed and
    /// must be reported, not rolled back (it may be another writer's newer content).
    /// </summary>
    public static async Task<Result<StorageItem>> ConfirmPromotedAsync(IStorageService destination, string path, StagedContent content, CancellationToken cancellationToken)
    {
        Result<StorageItem> info;
        try { info = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { return Result<StorageItem>.Failure(StorageErrors.FromException(error, "Confirm transfer destination")); }
        if (info.IsFailure) return info;
        var total = content.Bytes + content.BytesResumed;
        if (info.Value!.Size is { } size && size != total)
            return Result<StorageItem>.Failure(StorageErrors.Conflict(
                $"The destination '{path}' does not hold what was committed: it is {size} bytes, not {total}.",
                $"expectedLength={total};actualLength={size};{StorageErrorInfo.DestinationStateKey}=complete"));
        if (content.Sha256 is { } digest)
        {
            Result<StorageChecksum> server;
            try { server = await destination.GetServerChecksumAsync(path, StorageChecksumAlgorithm.Sha256, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { server = Result<StorageChecksum>.Failure(StorageErrors.FromException(error, "Confirm transfer destination")); }
            if (server.IsSuccess && !string.Equals(server.Value!.HexValue, digest, StringComparison.OrdinalIgnoreCase))
                return Result<StorageItem>.Failure(StorageErrors.Conflict(
                    $"The destination '{path}' does not hold what was committed.",
                    $"expectedSha256={digest};actualSha256={server.Value.HexValue.ToLowerInvariant()};{StorageErrorInfo.DestinationStateKey}=complete"));
        }
        return Result<StorageItem>.Success(info.Value with { Sha256 = content.Sha256 });
    }

    /// <summary>Succeeds when the destination exists and still has the expected ETag and version.</summary>
    public static async Task<Result> CheckConditionAsync(
        IStorageService destination,
        string path,
        StorageMutationCondition condition,
        CancellationToken cancellationToken)
    {
        var current = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return current.Error!.Code == StorageErrors.NotFoundCode
                ? Result.Failure(StorageErrors.Conflict($"The destination '{path}' no longer exists, so it is not the expected version."))
                : Result.Failure(current.Error);
        }
        if (condition.ExpectedETag is { } eTag && !SameETag(eTag, current.Value!.ETag))
            return Result.Failure(StorageErrors.Conflict($"The destination '{path}' changed: its ETag is no longer the expected one.",
                $"expectedETag={eTag};actualETag={current.Value.ETag}"));
        if (condition.ExpectedVersionId is { } version && !string.Equals(version, current.Value!.VersionId, StringComparison.Ordinal))
            return Result.Failure(StorageErrors.Conflict($"The destination '{path}' changed: its version is no longer the expected one.",
                $"expectedVersionId={version};actualVersionId={current.Value.VersionId}"));
        return Result.Success();
    }

    /// <summary>ETags compare without quotes or a weak prefix, as providers report them inconsistently.</summary>
    public static bool SameETag(string? expected, string? actual) =>
        expected is not null && actual is not null &&
        string.Equals(TrimETag(expected), TrimETag(actual), StringComparison.Ordinal);

    private static string TrimETag(string value) =>
        (value.StartsWith("W/", StringComparison.Ordinal) ? value[2..] : value).Trim('"');

    /// <summary>
    /// Deletes a staging object, ignoring one that is already gone. Never recursive: a staging object is a file,
    /// and a path that turns out to be a folder is not the library's to remove.
    /// </summary>
    public static async Task<Result> DeleteAsync(IStorageService destination, string stagingPath)
    {
        try
        {
            return await destination.DeleteAsync(
                stagingPath,
                new StorageDeleteOptions { IgnoreMissing = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result.Failure(StorageErrors.FromException(error, "Delete transfer staging object"));
        }
    }

    public static async Task<Result<string>> AllocateStagingPathAsync(
        IStorageService destination,
        string parent,
        CancellationToken cancellationToken,
        string namePrefix = ".cl-storage-transfer-")
    {
        for (var attempt = 0; attempt < StagingNameAttempts; attempt++)
        {
            var candidate = Combine(parent, $"{namePrefix}{Guid.NewGuid():N}.tmp");
            var exists = await destination.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure)
                return Result<string>.Failure(exists.Error!);
            if (!exists.Value)
                return Result<string>.Success(candidate);
        }
        return Result<string>.Failure(StorageErrors.Conflict("Unable to allocate a unique destination staging path."));
    }

    private static async Task<StagedWriteResult> FailAsync(
        IStorageService destination,
        StagedWriteRequest request,
        string staging,
        Error error,
        bool contentIsWrong)
    {
        // Wrong content cannot be resumed; a transport failure leaves a resumable prefix.
        if (request.Resumable && !contentIsWrong)
        {
            var info = await TryGetInfoAsync(destination, staging).ConfigureAwait(false);
            if (info.IsSuccess)
                return new StagedWriteResult(null, error, staging, info.Value!.Size ?? 0);
            return new StagedWriteResult(null, error, null, 0);
        }
        var deleted = await DeleteAsync(destination, staging).ConfigureAwait(false);
        return new StagedWriteResult(null, error, deleted.IsFailure ? staging : null, 0) { CleanupError = deleted.Error };
    }

    /// <summary>
    /// How many bytes an earlier attempt staged. Only a part file that is not there means none; a size that
    /// cannot be read fails the attempt (and keeps the part file) rather than appending the whole source
    /// after the existing prefix.
    /// </summary>
    private static async Task<Result<long>> ResumableOffsetAsync(IStorageService destination, string staging, long? expectedLength, CancellationToken cancellationToken)
    {
        var existing = await destination.GetInfoAsync(staging, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
            return existing.Error!.Code == StorageErrors.NotFoundCode ? Result<long>.Success(0) : Result<long>.Failure(existing.Error);
        if (existing.Value!.ItemType != StorageItemType.File)
            return Result<long>.Failure(StorageErrors.Conflict($"The staging path '{staging}' is not a file."));
        var size = existing.Value.Size ?? 0;
        var canAppend = destination is IStorageAppendService && destination.Capabilities.Supports(StorageFeature.Append);
        if (size > 0 && canAppend && (expectedLength is null || size <= expectedLength))
            return Result<long>.Success(size);
        await DeleteAsync(destination, staging).ConfigureAwait(false);
        return Result<long>.Success(0);
    }

    /// <summary>
    /// Opens the source from the start, hashes its first <paramref name="length"/> bytes into
    /// <paramref name="hash"/>, and checks them against the staged prefix. Returns the source positioned at
    /// <paramref name="length"/>, or a null stream when the prefix does not match, in which case the caller
    /// starts over. A staged prefix that cannot be read is a failure (the part file is kept), not a mismatch.
    /// </summary>
    private static async Task<Result<Stream>> OpenVerifiedTailAsync(
        IStorageService destination,
        string staging,
        long length,
        IncrementalHash hash,
        StagedSourceOpener open,
        CancellationToken cancellationToken)
    {
        using var stagedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var download = await destination.DownloadAsync(staging, new StorageDownloadOptions { Length = length }, cancellationToken).ConfigureAwait(false);
        if (download.IsFailure) return Result<Stream>.Failure(download.Error!);
        await using (var stagedStream = download.Value!)
        {
            if (await HashAsync(stagedStream, stagedHash, length, cancellationToken).ConfigureAwait(false) != length)
                return Result<Stream>.Success(null!);
        }

        var opened = await open(0, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure) return opened;
        var source = opened.Value!;
        var matched = false;
        try
        {
            matched = await HashAsync(source, hash, length, cancellationToken).ConfigureAwait(false) == length &&
                hash.GetCurrentHash().AsSpan().SequenceEqual(stagedHash.GetHashAndReset());
        }
        finally
        {
            if (!matched) await source.DisposeAsync().ConfigureAwait(false);
        }
        return Result<Stream>.Success(matched ? source : null!);
    }

    /// <summary>Hashes up to <paramref name="length"/> bytes of a stream and returns how many there were.</summary>
    private static async Task<long> HashAsync(Stream stream, IncrementalHash hash, long length, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(SegmentSize);
        try
        {
            long read = 0;
            while (read < length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(SegmentSize, length - read)), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
                read += count;
            }
            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Confirms the staged object by the server's SHA-256 where it keeps one, otherwise by reading it back.</summary>
    private static async Task<Result<string>> ConfirmAsync(IStorageService destination, string staging, string digest, long length, CancellationToken cancellationToken)
    {
        var server = await destination.GetServerChecksumAsync(staging, StorageChecksumAlgorithm.Sha256, cancellationToken).ConfigureAwait(false);
        if (server.IsSuccess)
        {
            return string.Equals(server.Value!.HexValue, digest, StringComparison.OrdinalIgnoreCase)
                ? Result<string>.Success("server")
                : Result<string>.Failure(StorageErrors.Conflict("The destination's SHA-256 does not match the content that was sent.",
                    $"expectedSha256={digest};actualSha256={server.Value.HexValue.ToLowerInvariant()}"));
        }
        var reread = await destination.ComputeChecksumAsync(staging, StorageChecksumAlgorithm.Sha256, mode: StorageChecksumMode.ComputeOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (reread.IsFailure)
            return Result<string>.Failure(reread.Error!);
        if (reread.Value!.BytesProcessed != length || !string.Equals(reread.Value.HexValue, digest, StringComparison.OrdinalIgnoreCase))
            return Result<string>.Failure(StorageErrors.Conflict("The destination does not hold the content that was sent.",
                $"expectedSha256={digest};actualSha256={reread.Value.HexValue.ToLowerInvariant()};expectedLength={length};actualLength={reread.Value.BytesProcessed}"));
        return Result<string>.Success("reread");
    }

    private static async Task<Result<long>> AppendAsync(
        IStorageService destination,
        string staging,
        GuardedReadStream source,
        StorageUploadOptions upload,
        long? total,
        string path,
        CancellationToken cancellationToken)
    {
        // An append bypasses the provider's upload entry, so the destination's speed limits apply here (unless
        // the caller's pipeline already applied them to the source).
        var progress = upload.Progress;
        var limits = upload.PipelineApplied ? TransferLimits.None : StorageTransferPipeline.LimitsFor(destination);
        Stream input = progress is null && !limits.LimitsUploads
            ? source
            : new Providers.MeteredStream(source, progress, total, path, leaveOpen: true, limits.Upload, limits.TotalUpload);
        Result<StorageItem> appended;
        try
        {
            appended = await destination.AppendAsync(staging, input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(input, source)) await input.DisposeAsync().ConfigureAwait(false);
        }
        if (appended.IsFailure) return Result<long>.Failure(appended.Error!);
        // A provider that stopped reading early must not look like the source ended.
        if (!source.Exceeded && await source.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) > 0)
            return Result<long>.Failure(StorageErrors.ProviderError("The destination reported the append complete before consuming the whole source."));
        return Result<long>.Success(source.BytesRead);
    }

    /// <summary>
    /// Streams <paramref name="source"/> into an upload through a bounded pipe (at most 1 MiB read ahead) and
    /// checks that the provider consumed everything it was given.
    /// </summary>
    internal static async Task<Result<long>> RelayAsync(
        Stream source,
        IStorageService destination,
        string stagingPath,
        StorageUploadOptions uploadOptions,
        CancellationToken cancellationToken)
    {
        var pipe = new Pipe(new PipeOptions(
            pool: MemoryPool<byte>.Shared,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.ThreadPool,
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            minimumSegmentSize: SegmentSize,
            useSynchronizationContext: false));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(source, pipe.Writer, linked.Token, cancellationToken);
        Result<StorageItem>? upload = null;
        Error? thrownUploadError = null;
        try
        {
            await using var relayStream = new CountingReadStream(pipe.Reader.AsStream(leaveOpen: true));
            try
            {
                upload = await destination.UploadAsync(stagingPath, relayStream, uploadOptions, linked.Token).ConfigureAwait(false);
                if (upload.Value.IsFailure || !producer.IsCompleted)
                    linked.Cancel();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                linked.Cancel();
                await ObserveProducerAsync(producer).ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                thrownUploadError = StorageErrors.FromException(error, "Upload transfer staging object");
                linked.Cancel();
            }

            var produced = await producer.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();
            // A source that failed makes the upload fail too; the source's error is the one that explains it.
            // (The producer reports "unavailable" only when the upload stopped reading first.)
            if (produced.IsFailure && produced.Error!.Code != StorageErrors.UnavailableCode)
                return Result<long>.Failure(produced.Error);
            if (thrownUploadError is not null)
                return Result<long>.Failure(thrownUploadError);
            if (upload?.IsFailure == true)
                return Result<long>.Failure(upload.Value.Error!);
            if (produced.IsFailure)
                return Result<long>.Failure(produced.Error!);
            if (relayStream.BytesRead != produced.Value)
                return Result<long>.Failure(StorageErrors.ProviderError(
                    "The destination provider reported upload success before consuming the complete transfer stream."));
            return Result<long>.Success(produced.Value);
        }
        finally
        {
            linked.Cancel();
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await ObserveProducerAsync(producer).ConfigureAwait(false);
        }
    }

    private static async Task<Result<long>> ProduceAsync(
        Stream source,
        PipeWriter writer,
        CancellationToken relayCancellationToken,
        CancellationToken callerCancellationToken)
    {
        long bytes = 0;
        Exception? completionError = null;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(SegmentSize)[..SegmentSize];
                var read = await source.ReadAsync(memory, relayCancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                writer.Advance(read);
                bytes = checked(bytes + read);
                var flush = await writer.FlushAsync(relayCancellationToken).ConfigureAwait(false);
                if (flush.IsCanceled)
                    relayCancellationToken.ThrowIfCancellationRequested();
                if (flush.IsCompleted)
                    return Result<long>.Failure(StorageErrors.Unavailable(
                        "The transfer destination stopped reading before the source reached end-of-stream."));
            }
            return Result<long>.Success(bytes);
        }
        catch (OperationCanceledException error) when (callerCancellationToken.IsCancellationRequested)
        {
            completionError = error;
            throw;
        }
        catch (OperationCanceledException error)
        {
            completionError = error;
            return Result<long>.Failure(StorageErrors.Unavailable(
                "The transfer stream stopped because the peer operation did not complete."));
        }
        catch (SourceTooLongException error)
        {
            completionError = error;
            return Result<long>.Failure(StorageErrors.Conflict("The source delivered more bytes than expected."));
        }
        catch (Exception error)
        {
            completionError = error;
            return Result<long>.Failure(StorageErrors.FromException(error, "Read transfer source"));
        }
        finally
        {
            await writer.CompleteAsync(completionError).ConfigureAwait(false);
        }
    }

    private static async Task ObserveProducerAsync(Task<Result<long>> producer)
    {
        try { _ = await producer.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
    }

    internal static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    internal static string Combine(string parent, string child) =>
        parent.Length == 0 ? child.TrimStart('/') : $"{parent.TrimEnd('/')}/{child.TrimStart('/')}";

    /// <summary>Raised when a source delivers more than its expected length.</summary>
    private sealed class SourceTooLongException() : IOException("The source delivered more bytes than expected.");

    /// <summary>Counts, hashes, and caps what is read from a source.</summary>
    private sealed class GuardedReadStream(Stream inner, IncrementalHash? hash, long? limit) : Stream
    {
        private long _read;

        public long BytesRead => Interlocked.Read(ref _read);
        public bool Exceeded { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Account(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Account(buffer.Span[..count]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Account(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;
            var total = Interlocked.Add(ref _read, data.Length);
            if (limit is { } max && total > max)
            {
                // Probing one byte past the expected end is how a longer source is detected.
                Exceeded = true;
                throw new SourceTooLongException();
            }
            hash?.AppendData(data);
            return data.Length;
        }
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        private long _bytesRead;

        internal long BytesRead => Interlocked.Read(ref _bytesRead);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
