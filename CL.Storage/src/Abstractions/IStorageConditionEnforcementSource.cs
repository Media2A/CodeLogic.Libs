using CL.Storage.Models;

namespace CL.Storage.Abstractions;

/// <summary>What kind of destination condition a mutation carries.</summary>
internal enum StorageConditionKind
{
    /// <summary>The destination must not exist (create-only upload, copy, or move).</summary>
    CreateOnly = 0,
    /// <summary>The destination must still have an expected ETag or version (upload, copy, or move).</summary>
    MatchVersion = 1,
    /// <summary>A delete that requires an expected ETag or version.</summary>
    DeleteMatchVersion = 2
}

/// <summary>
/// Implemented by backends whose enforcement of a destination condition depends on the server rather than on
/// the provider type (for example S3-compatible servers that ignore <c>If-None-Match</c> on some requests).
/// Callers ask here before reporting <see cref="StorageConditionEnforcement"/>; backends that do not implement
/// it are judged by their <see cref="StorageFeature.ConditionalCreate"/>, <see cref="StorageFeature.ConditionalUpdate"/>
/// and <see cref="StorageFeature.ConditionalDelete"/> flags.
/// </summary>
internal interface IStorageConditionEnforcementSource
{
    /// <summary>Returns how this connection enforces a condition of the given kind.</summary>
    /// <param name="kind">The kind of condition.</param>
    /// <param name="serverSideCopy">Whether the mutation is a server-side copy or move rather than an upload.</param>
    /// <param name="cancellationToken">Token used to cancel a probe.</param>
    /// <returns><see cref="StorageConditionEnforcement.Atomic"/> when the server enforces it in the committing request.</returns>
    ValueTask<StorageConditionEnforcement> GetEnforcementAsync(StorageConditionKind kind, bool serverSideCopy, CancellationToken cancellationToken);
}

/// <summary>Resolves how a connection enforces a destination condition.</summary>
internal static class StorageConditionEnforcements
{
    /// <summary>Asks the backend when it can tell, otherwise judges by its capability flags.</summary>
    public static ValueTask<StorageConditionEnforcement> ForAsync(
        IStorageService service,
        StorageConditionKind kind,
        bool serverSideCopy,
        CancellationToken cancellationToken)
    {
        if (service is IStorageConditionEnforcementSource source)
            return source.GetEnforcementAsync(kind, serverSideCopy, cancellationToken);
        var feature = kind switch
        {
            StorageConditionKind.CreateOnly => StorageFeature.ConditionalCreate,
            StorageConditionKind.MatchVersion => StorageFeature.ConditionalUpdate,
            _ => StorageFeature.ConditionalDelete
        };
        return ValueTask.FromResult(service.Capabilities.Supports(feature)
            ? StorageConditionEnforcement.Atomic
            : StorageConditionEnforcement.CheckedBeforeCommit);
    }
}
