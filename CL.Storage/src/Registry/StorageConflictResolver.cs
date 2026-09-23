using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Registry;

/// <summary>What to do with one file whose destination may already exist.</summary>
/// <param name="Skip">Leave the destination untouched and do not transfer.</param>
/// <param name="Path">Destination path to write, which differs from the requested one under <see cref="StorageConflictPolicy.Rename"/>.</param>
/// <param name="Overwrite">Whether the write may replace an existing destination.</param>
/// <param name="Existing">The destination item found while deciding, when one exists.</param>
internal readonly record struct ConflictDecision(bool Skip, string Path, bool Overwrite, StorageItem? Existing);

/// <summary>Resolves <see cref="StorageConflictPolicy"/> into a skip, overwrite, or new-name decision.</summary>
internal static class StorageConflictResolver
{
    /// <summary>Modification times within this window count as equal; FTP and FAT store whole or even seconds.</summary>
    internal static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
    private const int MaxRenameAttempts = 1_000;

    /// <summary>Returns whether a policy needs to look at the destination before writing.</summary>
    public static bool IsConditional(StorageConflictPolicy? policy) =>
        policy is not (null or StorageConflictPolicy.Fail or StorageConflictPolicy.Overwrite);

    public static async Task<Result<ConflictDecision>> ResolveAsync(
        IStorageService destination,
        string path,
        StorageConflictPolicy? policy,
        bool overwriteWhenUnset,
        long? sourceSize,
        DateTimeOffset? sourceModified,
        CancellationToken cancellationToken)
    {
        switch (policy)
        {
            case null:
                return Result<ConflictDecision>.Success(new ConflictDecision(false, path, overwriteWhenUnset, null));
            case StorageConflictPolicy.Fail:
                return Result<ConflictDecision>.Success(new ConflictDecision(false, path, false, null));
            case StorageConflictPolicy.Overwrite:
                return Result<ConflictDecision>.Success(new ConflictDecision(false, path, true, null));
        }

        var existing = await destination.GetInfoAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing.IsFailure)
        {
            // A missing destination is written normally; overwrite stays off so a racing writer is not clobbered.
            return existing.Error!.Code == StorageErrors.NotFoundCode
                ? Result<ConflictDecision>.Success(new ConflictDecision(false, path, false, null))
                : Result<ConflictDecision>.Failure(existing.Error);
        }
        var current = existing.Value!;
        if (current.ItemType == StorageItemType.Directory)
            return policy == StorageConflictPolicy.Rename
                // A folder in the way is just another taken name.
                ? await RenameAsync(destination, path, cancellationToken).ConfigureAwait(false)
                : Result<ConflictDecision>.Failure(StorageErrors.Conflict($"The destination '{path}' is a directory."));

