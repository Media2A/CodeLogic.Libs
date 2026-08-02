using System.Collections.ObjectModel;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>Controls whether a metadata update merges with or replaces existing user metadata.</summary>
public enum StorageMetadataUpdateMode
{
    Merge,
    Replace
}

/// <summary>Controls a capability-gated metadata update.</summary>
public sealed record StorageMetadataUpdateOptions
{
    public StorageMetadataUpdateMode Mode { get; init; } = StorageMetadataUpdateMode.Replace;
    /// <summary>Only update when the current provider ETag matches this value.</summary>
    public string? ExpectedETag { get; init; }
    /// <summary>Only update when the current provider version/generation token matches this value.</summary>
    public string? ExpectedVersionId { get; init; }

    public Result Validate(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var validation = StorageOptionValidation.Metadata(metadata);
        if (validation.IsFailure) return validation;
        validation = StorageOptionValidation.OptionalToken(ExpectedETag, nameof(ExpectedETag));
        return validation.IsFailure
            ? validation
            : StorageOptionValidation.OptionalToken(ExpectedVersionId, nameof(ExpectedVersionId));
    }
}

/// <summary>HTTP operation authorized by a temporary signed URL.</summary>
public enum StorageSignedUrlMethod
{
    Read,
    Write
}

/// <summary>Controls creation of a temporary provider-signed object URL.</summary>
public sealed record StorageSignedUrlOptions
{
    public StorageSignedUrlMethod Method { get; init; } = StorageSignedUrlMethod.Read;
    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromMinutes(15);
    public string? ContentType { get; init; }
    public string? VersionId { get; init; }

    public Result Validate()
    {
        if (ExpiresIn < TimeSpan.FromSeconds(1) || ExpiresIn > TimeSpan.FromDays(7))
            return Result.Failure(StorageErrors.InvalidPath("Signed URL expiry must be between one second and seven days."));
        if (Method == StorageSignedUrlMethod.Write && VersionId is not null)
            return Result.Failure(StorageErrors.InvalidPath("A write URL cannot target an existing object version."));
        var validation = StorageOptionValidation.OptionalHeaderValue(ContentType, nameof(ContentType));
        return validation.IsFailure
            ? validation
            : StorageOptionValidation.OptionalToken(VersionId, nameof(VersionId));
    }
}

/// <summary>A temporary signed URL. Treat <see cref="Url"/> as a secret and never log it.</summary>
public sealed record StorageSignedUrl(
    Uri Url,
    StorageSignedUrlMethod Method,
    DateTimeOffset ExpiresAt);

/// <summary>Incremental bytes observed while a convenience upload/download stream is consumed.</summary>
public sealed record StorageTransferProgress(
    long BytesTransferred,
    long? TotalBytes,
    bool IsCompleted);

/// <summary>Controls one provider page of versions for an exact object path.</summary>
public sealed record StorageVersionListOptions
{
    public int PageSize { get; init; } = 1000;
    public string? ContinuationToken { get; init; }
    public bool IncludeDeleteMarkers { get; init; } = true;

    public Result Validate()
    {
        if (PageSize is < 1 or > 10_000)
            return Result.Failure(StorageErrors.InvalidPath(
                "Version PageSize must be between 1 and 10,000."));
        return StorageOptionValidation.OptionalToken(ContinuationToken, nameof(ContinuationToken));
    }
}

/// <summary>One immutable object version or provider delete marker.</summary>
public sealed record StorageVersion
{
    public required string Path { get; init; }
    public required string VersionId { get; init; }
    public string? ETag { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public bool IsLatest { get; init; }
    public bool IsDeleteMarker { get; init; }
}

/// <summary>One page of versions and the opaque token for the next page.</summary>
public sealed record StorageVersionPage(
    IReadOnlyList<StorageVersion> Versions,
    string? ContinuationToken);

/// <summary>Client-side digest algorithm used by provider-neutral integrity helpers.</summary>
public enum StorageChecksumAlgorithm
{
    Md5,
    Sha256,
    Sha384,
    Sha512
}

/// <summary>A lowercase hexadecimal digest and the number of bytes included in it.</summary>
public sealed record StorageChecksum(
    StorageChecksumAlgorithm Algorithm,
    string HexValue,
    long BytesProcessed);

/// <summary>The actual digest and constant-time comparison outcome for an expected digest.</summary>
public sealed record StorageChecksumVerification(
    StorageChecksum Actual,
    bool Matches);

/// <summary>Counts a completed rollback-safe local-directory upload or download.</summary>
public sealed record StorageDirectoryTransferReport(
    long Files,
    long Directories,
    long Bytes);

/// <summary>Controls whether a tag update merges with or replaces existing tags.</summary>
public enum StorageTagUpdateMode
{
    Merge,
    Replace
}

/// <summary>Controls a capability-gated object tag update.</summary>
public sealed record StorageTagUpdateOptions
{
    public StorageTagUpdateMode Mode { get; init; } = StorageTagUpdateMode.Replace;

    public Result Validate(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (!Enum.IsDefined(Mode))
            return Result.Failure(StorageErrors.InvalidPath("The tag update mode is invalid."));
        if (tags.Count > 10)
            return Result.Failure(StorageErrors.TooLarge(
                "Portable object tags cannot contain more than 10 entries."));
        foreach (var (name, value) in tags)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 128 ||
                name.Any(character => !IsPortableTagCharacter(character)))
            {
                return Result.Failure(StorageErrors.InvalidPath(
                    "Tag names must be 1-128 portable ASCII tag characters."));
            }
            if (value is null || value.Length > 256 ||
                value.Any(character => !IsPortableTagCharacter(character)))
            {
                return Result.Failure(StorageErrors.InvalidPath(
                    "Tag values must be 0-256 portable ASCII tag characters."));
            }
        }
        return Result.Success();
    }

    private static bool IsPortableTagCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is ' ' or '+' or '-' or '.' or '/' or ':' or '=' or '_';
}

internal static class StorageMetadataSnapshot
{
    public static IReadOnlyDictionary<string, string> Create(IEnumerable<KeyValuePair<string, string>> values) =>
        new ReadOnlyDictionary<string, string>(
            values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
