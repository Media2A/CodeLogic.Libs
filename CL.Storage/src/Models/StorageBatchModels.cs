using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>Controls bounded concurrency for provider-neutral batch helpers.</summary>
public sealed record StorageBatchOptions
{
    /// <summary>Gets the maximum number of provider operations allowed to run concurrently.</summary>
    public int MaxConcurrency { get; init; } = 4;
    /// <summary>Gets the maximum accepted batch input count.</summary>
    public int MaxItems { get; init; } = 10_000;

    /// <summary>Validates concurrency and input bounds.</summary>
    /// <returns>A provider-neutral validation result.</returns>
    public Result Validate()
    {
        if (MaxConcurrency is < 1 or > 256)
            return Result.Failure(StorageErrors.InvalidPath("MaxConcurrency must be between 1 and 256."));
        if (MaxItems < 1)
            return Result.Failure(StorageErrors.InvalidPath("MaxItems must be greater than zero."));
        return Result.Success();
    }
}

/// <summary>Identifies one source/destination operation in a copy or move batch.</summary>
/// <param name="SourcePath">Source path relative to the connection root.</param>
/// <param name="DestinationPath">Destination path relative to the connection root.</param>
public sealed record StorageTransferRequest(string SourcePath, string DestinationPath);

/// <summary>Contains the provider result for one path while preserving input order.</summary>
/// <typeparam name="T">Successful result value type.</typeparam>
/// <param name="Index">Zero-based index of the original input.</param>
/// <param name="Path">Original provider-neutral path.</param>
/// <param name="Result">Per-item success or failure.</param>
public sealed record StorageBatchItemResult<T>(int Index, string Path, Result<T> Result);

/// <summary>Contains the provider result for one mutation while preserving input order.</summary>
/// <param name="Index">Zero-based index of the original input.</param>
/// <param name="SourcePath">Original source or mutation path.</param>
/// <param name="DestinationPath">Destination path for copy/move operations; otherwise <see langword="null"/>.</param>
/// <param name="Result">Per-item success or failure.</param>
public sealed record StorageBatchMutationResult(
    int Index,
    string SourcePath,
    string? DestinationPath,
    Result Result);
