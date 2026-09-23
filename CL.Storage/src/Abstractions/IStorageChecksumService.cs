using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>
/// Reads a checksum the server already holds, so content can be verified without downloading it.
/// Advertised with <see cref="StorageFeature.Checksums"/>; a server may still lack a given algorithm
/// or a stored value for a given object, in which case the call returns <c>storage.unsupported</c>.
/// </summary>
public interface IStorageChecksumService
{
    /// <summary>Returns the server-side checksum of one file.</summary>
    /// <param name="path">File path relative to the mounted root.</param>
    /// <param name="algorithm">Requested digest algorithm.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>A checksum whose <see cref="StorageChecksum.Source"/> is <see cref="StorageChecksumSource.Server"/>.</returns>
    Task<Result<StorageChecksum>> GetServerChecksumAsync(
        string path,
        StorageChecksumAlgorithm algorithm,
        CancellationToken cancellationToken = default);
}
