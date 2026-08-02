using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Abstractions;

/// <summary>Normalizes provider-neutral paths without allowing parent traversal.</summary>
public static class StoragePath
{
    /// <summary>Converts slash variants and redundant segments into a root-relative portable path.</summary>
    /// <param name="path">Path to normalize. Empty and root-like paths normalize to an empty string.</param>
    /// <returns>The normalized path, or <c>storage.invalid_path</c> for NUL or parent traversal.</returns>
    public static Result<string> Normalize(string path)
    {
        if (path is null)
            return Result<string>.Failure(StorageErrors.InvalidPath("A storage path cannot be null."));
        if (path.IndexOf('\0') >= 0)
            return Result<string>.Failure(StorageErrors.InvalidPath("A storage path cannot contain NUL."));

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
                continue;
            if (part == "..")
                return Result<string>.Failure(StorageErrors.InvalidPath("Parent traversal is not allowed."));
            normalized.Add(part);
        }

        return Result<string>.Success(string.Join('/', normalized));
    }
}
