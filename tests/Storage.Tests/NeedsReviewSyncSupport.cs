using System.Security.Cryptography;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;

namespace Storage.Tests;

/// <summary>
/// An in-memory storage connection for sync tests: case-sensitive (or not) on every platform, with ETags,
/// optional versions, pinned moves, conditional deletes, server checksums, and hooks to inject failures and
/// concurrent writes at exact points.
/// </summary>
internal sealed class MemoryStorage : IStorageService, IStorageAttributeService, IStorageChecksumService, CL.Storage.Sync.IStorageWatchService
{
    private sealed class Node
    {
        public required string Path { get; set; }
        public bool IsDirectory { get; init; }
        public byte[] Content { get; set; } = [];
        public DateTimeOffset Modified { get; set; }
        public string ETag { get; set; } = string.Empty;
        public string? VersionId { get; set; }
        public bool Hidden { get; set; }
        public string? LinkTarget { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.Ordinal);
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Node> _nodes;
    private readonly bool _caseInsensitive;
    private int _counter;

    public MemoryStorage(string connectionId, bool caseInsensitive = false, StorageFeature extra = StorageFeature.None, bool versioned = false, bool setTimestamps = true)
    {
        ConnectionId = connectionId;
        _caseInsensitive = caseInsensitive;
        Versioned = versioned;
        _nodes = new Dictionary<string, Node>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var features = StorageFeature.PhysicalDirectories | StorageFeature.FileCopy | StorageFeature.FileMove | StorageFeature.RangeReads |
                       StorageFeature.ConditionalCreate | StorageFeature.ConditionalUpdate | StorageFeature.ConditionalDelete | extra;
        if (setTimestamps) features |= StorageFeature.SetTimestamps;
        if (caseInsensitive) features |= StorageFeature.CaseInsensitivePaths;
        if (versioned) features |= StorageFeature.Versioning;
        Capabilities = new StorageCapabilities(features);
    }

    public string ConnectionId { get; }
    public StorageProvider Provider => StorageProvider.Local;
    public string Root { get; set; } = "/memory";
    public StorageCapabilities Capabilities { get; }
    public bool Versioned { get; }

    /// <summary>When set, a pinned move (ExpectedSourceETag / SourceVersionId) answers Unsupported, as a backend that cannot pin one.</summary>
    public bool PinnedMovesUnsupported { get; set; }

    /// <summary>Returns an error to fail a GetInfo, or null to answer normally.</summary>
    public Func<string, Error?>? FailGetInfo { get; set; }
    /// <summary>Called after a GetInfo was answered (with its answer), outside the lock.</summary>
    public Action<string, Result<StorageItem>>? AfterGetInfo { get; set; }
    /// <summary>Returns an error to fail a delete, or null.</summary>
    public Func<string, Error?>? FailDelete { get; set; }
    /// <summary>Throws from a download when it returns an exception.</summary>
    public Func<string, Exception?>? ThrowOnDownload { get; set; }
    /// <summary>Returns an error to fail a download, or null.</summary>
    public Func<string, Error?>? FailDownload { get; set; }
    /// <summary>Called before a move runs; may change the store.</summary>
    public Action<string, string, StorageTransferOptions?>? BeforeMove { get; set; }
    /// <summary>Returns a result to answer a move with instead (the move is not done), or null.</summary>
    public Func<string, string, Result?>? InterceptMove { get; set; }
    /// <summary>Server checksums: returns the hex digest this server keeps for a path and algorithm, or null.</summary>
    public Func<string, StorageChecksumAlgorithm, string?>? ServerChecksum { get; set; }
    /// <summary>Returns an error to fail a server checksum read, or null.</summary>
    public Func<string, Error?>? FailServerChecksum { get; set; }

