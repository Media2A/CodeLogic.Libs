using System.Collections.ObjectModel;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>Controls whether a metadata update merges with or replaces existing user metadata.</summary>
public enum StorageMetadataUpdateMode
{
    /// <summary>Overlay supplied keys while preserving metadata keys not present in the update.</summary>
    Merge,
    /// <summary>Replace the complete user-metadata dictionary with the supplied values.</summary>
    Replace
}

/// <summary>Controls a capability-gated metadata update.</summary>
public sealed record StorageMetadataUpdateOptions
{
    /// <summary>Gets the merge or replacement behavior.</summary>
    public StorageMetadataUpdateMode Mode { get; init; } = StorageMetadataUpdateMode.Replace;
    /// <summary>Only update when the current provider ETag matches this value.</summary>
    public string? ExpectedETag { get; init; }
    /// <summary>Only update when the current provider version/generation token matches this value.</summary>
    public string? ExpectedVersionId { get; init; }

    /// <summary>Validates metadata values and optional identity tokens.</summary>
    /// <param name="metadata">Metadata dictionary that will be applied.</param>
    /// <returns>A validation result suitable for returning before contacting the provider.</returns>
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
    /// <summary>Authorize an HTTP read of an existing object.</summary>
    Read,
    /// <summary>Authorize an HTTP write of object content.</summary>
    Write
}

/// <summary>Controls creation of a temporary provider-signed object URL.</summary>
public sealed record StorageSignedUrlOptions
{
    /// <summary>Gets the object operation authorized by the URL.</summary>
    public StorageSignedUrlMethod Method { get; init; } = StorageSignedUrlMethod.Read;
    /// <summary>Gets how long the signed URL remains valid, from one second through seven days.</summary>
    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Gets the content type bound to a signed write request, when required.</summary>
    public string? ContentType { get; init; }
    /// <summary>Gets the exact provider version targeted by a signed read URL.</summary>
    public string? VersionId { get; init; }

    /// <summary>Validates the expiry, method, content type, and optional version combination.</summary>
    /// <returns>A provider-neutral validation result.</returns>
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
/// <param name="Url">Secret-bearing absolute provider URL.</param>
/// <param name="Method">Object operation authorized by the URL.</param>
/// <param name="ExpiresAt">Absolute time after which the provider rejects the URL.</param>
public sealed record StorageSignedUrl(
    Uri Url,
    StorageSignedUrlMethod Method,
    DateTimeOffset ExpiresAt);

/// <summary>Incremental bytes observed while a convenience upload/download stream is consumed.</summary>
/// <param name="BytesTransferred">Cumulative content bytes consumed or produced.</param>
/// <param name="TotalBytes">Expected total byte count when known.</param>
/// <param name="IsCompleted">Whether the transfer reached successful completion.</param>
public sealed record StorageTransferProgress(
    long BytesTransferred,
    long? TotalBytes,
    bool IsCompleted);

/// <summary>Controls one provider page of versions for an exact object path.</summary>
public sealed record StorageVersionListOptions
{
    /// <summary>Gets the requested maximum number of provider entries in one page.</summary>
    public int PageSize { get; init; } = 1000;
    /// <summary>Gets the opaque token returned by the previous page.</summary>
    public string? ContinuationToken { get; init; }
    /// <summary>Gets whether provider delete markers are included with content versions.</summary>
    public bool IncludeDeleteMarkers { get; init; } = true;

    /// <summary>Validates the page size and continuation token.</summary>
    /// <returns>A provider-neutral validation result.</returns>
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
    /// <summary>Gets the provider-neutral object path shared by all versions in the result.</summary>
    public required string Path { get; init; }
    /// <summary>Gets the opaque provider version, generation, or snapshot identifier.</summary>
    public required string VersionId { get; init; }
    /// <summary>Gets the version-specific entity tag when exposed by the provider.</summary>
    public string? ETag { get; init; }
    /// <summary>Gets the content length, or <see langword="null"/> for delete markers.</summary>
    public long? Size { get; init; }
    /// <summary>Gets when this version was last modified.</summary>
    public DateTimeOffset? LastModified { get; init; }
    /// <summary>Gets whether this entry is the provider's current version.</summary>
    public bool IsLatest { get; init; }
    /// <summary>Gets whether this entry represents deletion rather than object content.</summary>
    public bool IsDeleteMarker { get; init; }
}

/// <summary>One page of versions and the opaque token for the next page.</summary>
/// <param name="Versions">Immutable version entries for the exact requested object.</param>
/// <param name="ContinuationToken">Opaque token for the next page, or <see langword="null"/> at the end.</param>
public sealed record StorageVersionPage(
    IReadOnlyList<StorageVersion> Versions,
    string? ContinuationToken);

/// <summary>Client-side digest algorithm used by provider-neutral integrity helpers.</summary>
public enum StorageChecksumAlgorithm
{
    /// <summary>MD5 for compatibility with legacy provider digests; not recommended for security decisions.</summary>
    Md5,
    /// <summary>SHA-256 digest.</summary>
    Sha256,
    /// <summary>SHA-384 digest.</summary>
    Sha384,
    /// <summary>SHA-512 digest.</summary>
    Sha512
}

/// <summary>A lowercase hexadecimal digest and the number of bytes included in it.</summary>
/// <param name="Algorithm">Digest algorithm used.</param>
/// <param name="HexValue">Lowercase hexadecimal digest.</param>
/// <param name="BytesProcessed">Number of content bytes included in the digest.</param>
public sealed record StorageChecksum(
    StorageChecksumAlgorithm Algorithm,
    string HexValue,
    long BytesProcessed);

/// <summary>The actual digest and constant-time comparison outcome for an expected digest.</summary>
/// <param name="Actual">Digest calculated from storage content.</param>
/// <param name="Matches">Whether the expected and actual digest bytes match.</param>
public sealed record StorageChecksumVerification(
    StorageChecksum Actual,
    bool Matches);

/// <summary>Counts a completed rollback-safe local-directory upload or download.</summary>
/// <param name="Files">Number of transferred files.</param>
/// <param name="Directories">Number of transferred directories.</param>
/// <param name="Bytes">Total file-content bytes transferred.</param>
public sealed record StorageDirectoryTransferReport(
    long Files,
    long Directories,
    long Bytes);

/// <summary>Controls whether a tag update merges with or replaces existing tags.</summary>
public enum StorageTagUpdateMode
{
    /// <summary>Overlay supplied keys while preserving tags not present in the update.</summary>
    Merge,
    /// <summary>Replace the complete tag set with the supplied values.</summary>
    Replace
}

/// <summary>Controls a capability-gated object tag update.</summary>
public sealed record StorageTagUpdateOptions
{
    /// <summary>Gets the merge or replacement behavior.</summary>
    public StorageTagUpdateMode Mode { get; init; } = StorageTagUpdateMode.Replace;

    /// <summary>Validates portable tag count, key, value, and character limits.</summary>
    /// <param name="tags">Tags that will be applied.</param>
    /// <returns>A provider-neutral validation result.</returns>
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
