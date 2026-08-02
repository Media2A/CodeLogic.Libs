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
    /// <summary>Gets whether missing physical parent directories should be created.</summary>
    public bool CreateParents { get; init; } = true;
    /// <summary>Gets the optional MIME content type stored with the object.</summary>
    public string? ContentType { get; init; }
    /// <summary>Optional condition applied atomically by providers that advertise conditional updates.</summary>
    public StorageMutationCondition? Condition { get; init; }
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
        return !Overwrite && Condition is { IsEmpty: false }
            ? Result.Failure(StorageErrors.InvalidPath(
                "Overwrite=false cannot be combined with an expected ETag or version condition."))
            : Result.Success();
    }
}

/// <summary>Controls range, buffering, and exact-version downloads.</summary>
public sealed record StorageDownloadOptions
{
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
    /// <summary>Gets whether missing physical destination parents should be created.</summary>
    public bool CreateParents { get; init; } = true;
    /// <summary>Gets how user metadata is handled across provider boundaries.</summary>
    public StorageMetadataPreservation MetadataPreservation { get; init; } = StorageMetadataPreservation.BestEffort;

    /// <summary>Validates the metadata-preservation mode.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate() => Enum.IsDefined(MetadataPreservation)
        ? Result.Success()
        : Result.Failure(StorageErrors.InvalidPath("MetadataPreservation is invalid."));
}