    /// <summary>Rewrites a move's result after the move was done.</summary>
    public Func<string, string, Result, Result>? AfterMove { get; set; }
    /// <summary>Called after a download was opened (the stream holds the content as it was).</summary>
    public Action<string>? AfterDownload { get; set; }
    /// <summary>Called after timestamps were set.</summary>
    public Action<string>? AfterSetTimestamps { get; set; }
    /// <summary>Returns an error to fail a listing page (by path and page number), or null.</summary>
    public Func<string, int, Error?>? FailList { get; set; }
    /// <summary>The most items one listing page holds.</summary>
    public int MaxPageSize { get; set; } = int.MaxValue;
    /// <summary>Whether items carry no ETag (as on SFTP and FTP).</summary>
    public bool NoETags { get; set; }
    /// <summary>Whether listings leave sizes out (as some servers do).</summary>
    public bool NoSizes { get; set; }
    /// <summary>Native change notifications, when the storage was made with <see cref="StorageFeature.ChangeNotifications"/>.</summary>
    public Func<string, bool, CancellationToken, IAsyncEnumerable<CL.Storage.Sync.StorageChange>>? WatchNative { get; set; }

    public List<(string From, string To, StorageTransferOptions? Options)> Moves { get; } = [];
    public List<string> Deletes { get; } = [];
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);

    // ------------------------------------------------------------ test helpers

