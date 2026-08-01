using System.Collections.ObjectModel;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Models;

public sealed record StorageListOptions
{
    public bool Recursive { get; init; }
    public int PageSize { get; init; } = 1000;
    public string? ContinuationToken { get; init; }

    public Result Validate() => PageSize > 0
        ? Result.Success()
        : Result.Failure(StorageErrors.InvalidPath("PageSize must be greater than zero."));
}

public sealed record StorageUploadOptions
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private IReadOnlyDictionary<string, string> _metadata = EmptyMetadata;

    public bool Overwrite { get; init; } = true;
    public bool CreateParents { get; init; } = true;
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = value is null || value.Count == 0
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    public Result Validate() => Result.Success();
}

public sealed record StorageDownloadOptions
{
    public long Offset { get; init; }
    public long? Length { get; init; }
    public long? MaxBufferedBytes { get; init; }

    public Result Validate()
    {
        if (Offset < 0)
            return Result.Failure(StorageErrors.InvalidPath("Offset cannot be negative."));
        if (Length is <= 0)
            return Result.Failure(StorageErrors.InvalidPath("Length must be greater than zero."));
        if (MaxBufferedBytes is <= 0)
            return Result.Failure(StorageErrors.InvalidPath("MaxBufferedBytes must be greater than zero."));
        if (Length.HasValue && Offset > long.MaxValue - Length.Value)
            return Result.Failure(StorageErrors.InvalidPath("The requested range overflows Int64."));
        return Result.Success();
    }
}

public sealed record StorageDeleteOptions
{
    public bool Recursive { get; init; }
    public bool IgnoreMissing { get; init; }

    public Result Validate() => Result.Success();
}

public sealed record StorageTransferOptions
{
    public bool Overwrite { get; init; } = true;
    public bool CreateParents { get; init; } = true;

    public Result Validate() => Result.Success();
}
