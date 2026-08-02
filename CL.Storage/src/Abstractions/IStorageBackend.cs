using System.Diagnostics.CodeAnalysis;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>
/// Extends a storage service with lifecycle, health, and typed native access.
/// Health implementations should honor cancellation promptly; the library retains
/// backend lifetime protection until the actual health task settles.
/// </summary>
public interface IStorageBackend : IStorageService, IAsyncDisposable
{
    /// <summary>Probes the mounted provider resource without exposing provider exception details.</summary>
    /// <param name="cancellationToken">Token used to cancel the health probe.</param>
    /// <returns>A success result when the mounted root is reachable and authorized.</returns>
    Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Attempts to expose a reusable native client owned by this backend.</summary>
    /// <typeparam name="TClient">Expected provider client type.</typeparam>
    /// <param name="client">Receives the native client when the type and backend lifetime support direct reuse.</param>
    /// <returns><see langword="true"/> when a matching reusable client is available.</returns>
    bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client)
        where TClient : class;

    /// <summary>Opens a scoped native provider connection when the backend cannot safely expose a reusable client.</summary>
    /// <typeparam name="TClient">Expected provider session or client type.</typeparam>
    /// <param name="cancellationToken">Token used to cancel opening the native connection.</param>
    /// <returns>A lease that must be disposed to return or close the native connection.</returns>
    Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default)
        where TClient : class;
}
