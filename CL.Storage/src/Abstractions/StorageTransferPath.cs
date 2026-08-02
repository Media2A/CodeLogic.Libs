using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Validates relationships between two normalized storage paths.</summary>
internal static class StorageTransferPath
{
    internal static Result ValidateDistinct(
        string sourcePath,
        string destinationPath,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var source = StoragePath.Normalize(sourcePath);
        if (source.IsFailure)
            return Result.Failure(source.Error!);
        var destination = StoragePath.Normalize(destinationPath);
        if (destination.IsFailure)
            return Result.Failure(destination.Error!);

        return string.Equals(source.Value, destination.Value, comparison)
            ? Result.Failure(StorageErrors.InvalidPath(
                "The source and destination paths must be different."))
            : Result.Success();
    }

    internal static Result ValidateDirectoryDestination(
        string sourcePath,
        string destinationPath,
        StringComparison comparison = StringComparison.Ordinal)
    {
        var distinct = ValidateDistinct(sourcePath, destinationPath, comparison);
        if (distinct.IsFailure)
            return distinct;

        var source = StoragePath.Normalize(sourcePath).Value!;
        var destination = StoragePath.Normalize(destinationPath).Value!;
        var prefix = source.Length == 0 ? string.Empty : source + "/";
        var nested = source.Length == 0
            ? destination.Length > 0
            : destination.StartsWith(prefix, comparison);
        return nested
            ? Result.Failure(StorageErrors.InvalidPath(
                "A directory cannot be copied or moved into one of its own descendants."))
            : Result.Success();
    }
}
