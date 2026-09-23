using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>
/// Appends content to the end of a file without rewriting it, advertised with
/// <see cref="StorageFeature.Append"/>. Also powers <see cref="StorageConflictPolicy.Resume"/>.
/// </summary>
/// <remarks>
/// Appends write in place: unlike uploads they are not staged and renamed, so an interrupted append
/// leaves the bytes written so far, which is exactly what a later resume continues from.
/// </remarks>
public interface IStorageAppendService
{
    /// <summary>Appends a stream to a file, creating the file when it does not exist.</summary>
    /// <param name="path">File path relative to the mounted root.</param>
    /// <param name="source">Content to append; read from its current position to the end.</param>
    /// <param name="cancellationToken">Token used to cancel the provider request.</param>
    /// <returns>The updated file description.</returns>
    Task<Result<StorageItem>> AppendAsync(string path, Stream source, CancellationToken cancellationToken = default);
}
