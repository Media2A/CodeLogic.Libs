using System.Buffers;
using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>Relays storage transfers with bounded buffering and cleanup-safe staging objects.</summary>
internal static class StorageTransferCoordinator
{
    internal const long PauseWriterThreshold = 1_048_576;
    internal const long ResumeWriterThreshold = 524_288;
    internal const int SegmentSize = 65_536;
    private const int PageSize = 1_000;
    private const int StagingNameAttempts = 8;

    internal static async Task<Result<StorageTransferSummary>> CopyAsync(
        IStorageBackend source,
        string sourcePath,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        CancellationToken cancellationToken,
        TransferState? state = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        state ??= new TransferState();

        var sourceInfo = await ResolveSourceAsync(source, sourcePath, options.SourceVersionId, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure)
            return Result<StorageTransferSummary>.Failure(sourceInfo.Error!);
        state.Source = sourceInfo.Value;
        if (sourceInfo.Value!.ItemType != StorageItemType.File && options.HasSingleFileGuarantees)
            return Result<StorageTransferSummary>.Failure(StorageErrors.InvalidContent(
                "DestinationCondition, source pinning, ExpectedSourceLength, ExpectedSha256, and ResumeToken apply to single-file transfers only."));

        AggregateProgress? aggregate = null;
        if (options.Progress is { } progress)
        {
            long? totalBytes = sourceInfo.Value.ItemType == StorageItemType.File ? sourceInfo.Value.Size : null;
            long? totalFiles = sourceInfo.Value.ItemType == StorageItemType.File ? 1 : null;
            if (options.PreScan && sourceInfo.Value.ItemType == StorageItemType.Directory)
                (totalBytes, totalFiles) = await ScanAsync(source, sourcePath, cancellationToken).ConfigureAwait(false);
            aggregate = new AggregateProgress(progress, totalBytes, totalFiles);
            options = options with { Progress = aggregate };
        }

        if (ReferenceEquals(source, destination) && sourceInfo.Value!.ItemType == StorageItemType.Directory)
        {
            var relationship = StorageTransferPath.ValidateDirectoryDestination(sourcePath, destinationPath);
            if (relationship.IsFailure)
                return Result<StorageTransferSummary>.Failure(relationship.Error!);
        }

        var cleanup = new TransferCleanupTracker(destination, sourceInfo.Value!.ItemType == StorageItemType.Directory);
        Result<StorageTransferSummary> result;
        try
        {
            result = sourceInfo.Value!.ItemType switch
            {
                StorageItemType.File => await CopyFileAsync(
                    source,
                    sourceInfo.Value,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken,
                    state).ConfigureAwait(false),
                StorageItemType.Directory => await CopyDirectoryAsync(
                    source,
                    sourceInfo.Value,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken,
                    state).ConfigureAwait(false),
                _ => await TransferLinkAsync(
                    source,
                    sourceInfo.Value,
                    treeRoot: null,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelledRollback = await cleanup.RollbackAsync(KeptStaging(state)).ConfigureAwait(false);
            if (cleanup.HadReplacements) state.BackupRestored = cancelledRollback.IsSuccess;
            if (cancelledRollback.IsFailure)
            {
                // Not a clean cancel: part of the destination could not be put back.
                state.RollbackIncomplete = true;
                state.BackupLeftBehind ??= cleanup.RetainedBackup;
                return Result<StorageTransferSummary>.Failure(StorageErrors.Cancelled(
                    "The transfer was cancelled and destination rollback was incomplete.",
                    $"rollbackError={cancelledRollback.Error!.Code};{cancelledRollback.Error.Details}"));
            }
            throw;
        }
        catch (Exception error)
        {
            // A throwing provider, phase callback, or progress sink is a failure like any other: staging and
            // backups are settled below instead of leaking.
            result = Result<StorageTransferSummary>.Failure(StorageErrors.FromException(error, "Transfer"));
        }

        if (result.IsFailure)
        {
            state.DirectoriesCreated = cleanup.CreatedDirectories;
            var rollback = await cleanup.RollbackAsync(KeptStaging(state)).ConfigureAwait(false);
            if (cleanup.HadReplacements)
                state.BackupRestored = rollback.IsSuccess;
            if (rollback.IsFailure)
            {
                state.RollbackIncomplete = true;
                state.BackupLeftBehind ??= cleanup.RetainedBackup;
                return Result<StorageTransferSummary>.Failure(StorageErrors.PartialFailure(
                    "The transfer failed and destination rollback was incomplete.",
                    $"transferError={result.Error!.Code};rollbackError={rollback.Error!.Code};{rollback.Error.Details}"));
            }
        }
        else
        {
            // Committed. A replacement backup that cannot be removed is reported as left behind; the transfer
            // itself succeeded.
            var retained = await cleanup.CommitAsync().ConfigureAwait(false);
            result = Result<StorageTransferSummary>.Success(result.Value! with
            {
                BackupLeftBehind = result.Value.BackupLeftBehind ?? retained,
                CreatedDirectories = cleanup.CreatedDirectories
            });
            aggregate?.Complete();
        }
        return result;
    }

    /// <summary>A resumable part file kept for a token; the folders holding it are not rolled back.</summary>
    private static string? KeptStaging(TransferState state) => state.StagingResumable ? state.StagingLeftBehind : null;

    /// <summary>
    /// Describes the source as it will be read: the latest item, or with <paramref name="versionId"/> the pinned
    /// version's size, ETag, and time, so every check, the resume key, and a move's source deletion refer to the
    /// version that is actually copied.
    /// </summary>
    internal static async Task<Result<StorageItem>> ResolveSourceAsync(
        IStorageService source,
        string path,
        string? versionId,
        CancellationToken cancellationToken)
    {
        var latest = await source.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (versionId is null)
            return latest;
        if (latest.IsFailure)
        {
            // A delete marker as the latest version hides the object, but an older version can still be read.
            if (latest.Error!.Code != StorageErrors.NotFoundCode || source is not IStorageVersionService || !source.Capabilities.Supports(StorageFeature.Versioning))
                return latest;
            var name = path[(path.LastIndexOf('/') + 1)..];
            latest = Result<StorageItem>.Success(new StorageItem { Path = path, Name = name, ItemType = StorageItemType.File });
        }
        if (latest.Value!.ItemType != StorageItemType.File)
            return Result<StorageItem>.Failure(StorageErrors.InvalidContent("SourceVersionId applies to single-file transfers only."));
        if (latest.Value.VersionId == versionId)
            return latest;
        if (source is not IStorageVersionService versions || !source.Capabilities.Supports(StorageFeature.Versioning))
            return Result<StorageItem>.Failure(StorageErrors.Unsupported("The source connection cannot read a specific version."));
        string? token = null;
        do
        {
            var page = await versions.ListVersionsAsync(path, new StorageVersionListOptions { ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
                return Result<StorageItem>.Failure(page.Error!);
            if (page.Value!.Versions.FirstOrDefault(version => version.VersionId == versionId) is { } pinned)
            {
                return pinned.IsDeleteMarker
                    ? Result<StorageItem>.Failure(StorageErrors.NotFound($"The source version '{versionId}' of '{path}' is a delete marker."))
                    : Result<StorageItem>.Success(latest.Value with
                    {
                        Size = pinned.Size,
                        ETag = pinned.ETag,
                        LastModified = pinned.LastModified,
                        VersionId = pinned.VersionId
                    });
            }
            token = page.Value.ContinuationToken;
        } while (token is not null);
        return Result<StorageItem>.Failure(StorageErrors.NotFound($"The source version '{versionId}' of '{path}' was not found."));
    }

    /// <summary>
    /// Adds up per-file progress into one running total for a whole relayed transfer, with totals when they
    /// are known (a single file, or a pre-scanned directory).
    /// </summary>
    private sealed class AggregateProgress(IProgress<StorageTransferProgress> target, long? totalBytes, long? totalFiles) : IProgress<StorageTransferProgress>
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private long _completedBytes;
        private long _completedFiles;

        public void Report(StorageTransferProgress value)
        {
            var done = Interlocked.Read(ref _completedBytes) + value.BytesTransferred;
            if (value.IsCompleted)
            {
                Interlocked.Add(ref _completedBytes, value.BytesTransferred);
                Interlocked.Increment(ref _completedFiles);
            }
            var rate = Rate(done);
            target.Report(new StorageTransferProgress(done, totalBytes, false, rate, Remaining(done, rate), value.ItemPath)
            {
                FilesCompleted = Interlocked.Read(ref _completedFiles),
                FilesTotal = totalFiles
            });
        }

        /// <summary>Counts a file that took no bytes through the relay: skipped, or copied on the server.</summary>
        public void FileDone(string path, long bytes)
        {
            Interlocked.Add(ref _completedBytes, bytes);
            Interlocked.Increment(ref _completedFiles);
            var done = Interlocked.Read(ref _completedBytes);
            var rate = Rate(done);
            target.Report(new StorageTransferProgress(done, totalBytes, false, rate, Remaining(done, rate), path)
            {
                FilesCompleted = Interlocked.Read(ref _completedFiles),
                FilesTotal = totalFiles
            });
        }

        public void Complete()
        {
            var done = Interlocked.Read(ref _completedBytes);
            target.Report(new StorageTransferProgress(done, totalBytes ?? done, true, Rate(done), TimeSpan.Zero)
            {
                FilesCompleted = Interlocked.Read(ref _completedFiles),
                FilesTotal = totalFiles ?? Interlocked.Read(ref _completedFiles)
            });
        }

        private double Rate(long bytes) => _clock.Elapsed.TotalSeconds > 0 ? bytes / _clock.Elapsed.TotalSeconds : 0;

        private TimeSpan? Remaining(long done, double rate) =>
            totalBytes is { } total && rate > 0 && total >= done ? TimeSpan.FromSeconds((total - done) / rate) : null;
    }

    /// <summary>Lists a directory once to count its files and bytes for progress totals.</summary>
    private static async Task<(long? Bytes, long? Files)> ScanAsync(IStorageBackend source, string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        long files = 0;
        await foreach (var item in source.EnumerateItemsAsync(path, new StorageListOptions { Recursive = true }, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsFailure) return (null, null);
            if (item.Value!.ItemType != StorageItemType.File) continue;
            files++;
            bytes += item.Value.Size ?? 0;
        }
        return (bytes, files);
    }

    private static async Task<Result<StorageTransferSummary>> CopyDirectoryAsync(
        IStorageBackend source,
        StorageItem sourceDirectory,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken,
        TransferState state)
    {
        // A directory changes the destination as soon as its first folder or file lands.
        if (options.PhaseChanged is { } committing)
            await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);
        var destinationDirectory = await EnsureDirectoryAsync(
            destination,
            destinationPath,
            options.CreateParents,
            cleanup,
            cancellationToken).ConfigureAwait(false);
        if (destinationDirectory.IsFailure)
            return Result<StorageTransferSummary>.Failure(destinationDirectory.Error!);

        long files = 0;
        long directories = 1;
        long bytes = 0;
        long skipped = 0;
        var transferred = new List<string>();
        var transferredItems = new List<StorageItem>();
        var skippedSources = new List<string>();
        string? stagingLeft = null;
        string? backupLeft = null;
        var enforcement = StorageConditionEnforcement.None;
        string? continuationToken = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await source.ListAsync(
                sourceDirectory.Path,
                new StorageListOptions
                {
                    Recursive = true,
                    PageSize = PageSize,
                    ContinuationToken = continuationToken
                },
                cancellationToken).ConfigureAwait(false);
            if (page.IsFailure)
                return Result<StorageTransferSummary>.Failure(page.Error!);

            foreach (var item in page.Value!.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemPath = StoragePath.Normalize(item.Path);
                if (itemPath.IsFailure)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.ProviderError(
                        "The source provider returned an invalid path while listing a directory."));
                if (!seenItems.Add(itemPath.Value!))
                    continue;

                var relative = GetRelativePath(sourceDirectory.Path, itemPath.Value!);
                if (relative is null)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.ProviderError(
                        "The source provider returned an item outside the requested directory."));
                if (relative.Length == 0)
                    continue;
                var mappedPath = Combine(destinationPath, relative);

                switch (item.ItemType)
                {
                    case StorageItemType.Directory:
                        {
                            var directory = await EnsureDirectoryAsync(
                                destination,
                                mappedPath,
                                options.CreateParents,
                                cleanup,
                                cancellationToken).ConfigureAwait(false);
                            if (directory.IsFailure)
                                return Result<StorageTransferSummary>.Failure(directory.Error!);
                            directories++;
                            break;
                        }
                    case StorageItemType.File:
                        {
                            var parent = Parent(mappedPath);
                            if (parent.Length > 0)
                            {
                                var directory = await EnsureDirectoryAsync(
                                    destination,
                                    parent,
                                    options.CreateParents,
                                    cleanup,
                                    cancellationToken).ConfigureAwait(false);
                                if (directory.IsFailure)
                                    return Result<StorageTransferSummary>.Failure(directory.Error!);
                            }
                            var fileState = new TransferState();
                            var copied = await CopyFileAsync(
                                source,
                                item with { Path = itemPath.Value! },
                                destination,
                                mappedPath,
                                options,
                                cleanup,
                                cancellationToken,
                                fileState).ConfigureAwait(false);
                            // What each file committed, left behind, and how its condition held is the directory's too.
                            state.FilesCommitted += fileState.FilesCommitted;
                            state.BytesCommitted += fileState.BytesCommitted;
                            state.ConditionEnforcement = Weakest(state.ConditionEnforcement, fileState.ConditionEnforcement);
                            state.BackupLeftBehind ??= fileState.BackupLeftBehind;
                            if (!fileState.StagingResumable)
                                state.StagingLeftBehind ??= fileState.StagingLeftBehind;
                            if (copied.IsFailure)
                                return Result<StorageTransferSummary>.Failure(copied.Error!);
                            var file = copied.Value!;
                            stagingLeft ??= file.StagingLeftBehind;
                            backupLeft ??= file.BackupLeftBehind;
                            enforcement = Weakest(enforcement, file.ConditionEnforcement);
                            files += copied.Value!.Files;
                            bytes += copied.Value.Bytes;
                            skipped += copied.Value.SkippedFiles;
                            transferred.AddRange(copied.Value.TransferredSources ?? []);
                            transferredItems.AddRange(copied.Value.TransferredItems ?? []);
                            skippedSources.AddRange(copied.Value.SkippedSources ?? []);
                            break;
                        }
                    default:
                        {
                            var parent = Parent(mappedPath);
                            if (parent.Length > 0 && options.LinkHandling is StorageLinkHandling.Follow or StorageLinkHandling.Recreate)
                            {
                                var directory = await EnsureDirectoryAsync(
                                    destination,
                                    parent,
                                    options.CreateParents,
                                    cleanup,
                                    cancellationToken).ConfigureAwait(false);
                                if (directory.IsFailure)
                                    return Result<StorageTransferSummary>.Failure(directory.Error!);
                            }
                            var linked = await TransferLinkAsync(
                                source,
                                item with { Path = itemPath.Value! },
                                sourceDirectory.Path,
                                destination,
                                mappedPath,
                                options,
                                cleanup,
                                cancellationToken).ConfigureAwait(false);
                            if (linked.IsFailure)
                                return Result<StorageTransferSummary>.Failure(linked.Error!);
                            files += linked.Value!.Files;
                            bytes += linked.Value.Bytes;
                            skipped += linked.Value.SkippedFiles;
                            skippedSources.AddRange(linked.Value.SkippedSources ?? []);
                            // A recreated link was moved (an equivalent link exists at the destination), and so was
                            // a followed link's content; a skipped link stays behind on a move.
                            if (options.LinkHandling == StorageLinkHandling.Recreate)
                            {
                                transferred.Add(itemPath.Value!);
                                transferredItems.Add(item with { Path = itemPath.Value! });
                            }
                            else
                            {
                                transferred.AddRange(linked.Value.TransferredSources ?? []);
                                transferredItems.AddRange(linked.Value.TransferredItems ?? []);
                            }
                            break;
                        }
                }
            }

            var next = page.Value.ContinuationToken;
            if (string.IsNullOrEmpty(next))
            {
                continuationToken = null;
            }
            else if (!seenTokens.Add(next))
            {
                return Result<StorageTransferSummary>.Failure(StorageErrors.ProviderError(
                    "The source provider repeated a continuation token while listing a directory."));
            }
            else
            {
                continuationToken = next;
            }
        }
        while (continuationToken is not null);

