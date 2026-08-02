using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Capability-gated object-version discovery and permanent version deletion.</summary>
public interface IStorageVersionService
{
    /// <summary>Lists one provider page of versions for one exact object path.</summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="options">Page size, continuation token, and delete-marker policy.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>One page of versions and an opaque continuation token.</returns>
    Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests deletion of one exact version without targeting the current version implicitly.
    /// Provider retention, soft-delete, and governance policies can retain the underlying data.
    /// </summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="versionId">Opaque provider version or generation identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>A provider-neutral deletion result.</returns>
    Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default);
}
