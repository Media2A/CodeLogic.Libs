using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers.Local;

internal sealed class LocalPathResolver
{
    private readonly bool _followLinks;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public LocalPathResolver(string rootPath, bool followLinks)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A local root path is required.", nameof(rootPath));
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _followLinks = followLinks;
    }

    public string Root { get; }

    public Result<ResolvedLocalPath> Resolve(string path) => Resolve(path, allowFinalLink: false);

    /// <summary>
    /// Resolves a path whose last segment may itself be a link, without following it. Used to create,
    /// read, or change a link while every parent directory still gets the usual link checks.
    /// </summary>
    public Result<ResolvedLocalPath> ResolveLink(string path) => Resolve(path, allowFinalLink: true);

    private Result<ResolvedLocalPath> Resolve(string path, bool allowFinalLink)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Result<ResolvedLocalPath>.Failure(normalized.Error!);

        try
        {
            var fullPath = normalized.Value!.Length == 0
                ? Root
                : Path.GetFullPath(Path.Combine(Root, normalized.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsContained(fullPath))
                return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath("The path escapes the configured root."));

            var current = Root;
            var segments = normalized.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                if (allowFinalLink && index == segments.Length - 1)
                    break;
                current = Path.Combine(current, segments[index]);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    break;
                }

                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    continue;
                if (!_followLinks)
                    return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath("Links and reparse points are disabled for this connection."));

                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !IsContained(Path.GetFullPath(target.FullName)))
                    return Result<ResolvedLocalPath>.Failure(StorageErrors.InvalidPath("A link resolves outside the configured root."));
            }

            return Result<ResolvedLocalPath>.Success(new ResolvedLocalPath(normalized.Value, fullPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            return Result<ResolvedLocalPath>.Failure(StorageErrors.FromException(error, "Resolve path"));
        }
    }

    /// <summary>Maps a contained full path back to a storage path, or returns null when it lies outside the root.</summary>
    public string? ToStoragePath(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!IsContained(full)) return null;
        var relative = Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? string.Empty : relative;
    }

    public bool IsContained(string fullPath)
    {
        if (string.Equals(fullPath, Root, _comparison))
            return true;
        var prefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, _comparison);
    }
}

internal sealed record ResolvedLocalPath(string StoragePath, string FullPath);
