using System.Buffers;
using System.IO.Pipelines;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
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

        var sourceInfo = await source.GetInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
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

        var cleanup = new TransferCleanupTracker(destination);
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
                    cancellationToken).ConfigureAwait(false),
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
            await cleanup.RollbackAsync().ConfigureAwait(false);
            throw;
        }

        if (result.IsFailure)
        {
            var rollback = await cleanup.RollbackAsync().ConfigureAwait(false);
            state.BackupRestored = cleanup.HadReplacements ? rollback.IsSuccess : null;
            if (rollback.IsFailure)
            {
                state.RollbackIncomplete = true;
                return Result<StorageTransferSummary>.Failure(StorageErrors.PartialFailure(
                    "The transfer failed and destination rollback was incomplete.",
                    $"transferError={result.Error!.Code};rollbackError={rollback.Error!.Code};{rollback.Error.Details}"));
            }
        }
        else
        {
            var committed = await cleanup.CommitAsync().ConfigureAwait(false);
            if (committed.IsFailure)
            {
                state.BackupLeftBehind = cleanup.RetainedBackup;
                state.DestinationCommitted = true;
                return Result<StorageTransferSummary>.Failure(committed.Error!);
            }
            aggregate?.Complete();
        }
        return result;
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

    /// <summary>Tags a file's progress with its source path instead of the internal staging name.</summary>
    private sealed class FileProgress(IProgress<StorageTransferProgress> inner, string path) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) => inner.Report(value with { ItemPath = path });
    }

    private static async Task<Result<StorageTransferSummary>> CopyDirectoryAsync(
        IStorageBackend source,
        StorageItem sourceDirectory,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken)
    {
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
                            var copied = await CopyFileAsync(
                                source,
                                item with { Path = itemPath.Value! },
                                destination,
                                mappedPath,
                                options,
                                cleanup,
                                cancellationToken).ConfigureAwait(false);
                            if (copied.IsFailure)
                                return Result<StorageTransferSummary>.Failure(copied.Error!);
                            files += copied.Value!.Files;
                            bytes += copied.Value.Bytes;
                            skipped += copied.Value.SkippedFiles;
                            transferred.AddRange(copied.Value.TransferredSources ?? []);
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
                            // A skipped or recreated link stays behind on a move; only followed content counts as moved.
                            if (options.LinkHandling == StorageLinkHandling.Recreate)
                                transferred.Add(itemPath.Value!);
                            else
                                transferred.AddRange(linked.Value.TransferredSources ?? []);
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
            transferred));
    }

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
                return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.Link, 0, 0, 0));

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
                cleanup.TrackFile(destinationPath);
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
                    Destination = decision.Value.Existing
                });
            }
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
            destinationItem.Size is { } present && present == sourceFile.Size)
        {
            // Resume: a destination of the source's full size is already complete.
            aggregate?.FileDone(sourceFile.Path, 0);
            return Result<StorageTransferSummary>.Success(new StorageTransferSummary(StorageItemType.File, 0, 0, 0, SkippedFiles: 1, TransferredSources: [])
            {
                SkipReason = StorageSkipReason.AlreadyComplete,
                Destination = destinationItem
            });
        }
        var overwrite = options.Overwrite || resumable;
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

        StagedContent staged;
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
            if (resumable)
            {
                resumeKey = StagedWriter.SourceKey(sourceFile.Path, sourceFile.Size, sourceFile.LastModified, sourceFile.ETag, options.SourceVersionId ?? sourceFile.VersionId);
                if (options.ResumeToken is { } token)
                {
                    if (token.DestinationPath == destinationPath && IsSameSource(token, sourceFile, options.SourceVersionId) &&
                        Parent(token.StagingPath) == parent && Name(token.StagingPath).StartsWith(StagedWriter.ResumablePrefix, StringComparison.Ordinal))
                        tokenStaging = token.StagingPath;
                    else if (Parent(token.StagingPath) == parent && Name(token.StagingPath).StartsWith(StagedWriter.ResumablePrefix, StringComparison.Ordinal))
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
                    Progress = options.Progress is { } progress ? new FileProgress(progress, sourceFile.Path) : null
                },
                ExpectedLength = options.ExpectedSourceLength,
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
                state.StagingResumable = resumable && written.StagingLeft is not null && written.CleanupError is null;
                state.BytesStaged = written.BytesStaged;
                return written.CleanupError is { } cleanupError
                    ? FailureAfterCleanup(written.Error!, "The transfer upload failed", Result.Failure(cleanupError))
                    : Result<StorageTransferSummary>.Failure(written.Error!);
            }
            staged = written.Content!;
        }

        // A source without a pinned version is read again: it must not have changed while it streamed.
        if (options.ExpectedSourceETag is not null && options.SourceVersionId is null)
        {
            var after = await source.GetInfoAsync(sourceFile.Path, cancellationToken).ConfigureAwait(false);
            if (after.IsFailure || !StagedWriter.SameETag(options.ExpectedSourceETag, after.Value!.ETag))
            {
                await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
                return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                    $"The source '{sourceFile.Path}' changed while it was being copied.",
                    $"expectedETag={options.ExpectedSourceETag};actualETag={(after.IsSuccess ? after.Value!.ETag : null)}"));
            }
        }

        string? backupPath = null;
        if (destinationExists)
        {
            var allocatedBackup = await AllocateStagingPathAsync(
                destination,
                parent,
                cancellationToken,
                ".cl-storage-transfer-backup-").ConfigureAwait(false);
            if (allocatedBackup.IsFailure)
            {
                var stagingCleanup = await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
                return FailureAfterCleanup(
                    allocatedBackup.Error!,
                    "The transfer could not allocate a replacement backup",
                    stagingCleanup);
            }
            backupPath = allocatedBackup.Value!;
            var backedUp = await destination.CopyAsync(
                destinationPath,
                backupPath,
                new StorageTransferOptions { Overwrite = false, CreateParents = false },
                cancellationToken).ConfigureAwait(false);
            if (backedUp.IsFailure)
            {
                var stagingCleanup = await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
                var backupCleanup = await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                return FailureAfterCleanup(
                    backedUp.Error!,
                    "The transfer could not back up the previous destination",
                    stagingCleanup,
                    backupCleanup);
            }
        }

        Result commit;
        StorageConditionEnforcement enforcement;
        try
        {
            (commit, enforcement) = await StagedWriter.PromoteAsync(
                destination,
                staged.StagingPath,
                destinationPath,
                overwrite,
                options.DestinationCondition,
                options.CreateParents,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
            if (backupPath is not null)
                await RestoreReplacementAsync(destination, destinationPath, backupPath).ConfigureAwait(false);
            throw;
        }
        state.ConditionEnforcement = enforcement;
        if (commit.IsFailure)
        {
            var stagingCleanup = await DeleteStagingAsync(destination, staged.StagingPath).ConfigureAwait(false);
            if (stagingCleanup.IsFailure) state.StagingLeftBehind = staged.StagingPath;
            if (backupPath is not null)
            {
                var restored = await RestoreReplacementAsync(destination, destinationPath, backupPath).ConfigureAwait(false);
                state.BackupRestored = restored.IsSuccess;
                if (restored.IsFailure) state.BackupLeftBehind = backupPath;
                return FailureAfterCleanup(
                    commit.Error!,
                    "The transfer commit failed",
                    stagingCleanup,
                    restored);
            }
            return FailureAfterCleanup(commit.Error!, "The transfer commit failed", stagingCleanup);
        }

        state.DestinationCommitted = true;
        if (backupPath is not null)
            cleanup.TrackReplacement(destinationPath, backupPath);
        else
            cleanup.TrackFile(destinationPath);
        var committed = await destination.GetInfoAsync(destinationPath, cancellationToken).ConfigureAwait(false);
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
            Destination = committed.IsSuccess ? committed.Value : null,
            BytesResumed = staged.BytesResumed,
            ConditionEnforcement = enforcement
        });
    }

    /// <summary>Whether a resume token's staged bytes came from the current source.</summary>
    private static bool IsSameSource(StorageResumeToken token, StorageItem source, string? versionId) =>
        token.SourcePath == source.Path &&
        token.SourceLength == source.Size &&
        (versionId is not null ? token.SourceVersionId == versionId : true) &&
        (token.SourceETag is null || StagedWriter.SameETag(token.SourceETag, source.ETag)) &&
        (token.SourceLastModified is null || token.SourceLastModified == source.LastModified);

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

    private static async Task<Result> RestoreReplacementAsync(
        IStorageBackend destination,
        string destinationPath,
        string backupPath)
    {
        try
        {
            var restored = await destination.CopyAsync(
                backupPath,
                destinationPath,
                new StorageTransferOptions { Overwrite = true, CreateParents = false },
                CancellationToken.None).ConfigureAwait(false);
            if (restored.IsFailure)
                return restored;
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

    private sealed class TransferCleanupTracker(IStorageBackend destination)
    {
        private readonly List<string> _createdFiles = [];
        private readonly List<string> _createdDirectories = [];
        private readonly List<(string DestinationPath, string BackupPath)> _replacements = [];

        internal bool HadReplacements { get; private set; }
        internal string? RetainedBackup { get; private set; }

        internal void TrackFile(string path) => _createdFiles.Add(path);
        internal void TrackDirectory(string path) => _createdDirectories.Add(path);
        internal void TrackReplacement(string destinationPath, string backupPath)
        {
            HadReplacements = true;
            _replacements.Add((destinationPath, backupPath));
        }

        internal async Task<Result> CommitAsync()
        {
            foreach (var replacement in _replacements)
            {
                try
                {
                    var deleted = await destination.DeleteAsync(
                        replacement.BackupPath,
                        new StorageDeleteOptions { IgnoreMissing = true },
                        CancellationToken.None).ConfigureAwait(false);
                    if (deleted.IsFailure)
                    {
                        RetainedBackup = replacement.BackupPath;
                        return Result.Failure(StorageErrors.PartialFailure(
                            "The transfer completed, but an internal replacement backup could not be removed.",
                            $"backupDeleteError={deleted.Error!.Code};destinationState=complete;backupState=retained"));
                    }
                }
                catch (Exception error)
                {
                    RetainedBackup = replacement.BackupPath;
                    return Result.Failure(StorageErrors.PartialFailure(
                        "The transfer completed, but an internal replacement backup could not be removed.",
                        $"backupDeleteError={StorageErrors.FromException(error, "Delete transfer backup").Code};destinationState=complete;backupState=retained"));
                }
            }
            _replacements.Clear();
            return Result.Success();
        }

        internal async Task<Result> RollbackAsync()
        {
            var fileCleanupErrors = new List<string>();
            var restoreErrors = new List<string>();
            var directoryCleanupErrors = new List<string>();
            foreach (var path in _createdFiles.AsEnumerable().Reverse())
            {
                var deleted = await DeleteAsync(path, recursive: false).ConfigureAwait(false);
                if (deleted.IsFailure)
                    fileCleanupErrors.Add(deleted.Error!.Code);
            }
            foreach (var replacement in _replacements.AsEnumerable().Reverse())
            {
                var restored = await RestoreReplacementAsync(
                    destination,
                    replacement.DestinationPath,
                    replacement.BackupPath).ConfigureAwait(false);
                if (restored.IsFailure)
                    restoreErrors.Add(restored.Error!.Code);
            }
            foreach (var path in _createdDirectories.AsEnumerable().Reverse())
            {
                var deleted = await DeleteAsync(path, recursive: false).ConfigureAwait(false);
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

        private async Task<Result> DeleteAsync(string path, bool recursive)
        {
            try
            {
                return await destination.DeleteAsync(
                    path,
                    new StorageDeleteOptions { Recursive = recursive, IgnoreMissing = true },
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
/// <param name="SkippedFiles">Files the conflict policy left untouched.</param>
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
}