        return Result<StorageTransferSummary>.Success(new StorageTransferSummary(
            sourceDirectory.ItemType,
            files,
            directories,
            bytes,
            skipped,
            transferred)
        {
            TransferredItems = transferredItems,
            SkippedSources = skippedSources,
            StagingLeftBehind = stagingLeft,
            BackupLeftBehind = backupLeft,
            ConditionEnforcement = enforcement
        });
    }

    /// <summary>The weaker of two enforcements: a check just before the commit outranks an atomic one, which outranks none.</summary>
    private static StorageConditionEnforcement Weakest(StorageConditionEnforcement a, StorageConditionEnforcement b) =>
        a == StorageConditionEnforcement.CheckedBeforeCommit || b == StorageConditionEnforcement.CheckedBeforeCommit
            ? StorageConditionEnforcement.CheckedBeforeCommit
            : a == StorageConditionEnforcement.Atomic || b == StorageConditionEnforcement.Atomic ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.None;

    /// <summary>
    /// Applies <see cref="StorageTransferOptions.LinkHandling"/> to one link. <c>treeRoot</c> is the source
    /// directory being transferred, used to remap link targets inside it, or null for a single link.
    /// </summary>
    private static async Task<Result<StorageTransferSummary>> TransferLinkAsync(
        IStorageBackend source,
        StorageItem link,
        string? treeRoot,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken)
    {
        switch (options.LinkHandling)
        {
            case StorageLinkHandling.Skip:
                // Counted as skipped, so a move leaves it where it is.
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.Link, 0, 0, 0, SkippedFiles: 1, TransferredSources: [])
                {
                    SkipReason = StorageSkipReason.Link,
                    SkippedSources = [link.Path]
                });

            case StorageLinkHandling.Follow:
            {
                // Some providers stat through the link and report the target's type.
                var target = await source.GetInfoAsync(link.Path, cancellationToken).ConfigureAwait(false);
                if (target.IsSuccess && target.Value!.ItemType == StorageItemType.Directory)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        $"Link '{link.Path}' points to a directory; following directory links is not supported."));
                var file = target.IsSuccess && target.Value!.ItemType == StorageItemType.File
                    ? target.Value with { Path = link.Path }
                    : link with { ItemType = StorageItemType.File, Size = null };
                return await CopyFileAsync(source, file, destination, destinationPath, options, cleanup, cancellationToken).ConfigureAwait(false);
            }

            case StorageLinkHandling.Recreate:
            {
                if (source is not IStorageAttributeService reader || !source.Capabilities.Supports(StorageFeature.ReadLinks))
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        "The source connection cannot read link targets, so links cannot be recreated."));
                if (destination is not IStorageAttributeService writer || !destination.Capabilities.Supports(StorageFeature.CreateLinks))
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        "The destination connection cannot create links."));
                var target = await reader.ReadLinkAsync(link.Path, cancellationToken).ConfigureAwait(false);
                if (target.IsFailure)
                    return Result<StorageTransferSummary>.Failure(target.Error!);
                if (target.Value!.StoragePath is not { } targetPath)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                        $"Link '{link.Path}' points outside the source root and cannot be recreated."));
                var mapped = targetPath;
                if (treeRoot is not null)
                {
                    var relative = GetRelativePath(treeRoot, targetPath);
                    if (relative is null)
                        return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                            $"Link '{link.Path}' points outside the transferred directory and cannot be recreated."));
                    mapped = Combine(DestinationTreeRoot(destinationPath, link.Path, treeRoot), relative);
                }
                var created = await writer.CreateLinkAsync(destinationPath, mapped, cancellationToken).ConfigureAwait(false);
                if (created.IsFailure)
                    return Result<StorageTransferSummary>.Failure(created.Error!);
                Result<StorageItem> createdLink;
                try { createdLink = await destination.GetInfoAsync(destinationPath, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { createdLink = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Read created link")); }
                cleanup.TrackFile(destinationPath, createdLink.IsSuccess ? createdLink.Value : null);
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.Link, 0, 0, 0));
            }

            default:
                return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                    $"Link '{link.Path}' was not transferred: link targets are provider-specific. Set LinkHandling to skip, follow, or recreate links."));
        }
    }

    /// <summary>Returns the destination directory that corresponds to <paramref name="treeRoot"/>.</summary>
    private static string DestinationTreeRoot(string destinationItemPath, string sourceItemPath, string treeRoot)
    {
        var relative = GetRelativePath(treeRoot, sourceItemPath) ?? string.Empty;
        var depth = relative.Length == 0 ? 0 : relative.Split('/').Length;
        var segments = destinationItemPath.Split('/');
        return string.Join('/', segments.Take(Math.Max(0, segments.Length - depth)));
    }

    private static async Task<Result<StorageTransferSummary>> CopyFileAsync(
        IStorageBackend source,
        StorageItem sourceFile,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken,
        TransferState? state = null)
    {
        state ??= new TransferState();
        var aggregate = options.Progress as AggregateProgress;
        var resumable = options.ConflictPolicy == StorageConflictPolicy.Resume || options.ResumeToken is not null;

        // The source must still be the version the caller planned with.
        if (options.ExpectedSourceETag is { } plannedETag && !StagedWriter.SameETag(plannedETag, sourceFile.ETag))
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                $"The source '{sourceFile.Path}' changed: its ETag is no longer the expected one.",
                $"expectedETag={plannedETag};actualETag={sourceFile.ETag}"));
        if (options.ExpectedSourceLength is { } plannedLength && sourceFile.Size is { } actualLength && actualLength != plannedLength)
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                $"The source '{sourceFile.Path}' is {actualLength} bytes; {plannedLength} were expected.",
                $"expectedLength={plannedLength};actualLength={actualLength}"));

        string? renamedFrom = null;
        if (options.ConflictPolicy is { } policy && policy != StorageConflictPolicy.Resume)
        {
            var decision = await StorageConflictResolver.ResolveAsync(
                destination,
                destinationPath,
                policy,
                options.Overwrite,
                sourceFile.Size,
                sourceFile.LastModified,
                cancellationToken).ConfigureAwait(false);
            if (decision.IsFailure)
                return Result<StorageTransferSummary>.Failure(decision.Error!);
            if (decision.Value.Skip)
            {
                aggregate?.FileDone(sourceFile.Path, 0);
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.File, 0, 0, 0, SkippedFiles: 1, TransferredSources: [])
                {
                    SkipReason = StorageConflictResolver.SkipReasonFor(policy),
                    Destination = decision.Value.Existing,
                    SkippedSources = [sourceFile.Path]
                });
            }
            if (policy == StorageConflictPolicy.Rename)
                renamedFrom = destinationPath;
            destinationPath = decision.Value.Path;
            options = options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null };
        }

        var exists = await destination.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (exists.IsFailure)
            return Result<StorageTransferSummary>.Failure(exists.Error!);
        var destinationExists = exists.Value;
        StorageItem? destinationItem = null;
        if (destinationExists)
        {
            var destinationInfo = await destination.GetInfoAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (destinationInfo.IsFailure)
                return Result<StorageTransferSummary>.Failure(destinationInfo.Error!);
            destinationItem = destinationInfo.Value!;
            if (destinationItem.ItemType != StorageItemType.File)
                return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                    $"The transfer destination '{destinationPath}' is not a file."));
        }
        if (resumable && destinationItem is not null && options.ResumeToken is null &&
            destinationItem.Size is { } present && present == sourceFile.Size &&
            await SameContentAsync(source, sourceFile, destination, destinationItem, options.SourceVersionId is not null, cancellationToken).ConfigureAwait(false))
        {
            // Resume: the destination already holds this content (equal digests, not merely an equal size).
            aggregate?.FileDone(sourceFile.Path, 0);
            return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.File, 0, 0, 0, SkippedFiles: 1, TransferredSources: [])
            {
                SkipReason = StorageSkipReason.AlreadyComplete,
                Destination = destinationItem,
                TransferredItems = [sourceFile]
            });
        }
        // Only the Resume policy replaces an existing destination by itself. A resume token continues staged
        // bytes but never turns overwriting on: Validate refuses a token with Overwrite off, and a destination
        // someone else created meanwhile is then a conflict, not something to replace.
        var overwrite = options.Overwrite || options.ConflictPolicy == StorageConflictPolicy.Resume;
        if (destinationExists && !overwrite)
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                $"The transfer destination '{destinationPath}' already exists."));
        if (options.DestinationCondition is { IsEmpty: false } condition)
        {
            var check = await StagedWriter.CheckConditionAsync(destination, destinationPath, condition, cancellationToken).ConfigureAwait(false);
            if (check.IsFailure)
            {
                state.ConditionEnforcement = StorageConditionEnforcement.CheckedBeforeCommit;
                return Result<StorageTransferSummary>.Failure(check.Error!);
            }
        }

        var parent = Parent(destinationPath);
        if (parent.Length > 0)
        {
            var parentResult = await EnsureDirectoryAsync(
                destination,
                parent,
                options.CreateParents,
                cleanup,
                cancellationToken).ConfigureAwait(false);
            if (parentResult.IsFailure)
                return Result<StorageTransferSummary>.Failure(parentResult.Error!);
        }

        IReadOnlyDictionary<string, string> transferredMetadata = sourceFile.Metadata;
        if (options.MetadataPreservation == StorageMetadataPreservation.Discard)
        {
            transferredMetadata = new Dictionary<string, string>();
        }
        else if (sourceFile.Metadata.Count > 0 &&
                 !destination.Capabilities.Supports(StorageFeature.MetadataWrite))
        {
            if (options.MetadataPreservation == StorageMetadataPreservation.Require)
            {
                return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                    "The destination cannot preserve source user metadata."));
            }
            transferredMetadata = new Dictionary<string, string>();
        }

        if (options.PhaseChanged is { } transferring)
            await transferring(Queue.StorageTransferPhase.Transferring, cancellationToken).ConfigureAwait(false);

        StagedContent staged;
        var stagedResumable = false;
        PartLease? lease = null;
        var canUseNativeStagingCopy = ReferenceEquals(source, destination) &&
            source.Capabilities.Supports(StorageFeature.FileCopy | StorageFeature.ServerSideCopy) &&
            options.MetadataPreservation != StorageMetadataPreservation.Discard &&
            !resumable && !options.Verify && options.ExpectedSha256 is null &&
            options.ExpectedSourceLength is null && options.SourceVersionId is null;
        if (canUseNativeStagingCopy)
        {
            var allocated = await AllocateStagingPathAsync(destination, parent, cancellationToken).ConfigureAwait(false);
            if (allocated.IsFailure)
                return Result<StorageTransferSummary>.Failure(allocated.Error!);
            var stagingPath = allocated.Value!;
            Result copied;
            try
            {
                copied = await destination.CopyAsync(
                    sourceFile.Path,
                    stagingPath,
                    new StorageTransferOptions
                    {
                        Overwrite = false,
                        CreateParents = options.CreateParents,
                        MetadataPreservation = options.MetadataPreservation
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                copied = Result.Failure(StorageErrors.FromException(error, "Copy transfer staging object"));
            }
            if (copied.IsFailure && StorageErrorInfo.DestinationCommitted(copied.Error))
                copied = Result.Success(); // the staging copy exists; only an internal leftover remains
            if (copied.IsFailure)
            {
                var stagingCleanup = await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
                if (stagingCleanup.IsFailure) state.StagingLeftBehind = stagingPath;
                return FailureAfterCleanup(copied.Error!, "The transfer upload failed", stagingCleanup);
            }
            // A server-side copy moves no bytes through the client; report it as one step.
            aggregate?.FileDone(sourceFile.Path, sourceFile.Size ?? 0);
            staged = new StagedContent(stagingPath, sourceFile.Size ?? 0, 0, null, null);
        }
        else
        {
            string? resumeKey = null;
            string? tokenStaging = null;
            // Staged bytes are only reused for a source that can be identified (an ETag, a time, or a version);
            // otherwise a different source of the same length would be appended onto an old prefix. A weak ETag
            // (the local provider's write time, creation time and length) cannot prove the content is unchanged
            // on a coarse file-system clock, so such a source resumes only when Verify re-reads the staged prefix.
            if (resumable && HasIdentity(sourceFile) && (!StorageETags.IsWeak(sourceFile.ETag) || options.Verify))
            {
                resumeKey = StagedWriter.SourceKey(sourceFile.Path, sourceFile.Size, sourceFile.LastModified, sourceFile.ETag, sourceFile.VersionId);
                var expectedStaging = StagedWriter.ResumableStagingPath(destinationPath, resumeKey);
                if (options.ResumeToken is { } token)
                {
                    if (token.DestinationPath == destinationPath && IsSameSource(token, sourceFile) && token.StagingPath == expectedStaging)
                        tokenStaging = token.StagingPath;
                    else if (token.StagingPath != expectedStaging && IsOwnPartFile(token, destinationPath) && !await StagedWriter.IsPartInUseAsync(destination, token.StagingPath).ConfigureAwait(false))
                        // The token's own part file for this destination, staged from a source version that is gone.
                        // A token naming any other file is ignored, never followed: it may be another job's.
                        await DeleteStagingAsync(destination, token.StagingPath).ConfigureAwait(false);
                }
            }

            var request = new StagedWriteRequest
            {
                Path = destinationPath,
                Upload = new StorageUploadOptions
                {
                    CreateParents = options.CreateParents,
                    ContentType = sourceFile.ContentType,
                    Metadata = transferredMetadata,
                    Progress = options.Progress is { } progress ? new PathProgress(progress, sourceFile.Path) : null
                },
                // A resumable write knows the source's length, so a fully staged part file is recognised as
                // complete and a source that grew is not appended onto it.
                ExpectedLength = options.ExpectedSourceLength ?? (resumable ? sourceFile.Size : null),
                Verify = options.Verify,
                ExpectedSha256 = options.ExpectedSha256,
                ResumeKey = tokenStaging is null ? resumeKey : null,
                StagingPath = tokenStaging
            };
            var versionId = options.SourceVersionId;
            var written = await StagedWriter.WriteAsync(
                destination,
                request,
                (offset, token) => source.DownloadAsync(
                    sourceFile.Path,
                    new StorageDownloadOptions { Offset = offset, VersionId = versionId },
                    token),
                cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess)
            {
                state.StagingLeftBehind = written.StagingLeft;
                // Kept staged bytes are resumable only when they are a part file (never a private staging object).
                state.StagingResumable = written.Resumable && written.StagingLeft is not null && written.CleanupError is null;
                state.BytesStaged = written.BytesStaged;
                return written.CleanupError is { } cleanupError
                    ? FailureAfterCleanup(written.Error!, "The transfer upload failed", Result.Failure(cleanupError))
                    : Result<StorageTransferSummary>.Failure(written.Error!);
            }
            staged = written.Content!;
            stagedResumable = written.Resumable;
            lease = written.Lease;
        }

        var committed = false;
        var promoteStarted = false;
        string? backupPath = null;
        var renamedBackup = false;
        try
        {
            // A source without a pinned version is read again: it must not have changed while it streamed.
            if (options.ExpectedSourceETag is not null && options.SourceVersionId is null)
            {
                var after = await source.GetInfoAsync(sourceFile.Path, cancellationToken).ConfigureAwait(false);
                if (after.IsFailure)
                {
                    // Not evidence of a change: the staged bytes stay for a resume.
                    var kept = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                    return FailureAfterCleanup(after.Error!, "The transfer could not re-read its source", kept);
                }
                if (!StagedWriter.SameETag(options.ExpectedSourceETag, after.Value!.ETag))
                {
                    await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                        $"The source '{sourceFile.Path}' changed while it was being copied.",
                        $"expectedETag={options.ExpectedSourceETag};actualETag={after.Value.ETag}"));
                }
            }

            if (lease is not null)
            {
                // The part file is promoted only while this transfer still holds it: one taken over by another
                // transfer (its owner was judged gone) is theirs, and is neither promoted nor removed.
                var owned = await lease.StillOwnedAsync(cancellationToken).ConfigureAwait(false);
                if (owned.IsFailure)
                {
                    var kept = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                    return FailureAfterCleanup(owned.Error!, "The transfer could not confirm its hold on the staged data", kept);
                }
                if (!owned.Value)
                    return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                        $"The staged data for '{destinationPath}' was taken over by another transfer, so it was not committed.",
                        $"stagingPath={staged.StagingPath}"));
            }

            if (options.PhaseChanged is { } committing)
                await committing(Queue.StorageTransferPhase.Committing, cancellationToken).ConfigureAwait(false);

            if (destinationExists)
            {
                // The previous destination is kept until the new one is committed. Where the server copies, the
                // backup costs no transfer; on FTP and SFTP (no server-side copy, but a replace that keeps the old
                // file until the new one is in place) a single file needs none, and a directory transfer (which may
                // roll back) renames it aside instead of copying it through the client. Any other connection
                // without a server-side copy renames it aside too: its replace is not known to restore anything.
                var serverCopy = destination.Capabilities.Supports(StorageFeature.FileCopy | StorageFeature.ServerSideCopy);
                var restoringReplace = destination is Providers.IStorageRestoringReplace;
                var renameAside = !serverCopy && (cleanup.ForDirectory || !restoringReplace) && destination.Capabilities.Supports(StorageFeature.FileMove);
                if (serverCopy || renameAside)
                {
                    var allocatedBackup = await AllocateStagingPathAsync(
                        destination,
                        parent,
                        cancellationToken,
                        ".cl-storage-transfer-backup-").ConfigureAwait(false);
                    if (allocatedBackup.IsFailure)
                    {
                        var stagingCleanup = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                        return FailureAfterCleanup(
                            allocatedBackup.Error!,
                            "The transfer could not allocate a replacement backup",
                            stagingCleanup);
                    }
                    backupPath = allocatedBackup.Value!;
                    renamedBackup = renameAside;
                    var backupOptions = new StorageTransferOptions { Overwrite = false, CreateParents = false };
                    var backedUp = renameAside
                        ? await destination.MoveAsync(destinationPath, backupPath, backupOptions, cancellationToken).ConfigureAwait(false)
                        : await destination.CopyAsync(destinationPath, backupPath, backupOptions, cancellationToken).ConfigureAwait(false);
                    if (backedUp.IsFailure && StorageErrorInfo.DestinationCommitted(backedUp.Error))
                        backedUp = Result.Success();
                    if (backedUp.IsFailure)
                    {
                        var stagingCleanup = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                        var backupCleanup = renameAside
                            ? await RecoverReplacementAsync(destination, destinationPath, backupPath, renamed: true).ConfigureAwait(false)
                            : await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                        if (backupCleanup.IsFailure) state.BackupLeftBehind = backupPath;
                        return FailureAfterCleanup(
                            backedUp.Error!,
                            "The transfer could not back up the previous destination",
                            stagingCleanup,
                            backupCleanup);
                    }
                }
            }

            promoteStarted = true;
            var outcome = await StagedWriter.PromoteCoreAsync(
                destination,
                staged.StagingPath,
                destinationPath,
                overwrite && !renamedBackup,
                options.DestinationCondition,
                options.CreateParents,
                cancellationToken).ConfigureAwait(false);
            // Rename: a name taken between the choice and the commit is not a failure; the next free one is used.
            for (var retry = 0; renamedFrom is not null && retry < RenameRetries &&
                outcome.Result.IsFailure && outcome.Result.Error!.Code == StorageErrors.ConflictCode; retry++)
            {
                var next = await StorageConflictResolver.ResolveAsync(
                    destination, renamedFrom, StorageConflictPolicy.Rename, false, sourceFile.Size, sourceFile.LastModified, cancellationToken).ConfigureAwait(false);
                if (next.IsFailure || next.Value.Path == destinationPath)
                    break;
                destinationPath = next.Value.Path;
                outcome = await StagedWriter.PromoteCoreAsync(
                    destination, staged.StagingPath, destinationPath, false, null, options.CreateParents, cancellationToken).ConfigureAwait(false);
            }
            state.ConditionEnforcement = outcome.Enforcement;
            if (outcome.Result.IsFailure)
            {
                var stagingCleanup = await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
                if (backupPath is not null)
                {
                    if (!outcome.Touched && !renamedBackup)
                    {
                        // Refused before anything was moved (a failed condition): the destination is exactly as the
                        // refusal found it, even if someone else deleted it, so the backup is only dropped.
                        var dropped = await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                        if (dropped.IsFailure) state.BackupLeftBehind = backupPath;
                        return FailureAfterCleanup(outcome.Result.Error!, "The transfer commit failed", stagingCleanup, dropped);
                    }
                    var restored = await RecoverReplacementAsync(destination, destinationPath, backupPath, renamedBackup).ConfigureAwait(false);
                    state.BackupRestored = restored.IsSuccess;
                    if (restored.IsFailure) state.BackupLeftBehind = backupPath;
                    return FailureAfterCleanup(
                        outcome.Result.Error!,
                        "The transfer commit failed",
                        stagingCleanup,
                        restored);
                }
                return FailureAfterCleanup(outcome.Result.Error!, "The transfer commit failed", stagingCleanup);
            }

            committed = true;
            state.DestinationCommitted = true;
            state.FilesCommitted = 1;
            state.BytesCommitted = staged.Bytes;
            // What the provider could not remove after it committed: our staging object or its own backup.
            var stagingLeft = outcome.LeftBehind.FirstOrDefault(path => path == staged.StagingPath);
            var providerLeft = outcome.LeftBehind.FirstOrDefault(path => path != staged.StagingPath);
            // Committed: the result is reported even if the caller cancels now, and nothing after this point rolls
            // the destination back. A verified copy also confirms the promoted destination, not only the staging
            // object; a destination that does not hold what was committed always fails (it may be another
            // writer's content, so it is reported and left as it is).
            var confirmed = await StagedWriter.ConfirmPromotedAsync(destination, destinationPath, staged, CancellationToken.None).ConfigureAwait(false);
            if (confirmed.IsFailure && confirmed.Error!.Code == StorageErrors.ConflictCode)
            {
                // The mismatch may be a concurrent writer or a broken promote: either way the previous version is
                // kept, never deleted and never put back, and the report names it.
                if (backupPath is not null)
                    state.BackupLeftBehind = backupPath;
                state.BackupLeftBehind ??= providerLeft;
                state.StagingLeftBehind = stagingLeft;
                return Result<StorageTransferSummary>.Failure(confirmed.Error);
            }
            if (backupPath is not null)
                cleanup.TrackReplacement(destinationPath, backupPath, renamedBackup, confirmed.IsSuccess ? confirmed.Value : null);
            else if (!destinationExists)
                cleanup.TrackFile(destinationPath, confirmed.IsSuccess ? confirmed.Value : null);
            return Result<StorageTransferSummary>.Success(new StorageTransferSummary(
                StorageItemType.File,
                Files: 1,
                Directories: 0,
                Bytes: staged.Bytes,
                TransferredSources: [sourceFile.Path])
            {
                WrittenPath = destinationPath,
                Sha256 = staged.Sha256,
                VerifiedBy = staged.VerifiedBy,
                Destination = confirmed.IsSuccess ? confirmed.Value : null,
                BytesResumed = staged.BytesResumed,
                ConditionEnforcement = outcome.Enforcement,
                TransferredItems = [sourceFile],
                StagingLeftBehind = stagingLeft,
                BackupLeftBehind = providerLeft
            });
        }
        catch (Exception error) when (!committed)
        {
            // Nothing was committed: the staging object goes (or stays, to resume from), and a replaced
            // destination keeps its content. A backup is copied back only when the promote may have removed
            // the destination; before that, it is only dropped.
            await SettleStagingAsync(destination, staged, stagedResumable, state).ConfigureAwait(false);
            if (backupPath is not null)
            {
                var settled = promoteStarted || renamedBackup
                    ? await RecoverReplacementAsync(destination, destinationPath, backupPath, renamedBackup).ConfigureAwait(false)
                    : await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                if (settled.IsFailure) state.BackupLeftBehind = backupPath;
            }
            return Result<StorageTransferSummary>.Failure(error is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? StorageErrors.Cancelled("The transfer was cancelled.")
                : StorageErrors.FromException(error, "Commit transfer"));
        }
        finally
        {
            // Held until the part file was promoted or settled, so nobody appended to it in between.
            if (lease is not null)
                await lease.ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How often a Rename transfer tries the next free name when its chosen one is taken at commit.</summary>
    private const int RenameRetries = 8;

    /// <summary>
    /// Keeps a resumable part file (and records it for a token) or removes a staging object. A part file that
    /// is already gone is simply not reported.
    /// </summary>
    private static async Task<Result> SettleStagingAsync(IStorageBackend destination, StagedContent staged, bool resumable, TransferState state)
    {
        if (resumable)
        {
            Result<StorageItem> kept;
            try { kept = await destination.GetInfoAsync(staged.StagingPath, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { kept = Result<StorageItem>.Failure(StorageErrors.FromException(error, "Read transfer staging object")); }
            if (kept.IsSuccess)
            {
                state.StagingLeftBehind = staged.StagingPath;
                state.StagingResumable = true;
                state.BytesStaged = kept.Value!.Size ?? staged.Bytes + staged.BytesResumed;
                return Result.Success();
            }
            if (kept.Error!.Code == StorageErrors.NotFoundCode)
                return Result.Success();
        }
        var deleted = await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
        if (deleted.IsFailure) state.StagingLeftBehind = staged.StagingPath;
        return deleted;
    }

    /// <summary>
    /// Whether a token's staging path is the part file its own fields derive for <paramref name="destinationPath"/>:
    /// only then may the library delete it as stale. Any other path in a token is not the library's to touch.
    /// </summary>
    private static bool IsOwnPartFile(StorageResumeToken token, string destinationPath) =>
        token.DestinationPath == destinationPath &&
        token.StagingPath == StagedWriter.ResumableStagingPath(
            token.DestinationPath,
            StagedWriter.SourceKey(token.SourcePath, token.SourceLength, token.SourceLastModified, token.SourceETag, token.SourceVersionId));

    /// <summary>Whether an item carries anything that tells one version of its content from another.</summary>
    private static bool HasIdentity(StorageItem item) =>
        item.ETag is not null || item.LastModified is not null || item.VersionId is not null;

    /// <summary>
    /// Whether a resume token's staged bytes came from the current source: every identity field must match
    /// exactly, and a field missing on one side only is a mismatch, not a wildcard.
    /// </summary>
    private static bool IsSameSource(StorageResumeToken token, StorageItem source) =>
        HasIdentity(source) &&
        token.SourcePath == source.Path &&
        token.SourceLength == source.Size &&
        token.SourceVersionId == source.VersionId &&
        (token.SourceETag is null ? source.ETag is null : StagedWriter.SameETag(token.SourceETag, source.ETag)) &&
        token.SourceLastModified == source.LastModified;

    /// <summary>
    /// Whether the destination provably holds the source's content: both sides report the same digest for
    /// a common algorithm. Without that evidence the content is written again rather than assumed complete.
    /// </summary>
    internal static async Task<bool> SameContentAsync(
        IStorageService source,
        StorageItem sourceFile,
        IStorageService destination,
        StorageItem destinationItem,
        bool pinnedVersion,
        CancellationToken cancellationToken)
    {
        // A server digest describes the latest version, so it proves nothing about a pinned older one.
        if (pinnedVersion)
            return false;
        foreach (var algorithm in new[] { StorageChecksumAlgorithm.Sha256, StorageChecksumAlgorithm.Md5 })
        {
            var ours = await destination.GetServerChecksumAsync(destinationItem.Path, algorithm, cancellationToken).ConfigureAwait(false);
            if (ours.IsFailure) continue;
            var theirs = await source.GetServerChecksumAsync(sourceFile.Path, algorithm, cancellationToken).ConfigureAwait(false);
            if (theirs.IsFailure) continue;
            return string.Equals(ours.Value!.HexValue, theirs.Value!.HexValue, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string Name(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static async Task<Result> EnsureDirectoryAsync(
        IStorageBackend destination,
        string path,
        bool createParents,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken)
    {
        if (path.Length == 0)
            return Result.Success();
        var exists = await destination.ExistsAsync(path, cancellationToken).ConfigureAwait(false);
        if (exists.IsFailure)
            return Result.Failure(exists.Error!);
        if (exists.Value)
        {
            var info = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
            if (info.IsFailure)
                return Result.Failure(info.Error!);
            return info.Value!.ItemType == StorageItemType.Directory
                ? Result.Success()
                : Result.Failure(StorageErrors.Conflict(
                    $"The transfer path '{path}' is not a directory."));
        }

        var parent = Parent(path);
        if (parent.Length > 0)
        {
            if (!createParents)
            {
                var parentExists = await destination.ExistsAsync(parent, cancellationToken).ConfigureAwait(false);
                if (parentExists.IsFailure)
                    return Result.Failure(parentExists.Error!);
                if (!parentExists.Value)
                    return Result.Failure(StorageErrors.NotFound(
                        $"The transfer destination parent '{parent}' was not found."));
            }
            else
            {
                var ensuredParent = await EnsureDirectoryAsync(
                    destination,
                    parent,
                    createParents: true,
                    cleanup,
                    cancellationToken).ConfigureAwait(false);
                if (ensuredParent.IsFailure)
                    return ensuredParent;
            }
        }

        var created = await destination.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
            return created;
        cleanup.TrackDirectory(path);
        return Result.Success();
    }

    private static Task<Result<string>> AllocateStagingPathAsync(
        IStorageBackend destination,
        string parent,
        CancellationToken cancellationToken,
        string namePrefix = ".cl-storage-transfer-") =>
        StagedWriter.AllocateStagingPathAsync(destination, parent, cancellationToken, namePrefix);

    private static Task<Result> DeleteStagingAsync(IStorageBackend destination, string stagingPath) =>
        StagedWriter.DeleteAsync(destination, stagingPath);

    private static Result<StorageTransferSummary> FailureAfterCleanup(
        Error primary,
        string message,
        params Result[] cleanupResults)
    {
        var cleanupErrors = cleanupResults
            .Where(result => result.IsFailure)
            .Select(result => result.Error!.Code)
            .ToArray();
        return cleanupErrors.Length == 0
            ? Result<StorageTransferSummary>.Failure(primary)
            : Result<StorageTransferSummary>.Failure(StorageErrors.PartialFailure(
                $"{message}, and cleanup was incomplete.",
                $"primaryError={primary.Code};cleanupErrors={string.Join(',', cleanupErrors)}"));
    }

    /// <summary>
    /// Settles the backup after a promote that did not complete (or may not have). Someone else may have
    /// written the destination since it was backed up, so the backup is put back only when the destination is
    /// gone — a copied backup is copied back create-only, a renamed one renamed back; a destination that is
    /// still the backed-up version, or someone else's newer write, is left alone and the backup removed.
    /// </summary>
    private static async Task<Result> RecoverReplacementAsync(
        IStorageBackend destination,
        string destinationPath,
        string backupPath,
        bool renamed)
    {
        try
        {
            var current = await destination.GetInfoAsync(destinationPath, CancellationToken.None).ConfigureAwait(false);
            if (current.IsFailure && current.Error!.Code != StorageErrors.NotFoundCode)
                return Result.Failure(current.Error);
            if (current.IsFailure)
            {
                var putBack = new StorageTransferOptions { Overwrite = false, CreateParents = false };
                var restored = renamed
                    ? await destination.MoveAsync(backupPath, destinationPath, putBack, CancellationToken.None).ConfigureAwait(false)
                    : await destination.CopyAsync(backupPath, destinationPath, putBack, CancellationToken.None).ConfigureAwait(false);
                if (restored.IsFailure && !StorageErrorInfo.DestinationCommitted(restored.Error))
                    return restored;
                if (renamed)
                    return Result.Success();
            }
            return await destination.DeleteAsync(
                backupPath,
                new StorageDeleteOptions { IgnoreMissing = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result.Failure(StorageErrors.FromException(error, "Restore transfer destination"));
        }
    }

    /// <summary>
    /// Whether <paramref name="current"/> is still the version <paramref name="committed"/> describes: the same
    /// ETag or version where both have one, otherwise the same size and modification time.
    /// </summary>
    internal static bool SameVersion(StorageItem committed, StorageItem current)
    {
        if (committed.ETag is not null && current.ETag is not null)
        {
            // A strong match proves the version; any mismatch proves a change. Weak validators that match prove
            // nothing more than size and time do, so they fall through to that comparison.
            if (!StorageETags.WeakEquals(committed.ETag, current.ETag)) return false;
            if (!StorageETags.IsWeak(committed.ETag) && !StorageETags.IsWeak(current.ETag)) return true;
        }
        if (committed.VersionId is not null && current.VersionId is not null)
            return string.Equals(committed.VersionId, current.VersionId, StringComparison.Ordinal);
        return committed.Size == current.Size && committed.LastModified == current.LastModified;
    }

    private static string? GetRelativePath(string root, string candidate)
    {
        if (root.Length == 0)
            return candidate.TrimStart('/');
        if (string.Equals(root, candidate, StringComparison.Ordinal))
            return string.Empty;
        var prefix = root + "/";
        return candidate.StartsWith(prefix, StringComparison.Ordinal)
            ? candidate[prefix.Length..]
            : null;
    }

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static string Combine(string parent, string child) =>
        parent.Length == 0 ? child.TrimStart('/') : $"{parent.TrimEnd('/')}/{child.TrimStart('/')}";

    /// <summary>
    /// Records what a transfer created or replaced so a failed directory transfer can be undone. Each committed
    /// file carries the identity it had when it was committed; rolling back touches it only while it still has
    /// that identity, so a write someone else made after the commit is never removed or overwritten.
    /// </summary>
    private sealed class TransferCleanupTracker(IStorageBackend destination, bool forDirectory)
    {
        private readonly List<(string Path, StorageItem? Committed)> _createdFiles = [];
        private readonly List<string> _createdDirectories = [];
        private readonly List<(string DestinationPath, string BackupPath, bool Renamed, StorageItem? Committed)> _replacements = [];

        /// <summary>Whether this is a directory transfer, which may roll back files it already committed.</summary>
        internal bool ForDirectory => forDirectory;
        internal bool HadReplacements { get; private set; }
        internal string? RetainedBackup { get; private set; }
        internal long CreatedDirectories => _createdDirectories.Count;

        internal void TrackFile(string path, StorageItem? committed) => _createdFiles.Add((path, committed));
        internal void TrackDirectory(string path) => _createdDirectories.Add(path);
        internal void TrackReplacement(string destinationPath, string backupPath, bool renamed, StorageItem? committed)
        {
            HadReplacements = true;
            _replacements.Add((destinationPath, backupPath, renamed, committed));
        }

        /// <summary>Removes the backups of a committed transfer; returns a backup that could not be removed.</summary>
        internal async Task<string?> CommitAsync()
        {
            string? retained = null;
            foreach (var replacement in _replacements)
            {
                Result deleted;
                try
                {
                    deleted = await destination.DeleteAsync(
                        replacement.BackupPath,
                        new StorageDeleteOptions { IgnoreMissing = true },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    deleted = Result.Failure(StorageErrors.FromException(error, "Delete transfer backup"));
                }
                if (deleted.IsFailure)
                    retained ??= replacement.BackupPath;
            }
            _replacements.Clear();
            RetainedBackup = retained;
            return retained;
        }

        /// <summary>
        /// Undoes the transfer. <paramref name="keep"/> is a resumable part file kept for a token: the folders
        /// holding it stay, or the rollback would fail on them.
        /// </summary>
        internal async Task<Result> RollbackAsync(string? keep = null)
        {
            var fileCleanupErrors = new List<string>();
            var restoreErrors = new List<string>();
            var directoryCleanupErrors = new List<string>();
            foreach (var (path, committed) in _createdFiles.AsEnumerable().Reverse())
            {
                var deleted = await DeleteIfStillOursAsync(path, committed).ConfigureAwait(false);
                if (deleted.IsFailure)
                    fileCleanupErrors.Add(deleted.Error!.Code);
            }
            foreach (var replacement in _replacements.AsEnumerable().Reverse())
            {
                var restored = await RestoreIfStillOursAsync(replacement.DestinationPath, replacement.BackupPath, replacement.Renamed, replacement.Committed).ConfigureAwait(false);
                if (restored.IsFailure)
                {
                    restoreErrors.Add(restored.Error!.Code);
                    RetainedBackup ??= replacement.BackupPath;
                }
            }
            foreach (var path in _createdDirectories.AsEnumerable().Reverse())
            {
                if (keep is not null && keep.StartsWith(path + "/", StringComparison.Ordinal))
                    continue;
                var deleted = await DeleteAsync(path).ConfigureAwait(false);
                if (deleted.IsFailure)
                    directoryCleanupErrors.Add(deleted.Error!.Code);
            }

            return fileCleanupErrors.Count == 0 && restoreErrors.Count == 0 && directoryCleanupErrors.Count == 0
                ? Result.Success()
                : Result.Failure(StorageErrors.PartialFailure(
                    "One or more destination rollback operations failed.",
                    $"fileCleanupErrors={string.Join(',', fileCleanupErrors)};" +
                    $"restoreErrors={string.Join(',', restoreErrors)};" +
                    $"directoryCleanupErrors={string.Join(',', directoryCleanupErrors)}"));
        }

        /// <summary>
        /// Reads the destination and says whether it is still what this transfer committed. A committed identity
        /// that is not known (the read after the commit failed) is not taken as ours: such a file is left and
        /// reported rather than undone on a guess. The current item is null when it is gone.
        /// </summary>
        private async Task<Result<(bool Ours, StorageItem? Current)>> StillOursAsync(string path, StorageItem? committed)
        {
            Result<StorageItem> current;
            try { current = await destination.GetInfoAsync(path, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { return Result<(bool, StorageItem?)>.Failure(StorageErrors.FromException(error, "Rollback transfer destination")); }
            if (current.IsFailure)
                return current.Error!.Code == StorageErrors.NotFoundCode
                    ? Result<(bool, StorageItem?)>.Success((false, null))
                    : Result<(bool, StorageItem?)>.Failure(current.Error);
            return Result<(bool, StorageItem?)>.Success((committed is not null && SameVersion(committed, current.Value!), current.Value));
        }

        /// <summary>
        /// The committed version as a condition the provider can enforce in the same request, or null when it
        /// cannot (the destination was then compared immediately before).
        /// </summary>
        private static StorageMutationCondition? ConditionFor(StorageItem committed) =>
            committed.ETag is not null || committed.VersionId is not null
                ? new StorageMutationCondition { ExpectedETag = committed.ETag, ExpectedVersionId = committed.VersionId }
                : null;

        private async Task<Result> DeleteIfStillOursAsync(string path, StorageItem? committed)
        {
            var ours = await StillOursAsync(path, committed).ConfigureAwait(false);
            if (ours.IsFailure)
                return Result.Failure(ours.Error!);
            if (ours.Value.Current is null)
                return Result.Success();
            if (!ours.Value.Ours)
                // Replaced by someone else after the commit, or not known to be ours: it is left in place.
                return Result.Failure(StorageErrors.Conflict($"The destination '{path}' changed after it was committed, or its committed version is unknown, so it was not rolled back."));
            // Deleted only while it is still that version: in the same request where the server can enforce it.
            var condition = destination.Capabilities.Supports(StorageFeature.ConditionalDelete) ? ConditionFor(committed!) : null;
            return await DeleteAsync(path, condition).ConfigureAwait(false);
        }

        private async Task<Result> RestoreIfStillOursAsync(string destinationPath, string backupPath, bool renamed, StorageItem? committed)
        {
            var ours = await StillOursAsync(destinationPath, committed).ConfigureAwait(false);
            if (ours.IsFailure)
                return Result.Failure(ours.Error!);
            if (!ours.Value.Ours)
                // Changed or deleted by someone else after the commit (or not known to be ours): that is not undone,
                // and the previous version stays in the backup, which is reported.
                return Result.Failure(StorageErrors.Conflict(
                    $"The destination '{destinationPath}' changed after it was committed, so the previous version was not restored over it."));
            try
            {
                // The previous version replaces the committed one only while it is still that version: the
                // provider enforces it in the same request where it can; otherwise it was compared just before.
                var options = new StorageTransferOptions { Overwrite = true, CreateParents = false, DestinationCondition = ConditionFor(committed!) };
                var restored = await PutBackAsync(options).ConfigureAwait(false);
                if (restored.IsFailure && options.DestinationCondition is not null && restored.Error!.Code == StorageErrors.UnsupportedCode)
                    restored = await PutBackAsync(options with { DestinationCondition = null }).ConfigureAwait(false);
                if (restored.IsFailure && !StorageErrorInfo.DestinationCommitted(restored.Error))
                    return restored;
                if (renamed)
                    return Result.Success();
                return await destination.DeleteAsync(
                    backupPath,
                    new StorageDeleteOptions { IgnoreMissing = true },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return Result.Failure(StorageErrors.FromException(error, "Restore transfer destination"));
            }

            Task<Result> PutBackAsync(StorageTransferOptions options) => renamed
                ? destination.MoveAsync(backupPath, destinationPath, options, CancellationToken.None)
                : destination.CopyAsync(backupPath, destinationPath, options, CancellationToken.None);
        }

        private async Task<Result> DeleteAsync(string path, StorageMutationCondition? condition = null)
        {
            try
            {
                return await destination.DeleteAsync(
                    path,
                    new StorageDeleteOptions { IgnoreMissing = true, Condition = condition },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return Result.Failure(StorageErrors.FromException(error, "Rollback transfer destination"));
            }
        }
    }
}

/// <param name="SourceType">Whether the transferred source was a file, directory, or link.</param>
/// <param name="Files">Files written to the destination.</param>
/// <param name="Directories">Directories created or reused at the destination.</param>
/// <param name="Bytes">Content bytes relayed.</param>
/// <param name="SkippedFiles">Files (and links) the conflict or link policy left untouched.</param>
/// <param name="TransferredSources">Source paths of the files that were written, so a move can delete only those.</param>
internal sealed record StorageTransferSummary(
    StorageItemType SourceType,
    long Files,
    long Directories,
    long Bytes,
    long SkippedFiles = 0,
    IReadOnlyList<string>? TransferredSources = null)
{
    /// <summary>The path written for a single file, after a rename.</summary>
    public string? WrittenPath { get; init; }
    /// <summary>Why a single file was skipped.</summary>
    public StorageSkipReason? SkipReason { get; init; }
    /// <summary>The content digest of a single file.</summary>
    public string? Sha256 { get; init; }
    /// <summary>How a verified single file was confirmed.</summary>
    public string? VerifiedBy { get; init; }
    /// <summary>The destination's identity after a single-file commit.</summary>
    public StorageItem? Destination { get; init; }
    /// <summary>Bytes reused from an earlier attempt's staging.</summary>
    public long BytesResumed { get; init; }
    /// <summary>How a destination condition was enforced.</summary>
    public StorageConditionEnforcement ConditionEnforcement { get; init; }
    /// <summary>
    /// The source items that were written, with the identity they had when they were listed, so a move deletes
    /// each one only while it is still that version.
    /// </summary>
    public IReadOnlyList<StorageItem>? TransferredItems { get; init; }
    /// <summary>Source paths a policy left in place on purpose (skipped files and links).</summary>
    public IReadOnlyList<string>? SkippedSources { get; init; }
    /// <summary>Directories this transfer created at the destination.</summary>
    public long CreatedDirectories { get; init; }
    /// <summary>A staging object a provider could not remove after it committed.</summary>
    public string? StagingLeftBehind { get; init; }
    /// <summary>A backup (the library's or a provider's) that could not be removed after the commit.</summary>
    public string? BackupLeftBehind { get; init; }
}

/// <summary>What a transfer did before it stopped, so a failure can be reported precisely.</summary>
internal sealed class TransferState
{
    /// <summary>The source item as it was when the transfer read it.</summary>
    public StorageItem? Source { get; set; }
    /// <summary>A staging object left behind (kept for resume, or cleanup failed).</summary>
    public string? StagingLeftBehind { get; set; }
    /// <summary>Whether the staging object was kept on purpose so the transfer can resume.</summary>
    public bool StagingResumable { get; set; }
    /// <summary>Bytes in the kept staging object.</summary>
    public long BytesStaged { get; set; }
    /// <summary>Whether content reached its destination path.</summary>
    public bool DestinationCommitted { get; set; }
    /// <summary>Whether a replaced destination was restored after a failure.</summary>
    public bool? BackupRestored { get; set; }
    /// <summary>A backup that could not be removed.</summary>
    public string? BackupLeftBehind { get; set; }
    /// <summary>Whether rolling back the destination did not finish.</summary>
    public bool RollbackIncomplete { get; set; }
    /// <summary>How a destination condition was enforced, even when the promote was refused.</summary>
    public StorageConditionEnforcement ConditionEnforcement { get; set; }
    /// <summary>Files committed at the destination so far (a directory transfer that stopped part-way).</summary>
    public long FilesCommitted { get; set; }
    /// <summary>Bytes of the files committed so far.</summary>
    public long BytesCommitted { get; set; }
    /// <summary>Directories created at the destination so far.</summary>
    public long DirectoriesCreated { get; set; }
    /// <summary>Source items a move already deleted, when it stopped before deleting them all.</summary>
    public long SourceItemsDeleted { get; set; }
}
