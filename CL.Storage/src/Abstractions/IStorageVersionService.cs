using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Capability-gated object-version discovery and permanent version deletion.</summary>
public interface IStorageVersionService
{
    Task<Result<StorageVersionPage>> ListVersionsAsync(
        string path,
        StorageVersionListOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests deletion of one exact version without targeting the current version implicitly.
    /// Provider retention, soft-delete, and governance policies can retain the underlying data.
    /// </summary>
    Task<Result> DeleteVersionAsync(
        string path,
        string versionId,
        CancellationToken cancellationToken = default);
}