    public void Put(string path, string content, DateTimeOffset? modified = null, bool hidden = false, IReadOnlyDictionary<string, string>? metadata = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            var node = Write(path, System.Text.Encoding.UTF8.GetBytes(content));
            node.Modified = modified ?? node.Modified;
            node.Hidden = hidden;
            if (metadata is not null) node.Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        }
    }

    public void PutDirectory(string path, bool hidden = false)
    {
        lock (_gate)
        {
            EnsureParents(path);
            if (!_nodes.ContainsKey(path)) _nodes[path] = new Node { Path = path, IsDirectory = true, Modified = DateTimeOffset.UtcNow, Hidden = hidden };
        }
    }

    public void PutLink(string path, string target)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _nodes[path] = new Node { Path = path, LinkTarget = target, Modified = DateTimeOffset.UtcNow, ETag = NextTag() };
        }
    }

    public string? Text(string path)
    {
        lock (_gate) return _nodes.TryGetValue(path, out var node) && !node.IsDirectory ? System.Text.Encoding.UTF8.GetString(node.Content) : null;
    }

    public bool Has(string path)
    {
        lock (_gate) return _nodes.ContainsKey(path);
    }

    /// <summary>Every path as the store spells it, sorted.</summary>
    public string[] Paths()
    {
        lock (_gate) return [.. _nodes.Values.Where(node => !node.Path.Contains(".cl-storage-", StringComparison.Ordinal)).Select(node => node.Path).Order(StringComparer.Ordinal)];
    }

    public string[] Internal()
    {
        lock (_gate) return [.. _nodes.Keys.Where(path => path.Contains(".cl-storage-", StringComparison.Ordinal))];
    }

    // ------------------------------------------------------------ IStorageService

    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        path = Normalize(path);
        Result<StorageItem> answer;
        if (FailGetInfo?.Invoke(path) is { } error)
        {
            answer = Result<StorageItem>.Failure(error);
        }
        else
        {
            lock (_gate)
            {
                answer = path.Length == 0
                    ? Result<StorageItem>.Success(new StorageItem { Path = string.Empty, Name = string.Empty, ItemType = StorageItemType.Directory })
                    : _nodes.TryGetValue(path, out var node)
                        ? Result<StorageItem>.Success(ToItem(node))
                        : Result<StorageItem>.Failure(StorageErrors.NotFound($"'{path}' was not found."));
            }
        }
        AfterGetInfo?.Invoke(path, answer);
        return Task.FromResult(answer);
    }

    public async Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = await GetInfoAsync(path, cancellationToken);
        if (info.IsSuccess) return Result<bool>.Success(true);
        return info.Error!.Code == StorageErrors.NotFoundCode ? Result<bool>.Success(false) : Result<bool>.Failure(info.Error);
    }

    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new StorageListOptions();
        path = Normalize(path);
        var page = options.ContinuationToken is { } token ? int.Parse(token, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (FailList?.Invoke(path, page) is { } failure) return Task.FromResult(Result<StoragePage>.Failure(failure));
        lock (_gate)
        {
            if (path.Length > 0 && (!_nodes.TryGetValue(path, out var folder) || !folder.IsDirectory))
                return Task.FromResult(Result<StoragePage>.Failure(StorageErrors.NotFound($"'{path}' was not found.")));
            var spelled = path.Length > 0 ? _nodes[path].Path : string.Empty;
            var prefix = spelled.Length == 0 ? string.Empty : spelled + "/";
            var comparison = _caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var items = _nodes.Values
                .Where(node => node.Path.StartsWith(prefix, comparison) && node.Path.Length > prefix.Length &&
                               (options.Recursive || node.Path.IndexOf('/', prefix.Length) < 0))
                .Select(ToItem)
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ToList();
            var visible = CL.Storage.Providers.StorageListFilter.Apply(items, options).ToList();
            var size = Math.Min(MaxPageSize, options.PageSize);
            var slice = visible.Skip(page * size).Take(size).ToList();
            var next = (page + 1) * (long)size < visible.Count ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Task.FromResult(Result<StoragePage>.Success(new StoragePage(slice, next)));
        }
    }

    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (_nodes.TryGetValue(path, out var existing) && !existing.IsDirectory)
                return Task.FromResult(Result.Failure(StorageErrors.Conflict($"'{path}' is a file.")));
            EnsureParents(path);
            if (path.Length > 0 && !_nodes.ContainsKey(path)) _nodes[path] = new Node { Path = path, IsDirectory = true, Modified = DateTimeOffset.UtcNow };
        }
        return Task.FromResult(Result.Success());
    }

    public async Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        return Upload(Normalize(path), buffer.ToArray(), options ?? new StorageUploadOptions());
    }

    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Upload(Normalize(path), content, options ?? new StorageUploadOptions()));

    private Result<StorageItem> Upload(string path, byte[] content, StorageUploadOptions options)
    {
        lock (_gate)
        {
            _nodes.TryGetValue(path, out var existing);
            if (existing is { IsDirectory: true }) return Result<StorageItem>.Failure(StorageErrors.Conflict($"'{path}' is a directory."));
            if (existing is not null && !options.Overwrite) return Result<StorageItem>.Failure(StorageErrors.Conflict($"'{path}' exists."));
            if (options.Condition is { IsEmpty: false } condition && (existing is null || !Satisfies(existing, condition)))
                return Result<StorageItem>.Failure(StorageErrors.Conflict($"'{path}' is not the expected version."));
            if (!ParentExists(path))
            {
                if (!options.CreateParents) return Result<StorageItem>.Failure(StorageErrors.NotFound($"The parent of '{path}' does not exist."));
                EnsureParents(path);
            }
            var node = Write(path, content);
            node.Metadata = new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal);
            return Result<StorageItem>.Success(ToItem(node));
        }
    }

    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        options ??= new StorageDownloadOptions();
        Downloads.AddOrUpdate(path, 1, (_, count) => count + 1);
        if (ThrowOnDownload?.Invoke(path) is { } exception) throw exception;
        if (FailDownload?.Invoke(path) is { } error) return Task.FromResult(Result<Stream>.Failure(error));
        MemoryStream stream;
        lock (_gate)
        {
            if (!_nodes.TryGetValue(path, out var node) || node.IsDirectory)
                return Task.FromResult(Result<Stream>.Failure(StorageErrors.NotFound($"'{path}' was not found.")));
            if (options.VersionId is not null && options.VersionId != node.VersionId)
                return Task.FromResult(Result<Stream>.Failure(StorageErrors.NotFound($"Version '{options.VersionId}' of '{path}' is gone.")));
            var offset = (int)Math.Min(options.Offset, node.Content.Length);
            var length = options.Length is { } requested ? (int)Math.Min(requested, node.Content.Length - offset) : node.Content.Length - offset;
            stream = new MemoryStream(node.Content, offset, length, writable: false);
        }
        AfterDownload?.Invoke(path);
        return Task.FromResult(Result<Stream>.Success((Stream)stream));
    }

    public async Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        var stream = await DownloadAsync(path, options, cancellationToken);
        if (stream.IsFailure) return Result<byte[]>.Failure(stream.Error!);
        await using var content = stream.Value!;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return Result<byte[]>.Success(buffer.ToArray());
    }

    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        options ??= new StorageDeleteOptions();
        if (FailDelete?.Invoke(path) is { } error) return Task.FromResult(Result.Failure(error));
        lock (_gate)
        {
            if (!_nodes.TryGetValue(path, out var node))
                return Task.FromResult(options.IgnoreMissing ? Result.Success() : Result.Failure(StorageErrors.NotFound($"'{path}' was not found.")));
            if (options.Condition is { IsEmpty: false } condition && !Satisfies(node, condition))
                return Task.FromResult(Result.Failure(StorageErrors.Conflict($"'{path}' is not the expected version.")));
            var below = Below(node.Path);
            if (node.IsDirectory && below.Count > 0 && !options.Recursive)
                return Task.FromResult(Result.Failure(StorageErrors.Conflict($"'{path}' is not empty.")));
            foreach (var child in below) _nodes.Remove(child);
            _nodes.Remove(path);
            Deletes.Add(node.Path);
        }
        return Task.FromResult(Result.Success());
    }

    public Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Transfer(Normalize(sourcePath), Normalize(destinationPath), options ?? new StorageTransferOptions(), move: false));

    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        sourcePath = Normalize(sourcePath);
        destinationPath = Normalize(destinationPath);
        lock (_gate) Moves.Add((sourcePath, destinationPath, options));
        BeforeMove?.Invoke(sourcePath, destinationPath, options);
        if (InterceptMove?.Invoke(sourcePath, destinationPath) is { } intercepted) return Task.FromResult(intercepted);
        var moved = Transfer(sourcePath, destinationPath, options ?? new StorageTransferOptions(), move: true);
        return Task.FromResult(AfterMove?.Invoke(sourcePath, destinationPath, moved) ?? moved);
    }

    private Result Transfer(string from, string to, StorageTransferOptions options, bool move)
    {
        var pinned = options.ExpectedSourceETag is not null || options.SourceVersionId is not null;
        if (pinned && PinnedMovesUnsupported)
            return Result.Failure(StorageErrors.Unsupported("This connection cannot pin a move to a version."));
        lock (_gate)
        {
            if (!_nodes.TryGetValue(from, out var source) || source.IsDirectory)
                return Result.Failure(StorageErrors.NotFound($"'{from}' was not found."));
            if (options.ExpectedSourceETag is { } eTag && !CL.Storage.Registry.StagedWriter.SameETag(eTag, source.ETag))
                return Result.Failure(StorageErrors.Conflict($"'{from}' is not the expected version."));
            if (options.SourceVersionId is { } version && version != source.VersionId)
                return Result.Failure(StorageErrors.Conflict($"'{from}' is not the expected version."));
            _nodes.TryGetValue(to, out var existing);
            if (existing is not null && (!options.Overwrite || existing.IsDirectory))
                return Result.Failure(StorageErrors.Conflict($"'{to}' exists."));
            if (options.DestinationCondition is { IsEmpty: false } condition && (existing is null || !Satisfies(existing, condition)))
                return Result.Failure(StorageErrors.Conflict($"'{to}' is not the expected version."));
            if (!ParentExists(to))
            {
                if (!options.CreateParents) return Result.Failure(StorageErrors.NotFound($"The parent of '{to}' does not exist."));
                EnsureParents(to);
            }
            if (existing is not null) _nodes.Remove(to);
            var copy = new Node
            {
                Path = Spell(to),
                Content = source.Content,
                Modified = source.Modified,
                ETag = move ? source.ETag : NextTag(),
                VersionId = Versioned ? NextVersion() : null,
                Hidden = source.Hidden,
                Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.Ordinal)
            };
            _nodes[to] = copy;
            if (move) _nodes.Remove(from);
            return Result.Success();
        }
    }

    // ------------------------------------------------------------ attributes and checksums

    public Task<Result> SetTimestampsAsync(string path, DateTimeOffset? lastModified, DateTimeOffset? lastAccessed = null, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(path, out var node)) return Task.FromResult(Result.Failure(StorageErrors.NotFound($"'{path}' was not found.")));
            if (lastModified is { } time) node.Modified = time;
        }
        AfterSetTimestamps?.Invoke(path);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SetPermissionsAsync(string path, int unixMode, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("no permissions")));

    public Task<Result> SetOwnerAsync(string path, long? ownerId, long? groupId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("no owners")));

    public Task<Result> CreateLinkAsync(string linkPath, string targetPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(StorageErrors.Unsupported("no links")));

    public Task<Result<StorageLinkInfo>> ReadLinkAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<StorageLinkInfo>.Failure(StorageErrors.Unsupported("no links")));

    public Task<Result<StorageChecksum>> GetServerChecksumAsync(string path, StorageChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        path = Normalize(path);
        if (FailServerChecksum?.Invoke(path) is { } error) return Task.FromResult(Result<StorageChecksum>.Failure(error));
        var value = ServerChecksum?.Invoke(path, algorithm);
        if (value is null) return Task.FromResult(Result<StorageChecksum>.Failure(StorageErrors.Unsupported("No such checksum on this server.")));
        lock (_gate)
        {
            var length = _nodes.TryGetValue(path, out var node) ? node.Content.LongLength : 0;
            return Task.FromResult(Result<StorageChecksum>.Success(new StorageChecksum(algorithm, value, length, StorageChecksumSource.Server)));
        }
    }

    /// <summary>The digest a server would keep for a file's current content.</summary>
    public string Digest(string path, StorageChecksumAlgorithm algorithm)
    {
        lock (_gate)
        {
            var content = _nodes[Normalize(path)].Content;
            return Convert.ToHexStringLower(algorithm == StorageChecksumAlgorithm.Md5 ? MD5.HashData(content) : SHA256.HashData(content));
        }
    }

    public IAsyncEnumerable<CL.Storage.Sync.StorageChange> WatchNativeAsync(string path, bool recursive, CancellationToken cancellationToken) =>
        WatchNative is { } watch ? watch(path, recursive, cancellationToken) : throw new NotSupportedException("No native watching.");

    // ------------------------------------------------------------ internals

    private static string Normalize(string path) => StoragePath.Normalize(path).Value ?? path;

    private string NextTag() => $"\"e{Interlocked.Increment(ref _counter)}\"";
    private string NextVersion() => $"v{Interlocked.Increment(ref _counter)}";

    private Node Write(string path, byte[] content)
    {
        if (!_nodes.TryGetValue(path, out var node))
            _nodes[path] = node = new Node { Path = Spell(path) };
        node.Content = content;
        node.Modified = DateTimeOffset.UtcNow;
        node.ETag = NextTag();
        node.VersionId = Versioned ? NextVersion() : null;
        return node;
    }

    /// <summary>A new path under the spelling of the folders it lands in (as a case-insensitive file system does).</summary>
    private string Spell(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 || !_nodes.TryGetValue(path[..slash], out var parent) ? path : $"{parent.Path}/{path[(slash + 1)..]}";
    }

    private bool ParentExists(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 || (_nodes.TryGetValue(path[..slash], out var parent) && parent.IsDirectory);
    }

    private void EnsureParents(string path)
    {
        var slash = path.LastIndexOf('/');
        if (slash < 0) return;
        var parent = path[..slash];
        if (_nodes.ContainsKey(parent)) return;
        EnsureParents(parent);
        _nodes[parent] = new Node { Path = Spell(parent), IsDirectory = true, Modified = DateTimeOffset.UtcNow };
    }

    private List<string> Below(string path)
    {
        var comparison = _caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return [.. _nodes.Keys.Where(key => key.StartsWith(path + "/", comparison))];
    }

    private static bool Satisfies(Node node, StorageMutationCondition condition) =>
        (condition.ExpectedETag is null || CL.Storage.Registry.StagedWriter.SameETag(condition.ExpectedETag, node.ETag)) &&
        (condition.ExpectedVersionId is null || condition.ExpectedVersionId == node.VersionId);

    private StorageItem ToItem(Node node) => new()
    {
        Path = node.Path,
        Name = node.Path[(node.Path.LastIndexOf('/') + 1)..],
        ItemType = node.IsDirectory ? StorageItemType.Directory : node.LinkTarget is not null ? StorageItemType.Link : StorageItemType.File,
        Size = node.IsDirectory || NoSizes ? null : node.Content.LongLength,
        LastModified = node.Modified,
        ETag = node.IsDirectory || NoETags ? null : node.ETag,
        VersionId = node.VersionId,
        LinkTarget = node.LinkTarget,
        IsHidden = node.Hidden || node.Path[(node.Path.LastIndexOf('/') + 1)..].StartsWith('.'),
        Metadata = node.Metadata
    };
}

/// <summary>A test that only means something on Windows (file attributes); skipped elsewhere.</summary>
internal sealed class WindowsFactAttribute : Xunit.FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Needs Windows file attributes.";
    }
}