        return policy switch
        {
            StorageConflictPolicy.Skip => Skip(path, current),
            StorageConflictPolicy.OverwriteIfNewer => Decide(path, current, IsNewer(sourceModified, current.LastModified)),
            StorageConflictPolicy.OverwriteIfSizeDiffers => Decide(path, current, SizeDiffers(sourceSize, current.Size)),
            StorageConflictPolicy.OverwriteIfNewerOrSizeDiffers => Decide(path, current,
                IsNewer(sourceModified, current.LastModified) || SizeDiffers(sourceSize, current.Size)),
            StorageConflictPolicy.Rename => await RenameAsync(destination, path, cancellationToken).ConfigureAwait(false),
            // Outside uploads (for example relayed transfers) a partial file is replaced as a whole.
            StorageConflictPolicy.Resume => Decide(path, current, SizeDiffers(sourceSize, current.Size)),
            _ => Result<ConflictDecision>.Failure(StorageErrors.InvalidContent("The conflict policy is invalid."))
        };
    }

    /// <summary>
    /// Uploads with a conflict policy applied: resolves it against <paramref name="destination"/>, then
    /// uploads again with the policy cleared. A skip returns the existing item without writing.
    /// </summary>
    public static async Task<Result<StorageItem>> UploadAsync(
        IStorageService destination,
        string path,
        Stream source,
        StorageUploadOptions options,
        CancellationToken cancellationToken)
    {
        var validation = options.Validate();
        if (validation.IsFailure) return Result<StorageItem>.Failure(validation.Error!);
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure) return Result<StorageItem>.Failure(normalized.Error!);
        var decision = await ResolveAsync(
            destination,
            normalized.Value!,
            options.ConflictPolicy,
            options.Overwrite,
            source.CanSeek ? source.Length - source.Position : null,
            options.SourceLastModified,
            cancellationToken).ConfigureAwait(false);
        if (decision.IsFailure) return Result<StorageItem>.Failure(decision.Error!);
        if (decision.Value.Skip) return Result<StorageItem>.Success(decision.Value.Existing!);
        return await destination.UploadAsync(
            decision.Value.Path,
            source,
            options with { Overwrite = decision.Value.Overwrite, ConflictPolicy = null },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a policy skipped a file.</summary>
    internal static StorageSkipReason SkipReasonFor(StorageConflictPolicy policy) => policy switch
    {
        StorageConflictPolicy.OverwriteIfNewer => StorageSkipReason.SourceNotNewer,
        StorageConflictPolicy.OverwriteIfSizeDiffers => StorageSkipReason.SameSize,
        StorageConflictPolicy.OverwriteIfNewerOrSizeDiffers => StorageSkipReason.Unchanged,
        StorageConflictPolicy.Resume => StorageSkipReason.AlreadyComplete,
        _ => StorageSkipReason.DestinationExists
    };

    /// <summary>Source is newer when it is later by more than the tolerance; unknown times cannot prove it older.</summary>
    internal static bool IsNewer(DateTimeOffset? source, DateTimeOffset? destination) =>
        source is not { } s || destination is not { } d || s - d > TimeTolerance;

    /// <summary>Unknown sizes cannot prove the files equal, so they count as different.</summary>
    internal static bool SizeDiffers(long? source, long? destination) =>
        source is not { } s || destination is not { } d || s != d;

    /// <summary>
    /// Returns <c>name (1).ext</c>, <c>name (2).ext</c>, … for the first free name. A name that is already
    /// numbered counts on (<c>name (1).txt</c> gives <c>name (2).txt</c>, not <c>name (1) (1).txt</c>), and a
    /// trailing dot is not an extension (<c>file.</c> gives <c>file. (1)</c>, never the Windows-invalid <c>file (1).</c>).
    /// </summary>
    internal static string Candidate(string path, int attempt)
    {
        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 && dot < name.Length - 1 ? (name[..dot], name[dot..]) : (name, string.Empty);
        var numbered = NumberedStem.Match(stem);
        if (numbered.Success && int.TryParse(numbered.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start) && start < int.MaxValue - MaxRenameAttempts)
            return $"{directory}{numbered.Groups[1].Value} ({start + attempt}){extension}";
        return $"{directory}{stem} ({attempt}){extension}";
    }

    private static readonly System.Text.RegularExpressions.Regex NumberedStem =
        new(@"^(.+) \((\d{1,9})\)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static Result<ConflictDecision> Skip(string path, StorageItem current) =>
        Result<ConflictDecision>.Success(new ConflictDecision(true, path, false, current));

    private static Result<ConflictDecision> Decide(string path, StorageItem current, bool overwrite) =>
        overwrite
            ? Result<ConflictDecision>.Success(new ConflictDecision(false, path, true, current))
            : Skip(path, current);

    private static async Task<Result<ConflictDecision>> RenameAsync(IStorageService destination, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxRenameAttempts; attempt++)
        {
            var candidate = Candidate(path, attempt);
            var exists = await destination.ExistsAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (exists.IsFailure) return Result<ConflictDecision>.Failure(exists.Error!);
            if (!exists.Value)
                return Result<ConflictDecision>.Success(new ConflictDecision(false, candidate, false, null));
        }
        return Result<ConflictDecision>.Failure(StorageErrors.Conflict($"No free name was found for '{path}'."));
    }
}
