using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

/// <summary>Controls bounded concurrency for provider-neutral batch helpers.</summary>
public sealed record StorageBatchOptions
{
    public int MaxConcurrency { get; init; } = 4;
    public int MaxItems { get; init; } = 10_000;

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
public sealed record StorageTransferRequest(string SourcePath, string DestinationPath);

/// <summary>Contains the provider result for one path while preserving input order.</summary>
public sealed record StorageBatchItemResult<T>(int Index, string Path, Result<T> Result);

/// <summary>Contains the provider result for one mutation while preserving input order.</summary>
public sealed record StorageBatchMutationResult(
    int Index,
    string SourcePath,
    string? DestinationPath,
    Result Result);
