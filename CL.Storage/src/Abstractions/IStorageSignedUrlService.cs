using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Capability-gated temporary signed read/write URLs.</summary>
public interface IStorageSignedUrlService
{
    /// <summary>Creates a short-lived provider-signed URL for one exact object operation.</summary>
    /// <param name="path">Object path relative to the mounted root.</param>
    /// <param name="options">HTTP method, lifetime, content type, and optional version.</param>
    /// <param name="cancellationToken">Token used to cancel URL creation.</param>
    /// <returns>A signed URL that must be handled as a secret.</returns>
    Task<Result<StorageSignedUrl>> CreateSignedUrlAsync(
        string path,
        StorageSignedUrlOptions? options = null,
        CancellationToken cancellationToken = default);
}
