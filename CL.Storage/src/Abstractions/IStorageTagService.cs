using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Capability-gated object tag reads and updates.</summary>
public interface IStorageTagService
{
    /// <summary>Reads the provider-native tags attached to one exact object.</summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>An immutable tag dictionary.</returns>
    Task<Result<IReadOnlyDictionary<string, string>>> GetTagsAsync(
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Merges or replaces the portable tag set attached to one exact object.</summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="tags">Caller-owned tag values copied before asynchronous use.</param>
    /// <param name="options">Merge or replacement settings.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The updated object description.</returns>
    Task<Result<StorageItem>> SetTagsAsync(
        string path,
        IReadOnlyDictionary<string, string> tags,
        StorageTagUpdateOptions? options = null,
        CancellationToken cancellationToken = default);
}
