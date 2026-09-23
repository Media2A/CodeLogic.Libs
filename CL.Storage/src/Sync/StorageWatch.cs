using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;

namespace CL.Storage.Sync;

/// <summary>What happened to an item.</summary>
public enum StorageChangeKind
{
    /// <summary>The item appeared.</summary>
    Created,
    /// <summary>The item's content, size, or time changed.</summary>
    Changed,
    /// <summary>The item disappeared.</summary>
    Deleted,
    /// <summary>The item was renamed (native watching only; polling reports a delete and a create).</summary>
    Renamed,
    /// <summary>
    /// Changes were lost because notifications arrived faster than they could be buffered (native watching
    /// only). <see cref="StorageChange.Path"/> is the watched directory; list it again to catch up.
    /// </summary>
    Overflow
}

/// <summary>One observed change.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Path">Storage path of the item after the change.</param>
/// <param name="OldPath">Previous path, for renames.</param>
/// <param name="ItemType">Item type, when known.</param>
/// <param name="ObservedAt">When the change was noticed.</param>
public sealed record StorageChange(StorageChangeKind Kind, string Path, string? OldPath, StorageItemType? ItemType, DateTimeOffset ObservedAt);

/// <summary>Controls watching a directory.</summary>
public sealed record StorageWatchOptions
{
    /// <summary>Gets whether subdirectories are watched too.</summary>
    public bool Recursive { get; init; } = true;
    /// <summary>Gets how often polling providers re-list the directory. Native watching ignores it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets whether to poll even when the provider can notify natively.</summary>
    public bool ForcePolling { get; init; }
    /// <summary>
    /// Gets whether polls are incremental: a folder is listed again only when its modification time changed,
    /// which is when entries were added, removed, or renamed in it. Edits to a file's content, and changes
    /// deep inside a folder whose own time did not change, are caught by the full rescan every
    /// <see cref="FullRescanEvery"/> polls. Providers without folder times (object stores) list everything.
    /// </summary>
    public bool Incremental { get; init; }
    /// <summary>Gets how many polls pass between full rescans in incremental mode.</summary>
    public int FullRescanEvery { get; init; } = 10;
}

/// <summary>Native change notifications, advertised with <see cref="StorageFeature.ChangeNotifications"/>.</summary>
public interface IStorageWatchService
{
    /// <summary>Streams changes under a directory until cancelled.</summary>
    /// <param name="path">Directory to watch.</param>
    /// <param name="recursive">Whether subdirectories are included.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>Changes as they happen.</returns>
    IAsyncEnumerable<StorageChange> WatchNativeAsync(string path, bool recursive, CancellationToken cancellationToken);
}

/// <summary>Watches directories on any connection.</summary>
public static class StorageWatch
{
    /// <summary>
    /// Streams changes under a directory until cancelled: natively where the connection supports it
    /// (local folders), otherwise by listing on <see cref="StorageWatchOptions.PollInterval"/> and diffing.
    /// Listing failures while polling are skipped and retried on the next interval.
    /// </summary>
    /// <param name="storage">Connection to watch.</param>
    /// <param name="path">Directory to watch.</param>
    /// <param name="options">Recursion and polling settings.</param>
    /// <param name="cancellationToken">Stops watching.</param>
    /// <returns>Changes as they are observed.</returns>
    public static IAsyncEnumerable<StorageChange> WatchAsync(
        this IStorageService storage,
        string path,
        StorageWatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        options ??= new StorageWatchOptions();
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must be positive.");
        if (options.FullRescanEvery < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "FullRescanEvery must be at least 1.");
        return !options.ForcePolling && storage is IStorageWatchService native && storage.Capabilities.Supports(StorageFeature.ChangeNotifications)
            ? native.WatchNativeAsync(path, options.Recursive, cancellationToken)
            : PollAsync(storage, path, options, cancellationToken);
    }

