using System.Diagnostics.CodeAnalysis;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Extends a storage service with lifecycle, health, and typed native access.</summary>
public interface IStorageBackend : IStorageService, IAsyncDisposable
{
    Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default);

    bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client)
        where TClient : class;

    Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default)
        where TClient : class;
}
