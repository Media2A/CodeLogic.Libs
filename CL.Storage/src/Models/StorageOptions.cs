using System.Collections.ObjectModel;
using System.Text;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

public sealed record StorageListOptions
{
    public bool Recursive { get; init; }
    public int PageSize { get; init; } = 1000;
    public string? ContinuationToken { get; init; }

    public Result Validate() => PageSize > 0
        ? Result.Success()
        : Result.Failure(StorageErrors.InvalidPath("PageSize must be greater than zero."));
}

public sealed record StorageUploadOptions
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private IReadOnlyDictionary<string, string> _metadata = EmptyMetadata;

    public bool Overwrite { get; init; } = true;
    public bool CreateParents { get; init; } = true;
    public string? ContentType { get; init; }
    /// <summary>Optional condition applied atomically by providers that advertise conditional updates.</summary>
    public StorageMutationCondition? Condition { get; init; }
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = value is null || value.Count == 0
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

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

public sealed record StorageDownloadOptions
{
    public long Offset { get; init; }
    public long? Length { get; init; }
    public long? MaxBufferedBytes { get; init; }
    /// <summary>Optional provider version/generation identifier to read.</summary>
    public string? VersionId { get; init; }

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

public sealed record StorageDeleteOptions
{
    public bool Recursive { get; init; }
    public bool IgnoreMissing { get; init; }
    /// <summary>Optional condition applied atomically by providers that advertise conditional deletes.</summary>
    public StorageMutationCondition? Condition { get; init; }

    public Result Validate() => Condition?.Validate() ?? Result.Success();
}

/// <summary>Requires the current item to match one or both provider-neutral identity tokens.</summary>
public sealed record StorageMutationCondition
{
    public string? ExpectedETag { get; init; }
    public string? ExpectedVersionId { get; init; }

    public bool IsEmpty => ExpectedETag is null && ExpectedVersionId is null;

    public Result Validate()
    {
        var etag = StorageOptionValidation.OptionalToken(ExpectedETag, nameof(ExpectedETag));
        return etag.IsFailure
            ? etag
            : StorageOptionValidation.OptionalToken(ExpectedVersionId, nameof(ExpectedVersionId));
    }
}

public sealed record StorageTransferOptions
{
    public bool Overwrite { get; init; } = true;
    public bool CreateParents { get; init; } = true;
    public StorageMetadataPreservation MetadataPreservation { get; init; } = StorageMetadataPreservation.BestEffort;

    public Result Validate() => Enum.IsDefined(MetadataPreservation)
        ? Result.Success()
        : Result.Failure(StorageErrors.InvalidPath("MetadataPreservation is invalid."));
}
