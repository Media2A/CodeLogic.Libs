using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Capability-gated user metadata reads and in-place updates.</summary>
public interface IStorageMetadataService
{
    /// <summary>Reads a snapshot of provider user metadata for one exact object.</summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>An immutable metadata dictionary using provider-normalized keys.</returns>
    Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Merges or replaces provider user metadata without replacing object content.</summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="metadata">Caller-owned metadata copied before asynchronous use.</param>
    /// <param name="options">Update mode and optional identity conditions.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The updated object description.</returns>
    Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default);
}