    private static async IAsyncEnumerable<StorageChange> PollAsync(
        IStorageService storage,
        string path,
        StorageWatchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var previous = await SnapshotAsync(storage, path, options.Recursive, cancellationToken).ConfigureAwait(false) ?? [];
        using var timer = new PeriodicTimer(options.PollInterval);
        var polls = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            polls++;
            var full = !options.Incremental || !options.Recursive || polls % options.FullRescanEvery == 0;
            var current = full
                ? await SnapshotAsync(storage, path, options.Recursive, cancellationToken).ConfigureAwait(false)
                : await IncrementalSnapshotAsync(storage, path, previous, cancellationToken).ConfigureAwait(false);
            if (current is null) continue;
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, item) in current)
            {
                if (!previous.TryGetValue(key, out var before))
                    yield return new StorageChange(StorageChangeKind.Created, key, null, item.ItemType, now);
                else if (item.ItemType == StorageItemType.File && (item.Size != before.Size || item.LastModified != before.LastModified || item.ETag != before.ETag))
                    yield return new StorageChange(StorageChangeKind.Changed, key, null, item.ItemType, now);
            }
            foreach (var (key, item) in previous)
            {
                if (!current.ContainsKey(key))
                    yield return new StorageChange(StorageChangeKind.Deleted, key, null, item.ItemType, now);
            }
            previous = current;
        }
    }

    private static async Task<Dictionary<string, StorageItem>?> SnapshotAsync(IStorageService storage, string path, bool recursive, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
        await foreach (var item in storage.EnumerateItemsAsync(path, new StorageListOptions { Recursive = recursive }, cancellationToken).ConfigureAwait(false))
        {
            // A failed listing (server busy, directory gone) is retried next interval rather than reported as mass deletes.
            if (item.IsFailure) return item.Error!.Code == StorageErrors.NotFoundCode ? items : null;
            items[item.Value!.Path] = item.Value;
        }
        return items;
    }

    /// <summary>
    /// Refreshes a snapshot by listing only folders whose modification time changed (and the root), reusing the
    /// previous snapshot for the rest. Returns null when a listing fails, so the poll is retried.
    /// </summary>
    internal static async Task<Dictionary<string, StorageItem>?> IncrementalSnapshotAsync(
        IStorageService storage,
        string root,
        IReadOnlyDictionary<string, StorageItem> previous,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = StoragePath.Normalize(root);
        if (normalizedRoot.IsFailure) return null;
        var current = new Dictionary<string, StorageItem>(previous, StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(normalizedRoot.Value!);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            var children = new Dictionary<string, StorageItem>(StringComparer.Ordinal);
            await foreach (var item in storage.EnumerateItemsAsync(directory, new StorageListOptions(), cancellationToken).ConfigureAwait(false))
            {
                if (item.IsFailure)
                {
                    if (item.Error!.Code == StorageErrors.NotFoundCode) break;
                    return null;
                }
                children[item.Value!.Path] = item.Value;
            }
            // Replace this folder's direct children; a vanished child folder takes its whole subtree along.
            foreach (var stale in current.Keys.Where(key => ParentOf(key) == directory && !children.ContainsKey(key)).ToList())
            {
                current.Remove(stale);
                foreach (var nested in current.Keys.Where(key => key.StartsWith(stale + "/", StringComparison.Ordinal)).ToList())
                    current.Remove(nested);
            }
            foreach (var (childPath, child) in children)
            {
                current[childPath] = child;
                if (child.ItemType != StorageItemType.Directory) continue;
                var before = previous.GetValueOrDefault(childPath);
                // Descend where entries may have changed, or where the provider has no folder times at all.
                if (before is null || child.LastModified is null || before.LastModified != child.LastModified)
                    pending.Enqueue(childPath);
            }
        }
        return current;
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>Adapts <see cref="FileSystemWatcher"/> events to storage changes.</summary>
    internal static async IAsyncEnumerable<StorageChange> WatchFileSystemAsync(
        string fullPath,
        bool recursive,
        Func<string, string?> toStoragePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<StorageChange>(new UnboundedChannelOptions { SingleReader = true });
        using var watcher = new FileSystemWatcher(fullPath)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024
        };
        void Post(StorageChangeKind kind, string path, string? oldPath)
        {
            if (toStoragePath(path) is not { } storagePath || Providers.StorageListFilter.IsInternal(storagePath)) return;
            var old = oldPath is null ? null : toStoragePath(oldPath);
            StorageItemType? type = Directory.Exists(path) ? StorageItemType.Directory : File.Exists(path) ? StorageItemType.File : null;
            channel.Writer.TryWrite(new StorageChange(kind, storagePath, old, type, DateTimeOffset.UtcNow));
        }
        watcher.Created += (_, e) => Post(StorageChangeKind.Created, e.FullPath, null);
        watcher.Changed += (_, e) => Post(StorageChangeKind.Changed, e.FullPath, null);
        watcher.Deleted += (_, e) => Post(StorageChangeKind.Deleted, e.FullPath, null);
        watcher.Renamed += (_, e) => Post(StorageChangeKind.Renamed, e.FullPath, e.OldFullPath);
        // A full buffer drops notifications silently unless the error is handled; report it so the caller rescans.
        watcher.Error += (_, _) =>
            channel.Writer.TryWrite(new StorageChange(StorageChangeKind.Overflow, toStoragePath(fullPath) ?? string.Empty, null, StorageItemType.Directory, DateTimeOffset.UtcNow));
        watcher.EnableRaisingEvents = true;
        await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return change;
    }
}
