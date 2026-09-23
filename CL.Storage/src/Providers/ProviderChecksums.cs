using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

/// <summary>Converts provider-reported digests into <see cref="StorageChecksum"/> values.</summary>
internal static class ProviderChecksums
{
    /// <summary>Returns a server checksum from raw digest bytes.</summary>
    public static Result<StorageChecksum> FromBytes(StorageChecksumAlgorithm algorithm, byte[]? digest) =>
        digest is { Length: > 0 } && digest.Length == ExpectedLength(algorithm)
            ? Result<StorageChecksum>.Success(new StorageChecksum(algorithm, Convert.ToHexStringLower(digest), 0, StorageChecksumSource.Server))
            : Unavailable(algorithm);

    /// <summary>Returns a server checksum from a base64 digest, as S3 and GCS report them.</summary>
    public static Result<StorageChecksum> FromBase64(StorageChecksumAlgorithm algorithm, string? base64)
    {
        // Composite (multipart) checksums carry a "-parts" suffix and do not describe the whole content.
        if (string.IsNullOrWhiteSpace(base64) || base64.Contains('-'))
            return Unavailable(algorithm);
        try { return FromBytes(algorithm, Convert.FromBase64String(base64)); }
        catch (FormatException) { return Unavailable(algorithm); }
    }

    /// <summary>Returns a server checksum from hexadecimal text.</summary>
    public static Result<StorageChecksum> FromHex(StorageChecksumAlgorithm algorithm, string? hex)
    {
        var value = hex?.Trim().Trim('"');
        if (string.IsNullOrEmpty(value) || value.Length != ExpectedLength(algorithm) * 2 || !value.All(Uri.IsHexDigit))
            return Unavailable(algorithm);
        return Result<StorageChecksum>.Success(new StorageChecksum(algorithm, value.ToLowerInvariant(), 0, StorageChecksumSource.Server));
    }

    public static Result<StorageChecksum> Unavailable(StorageChecksumAlgorithm algorithm, string? reason = null) =>
        Result<StorageChecksum>.Failure(StorageErrors.Unsupported(
            reason ?? $"The server holds no {algorithm} checksum for this item."));

    private static int ExpectedLength(StorageChecksumAlgorithm algorithm) => algorithm switch
    {
        StorageChecksumAlgorithm.Md5 => 16,
        StorageChecksumAlgorithm.Sha256 => 32,
        StorageChecksumAlgorithm.Sha384 => 48,
        StorageChecksumAlgorithm.Sha512 => 64,
        _ => -1
    };
}
