using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Capability-gated user metadata reads and in-place updates.</summary>
public interface IStorageMetadataService
{
    Task<Result<IReadOnlyDictionary<string, string>>> GetMetadataAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<StorageItem>> SetMetadataAsync(
        string path,
        IReadOnlyDictionary<string, string> metadata,
        StorageMetadataUpdateOptions? options = null,
        CancellationToken cancellationToken = default);
}
