using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CodeLogic.Core.Results;

namespace CL.Storage.Providers;

internal sealed class RemotePathResolver
{
    private readonly string _remoteRoot;

    public RemotePathResolver(string? root)
    {
        var normalized = StoragePath.Normalize(root ?? string.Empty);
        if (normalized.IsFailure)
            throw new ArgumentException(normalized.Error!.Message, nameof(root));
        Root = normalized.Value!;
        _remoteRoot = Root.Length == 0 ? "/" : "/" + Root;
    }

    public string Root { get; }
    public string RemoteRoot => _remoteRoot;

    public Result<ResolvedRemotePath> Resolve(string path, bool requireNonRoot = false)
    {
        var normalized = StoragePath.Normalize(path);
        if (normalized.IsFailure)
            return Result<ResolvedRemotePath>.Failure(normalized.Error!);
        if (requireNonRoot && normalized.Value!.Length == 0)
            return Result<ResolvedRemotePath>.Failure(StorageErrors.InvalidPath("A non-root storage path is required."));
        var remote = normalized.Value!.Length == 0
            ? _remoteRoot
            : _remoteRoot == "/" ? "/" + normalized.Value : _remoteRoot + "/" + normalized.Value;
        return Result<ResolvedRemotePath>.Success(new ResolvedRemotePath(normalized.Value, remote));
    }

    public string? FromRemotePath(string remotePath)
    {
        var value = remotePath.Replace('\\', '/');
        if (!value.StartsWith('/')) value = "/" + value;
        string relative;
        if (_remoteRoot == "/")
        {
            relative = value.TrimStart('/');
        }
        else if (string.Equals(value, _remoteRoot, StringComparison.Ordinal))
        {
            relative = string.Empty;
        }
        else if (value.StartsWith(_remoteRoot + "/", StringComparison.Ordinal))
        {
            relative = value[(_remoteRoot.Length + 1)..];
        }
        else
        {
            return null;
        }
        var normalized = StoragePath.Normalize(relative);
        return normalized.IsSuccess ? normalized.Value : null;
    }

    public static string Parent(string remotePath)
    {
        var index = remotePath.LastIndexOf('/');
        return index <= 0 ? "/" : remotePath[..index];
    }
}

internal sealed record ResolvedRemotePath(string StoragePath, string RemotePath);
