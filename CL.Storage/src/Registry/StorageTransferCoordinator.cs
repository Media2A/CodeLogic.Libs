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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceInfo = await source.GetInfoAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.IsFailure)
            return Result<StorageTransferSummary>.Failure(sourceInfo.Error!);

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
                    cancellationToken).ConfigureAwait(false),
                StorageItemType.Directory => await CopyDirectoryAsync(
                    source,
                    sourceInfo.Value,
                    destination,
                    destinationPath,
                    options,
                    cleanup,
                    cancellationToken).ConfigureAwait(false),
                _ => Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                    "Relayed transfer of storage links is not supported because link targets are provider-specific."))
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
            if (rollback.IsFailure)
            {
                return Result<StorageTransferSummary>.Failure(StorageErrors.PartialFailure(
                    "The transfer failed and destination rollback was incomplete.",
                    $"transferError={result.Error!.Code};rollbackError={rollback.Error!.Code};{rollback.Error.Details}"));
            }
        }
        else
        {
            var committed = await cleanup.CommitAsync().ConfigureAwait(false);
            if (committed.IsFailure)
                return Result<StorageTransferSummary>.Failure(committed.Error!);
        }
        return result;
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
                            break;
                        }
                    default:
                        return Result<StorageTransferSummary>.Failure(StorageErrors.Unsupported(
                            $"Relayed directory transfer cannot copy link '{itemPath.Value}'."));
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
            bytes));
    }

    private static async Task<Result<StorageTransferSummary>> CopyFileAsync(
        IStorageBackend source,
        StorageItem sourceFile,
        IStorageBackend destination,
        string destinationPath,
        StorageTransferOptions options,
        TransferCleanupTracker cleanup,
        CancellationToken cancellationToken)
    {
        var destinationExists = await destination.ExistsAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        if (destinationExists.IsFailure)
            return Result<StorageTransferSummary>.Failure(destinationExists.Error!);
        if (destinationExists.Value && !options.Overwrite)
            return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                $"The transfer destination '{destinationPath}' already exists."));
        if (destinationExists.Value)
        {
            var destinationInfo = await destination.GetInfoAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (destinationInfo.IsFailure)
                return Result<StorageTransferSummary>.Failure(destinationInfo.Error!);
            if (destinationInfo.Value!.ItemType != StorageItemType.File)
                return Result<StorageTransferSummary>.Failure(StorageErrors.Conflict(
                    $"The transfer destination '{destinationPath}' is not a file."));
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

        var staging = await AllocateStagingPathAsync(destination, parent, cancellationToken).ConfigureAwait(false);
        if (staging.IsFailure)
            return Result<StorageTransferSummary>.Failure(staging.Error!);
        var stagingPath = staging.Value!;

        Result<long> relay;
        var canUseNativeStagingCopy = ReferenceEquals(source, destination) &&
            source.Capabilities.Supports(StorageFeature.FileCopy | StorageFeature.ServerSideCopy) &&
            options.MetadataPreservation != StorageMetadataPreservation.Discard;
        if (canUseNativeStagingCopy)
        {
            try
            {
                var copied = await destination.CopyAsync(
                    sourceFile.Path,
                    stagingPath,
                    new StorageTransferOptions
                    {
                        Overwrite = false,
                        CreateParents = options.CreateParents,
                        MetadataPreservation = options.MetadataPreservation
                    },
                    cancellationToken).ConfigureAwait(false);
                relay = copied.IsSuccess
                    ? Result<long>.Success(sourceFile.Size ?? 0)
                    : Result<long>.Failure(copied.Error!);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                relay = Result<long>.Failure(StorageErrors.FromException(
                    error,
                    "Copy transfer staging object"));
            }
        }
        else
        {
            var download = await source.DownloadAsync(
                sourceFile.Path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (download.IsFailure)
                return Result<StorageTransferSummary>.Failure(download.Error!);
            await using (var stream = download.Value!)
            {
                try
                {
                    relay = await RelayAsync(
                        stream,
                        destination,
                        stagingPath,
                        new StorageUploadOptions
                        {
                            Overwrite = false,
                            CreateParents = options.CreateParents,
                            ContentType = sourceFile.ContentType,
                            Metadata = transferredMetadata
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
                    throw;
                }
            }
        }

        if (relay.IsFailure)
        {
            var stagingCleanup = await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
            return FailureAfterCleanup(relay.Error!, "The transfer upload failed", stagingCleanup);
        }

        string? backupPath = null;
        if (destinationExists.Value)
        {
            var allocatedBackup = await AllocateStagingPathAsync(
                destination,
                parent,
                cancellationToken,
                ".cl-storage-transfer-backup-").ConfigureAwait(false);
            if (allocatedBackup.IsFailure)
            {
                var stagingCleanup = await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
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
                var stagingCleanup = await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
                var backupCleanup = await DeleteStagingAsync(destination, backupPath).ConfigureAwait(false);
                return FailureAfterCleanup(
                    backedUp.Error!,
                    "The transfer could not back up the previous destination",
                    stagingCleanup,
                    backupCleanup);
            }
        }

        Result commit;
        try
        {
            commit = await destination.MoveAsync(
                stagingPath,
                destinationPath,
                new StorageTransferOptions
                {
                    Overwrite = options.Overwrite,
                    CreateParents = options.CreateParents
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
            if (backupPath is not null)
                await RestoreReplacementAsync(destination, destinationPath, backupPath).ConfigureAwait(false);
            throw;
        }
        if (commit.IsFailure)
        {
            var stagingCleanup = await DeleteStagingAsync(destination, stagingPath).ConfigureAwait(false);
            if (backupPath is not null)
            {
                var restored = await RestoreReplacementAsync(destination, destinationPath, backupPath).ConfigureAwait(false);
                return FailureAfterCleanup(
                    commit.Error!,
                    "The transfer commit failed",
                    stagingCleanup,
                    restored);
            }
            return FailureAfterCleanup(commit.Error!, "The transfer commit failed", stagingCleanup);
        }

        if (backupPath is not null)
            cleanup.TrackReplacement(destinationPath, backupPath);
        else
            cleanup.TrackFile(destinationPath);
        return Result<StorageTransferSummary>.Success(new StorageTransferSummary(
            StorageItemType.File,
            Files: 1,
            Directories: 0,
            Bytes: relay.Value));
    }

    private static async Task<Result<long>> RelayAsync(
        Stream source,
        IStorageBackend destination,
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
                upload = await destination.UploadAsync(
                    stagingPath,
                    relayStream,
                    uploadOptions,
                    linked.Token).ConfigureAwait(false);
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

    private static async Task<Result<string>> AllocateStagingPathAsync(
        IStorageBackend destination,
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
        return Result<string>.Failure(StorageErrors.Conflict(
            "Unable to allocate a unique destination staging path."));
    }

    private static async Task<Result> DeleteStagingAsync(IStorageBackend destination, string stagingPath)
    {
        try
        {
            return await destination.DeleteAsync(
                stagingPath,
                new StorageDeleteOptions { Recursive = true, IgnoreMissing = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return Result.Failure(StorageErrors.FromException(error, "Delete transfer staging object"));
        }
    }

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

    private static async Task ObserveProducerAsync(Task<Result<long>> producer)
    {
        try { _ = await producer.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
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

        internal void TrackFile(string path) => _createdFiles.Add(path);
        internal void TrackDirectory(string path) => _createdDirectories.Add(path);
        internal void TrackReplacement(string destinationPath, string backupPath) =>
            _replacements.Add((destinationPath, backupPath));

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
                        return Result.Failure(StorageErrors.PartialFailure(
                            "The transfer completed, but an internal replacement backup could not be removed.",
                            $"backupDeleteError={deleted.Error!.Code};destinationState=complete;backupState=retained"));
                }
                catch (Exception error)
                {
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

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesRead, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
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

internal sealed record StorageTransferSummary(
    StorageItemType SourceType,
    long Files,
    long Directories,
    long Bytes);
