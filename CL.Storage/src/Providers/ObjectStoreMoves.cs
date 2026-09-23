using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>
/// Moves on object stores, which have no rename: a copy on the server followed by deleting the source, only
/// while the source is still what was copied.
/// </summary>
internal static class ObjectStoreMoves
{
    /// <summary>
    /// The error for a move whose destination committed but whose source could not be deleted: it carries
    /// <c>destinationState=complete</c>, so callers treat the destination as written, and names the source that
    /// was left behind.
    /// </summary>
    public static Error SourceKept(string service, string sourcePath, Error cause) =>
        StorageErrors.PartialFailure(
            $"The {service} destination completed, but the source could not be deleted: {cause.Message}",
            $"sourceDeleteError={cause.Code};{StorageErrorInfo.DestinationStateKey}=complete;{StorageErrorInfo.LeftBehindKey}={sourcePath}");

    /// <summary>
    /// Moves a directory: lists the source tree, copies it through the relay (which rolls back what it wrote if
    /// the copy fails), then deletes each copied source file only while it still has the ETag it was listed
    /// with, and removes folders left empty. Files added, changed, or skipped during the move stay in the
    /// source, and the result says so with <c>destinationState=complete</c>; nothing is deleted recursively.
    /// </summary>
    public static async Task<Result> MoveDirectoryAsync(
        IStorageBackend backend,
        string source,
        string destination,
        StorageTransferOptions options,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        var directories = new List<string> { source };
        string? token = null;
        do
        {
            var page = await backend.ListAsync(source, new StorageListOptions { Recursive = true, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            if (page.IsFailure) return Result.Failure(page.Error!);
            foreach (var item in page.Value!.Items)
            {
                if (item.ItemType == StorageItemType.Directory) directories.Add(item.Path);
                else files[item.Path] = item;
            }
            token = page.Value.ContinuationToken;
        } while (!string.IsNullOrEmpty(token));

        var copied = await StorageTransferCoordinator.CopyAsync(backend, source, backend, destination, options, cancellationToken).ConfigureAwait(false);
        if (copied.IsFailure) return Result.Failure(copied.Error!);

        var kept = 0;
        Error? firstError = null;
        try
        {
            foreach (var path in copied.Value!.TransferredSources ?? [])
            {
                // A file that appeared after the listing was copied as it is now, but its identity at the time it was
                // copied is unknown: it stays.
                if (!files.TryGetValue(path, out var listed))
                {
                    kept++;
                    continue;
                }
                var condition = listed.ETag is null && listed.VersionId is null
                    ? null
                    : new StorageMutationCondition { ExpectedETag = listed.ETag };
                var deleted = await backend.DeleteAsync(path, new StorageDeleteOptions { Condition = condition }, cancellationToken).ConfigureAwait(false);
                if (deleted.IsFailure && deleted.Error!.Code != StorageErrors.NotFoundCode)
                {
                    kept++;
                    firstError ??= deleted.Error;
                }
            }
            // Deepest first; a folder that still holds something is left in place.
            foreach (var directory in directories.Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path.Length))
                _ = await backend.DeleteAsync(directory, new StorageDeleteOptions { IgnoreMissing = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure(StorageErrors.PartialFailure(
                "The destination completed, but the move was cancelled while its source was being deleted.",
                $"sourceDeleteError={StorageErrors.CancelledCode};{StorageErrorInfo.DestinationStateKey}=complete"));
        }
        return kept == 0
            ? Result.Success()
            : Result.Failure(StorageErrors.PartialFailure(
                $"The destination completed, but {kept} source file(s) changed, appeared, or could not be deleted during the move and were kept.",
                $"sourceDeleteError={firstError?.Code ?? StorageErrors.ConflictCode};{StorageErrorInfo.DestinationStateKey}=complete;sourceKept={kept}"));
    }
}
