# Transfers & Sync

> Moving data safely: copies and moves between any two connections, what happens when the
> destination already exists, resume and append, progress and speed limits, a background queue,
> compare and sync, watching for changes, and links.

The queue and sync types live in `CL.Storage.Queue` and `CL.Storage.Sync`.

## Safe copies and moves

The library copies or moves files and complete directory trees between any two mounted connections:

```csharp
Result copied = await storage.CopyAsync(
    "primary", "exports/2026",
    "archive", "yearly/2026",
    new StorageTransferOptions { MetadataPreservation = StorageMetadataPreservation.BestEffort });

Result moved = await storage.MoveAsync(
    "incoming", "ready/item.bin",
    "processed", "item.bin");
```

The transfer coordinator:

1. leases both active connections;
2. validates normalized path relationships (equal paths and a destination inside its source are rejected);
3. stages each destination file under a unique internal name;
4. relays cross-provider data through a pipe with 1 MiB maximum read-ahead;
5. backs up overwritten files and rolls the destination tree back on failure;
6. deletes a move source only after every destination file commits.

Folder renames on the same FTP, SFTP, WebDAV, or local connection use a single server-side rename
(`AtomicMove`) instead of copying the tree. Safe same-provider file copies stay server-side when the
provider can guarantee them. An incomplete restore, staging cleanup, or post-copy source deletion is
`storage.partial_failure`.

Local folders transfer without registering a connection:

```csharp
Result<StorageDirectoryTransferReport> upload = await storage.UploadDirectoryAsync(
    @"C:\exports\2026", "archive", "yearly/2026");

Result<StorageDirectoryTransferReport> download = await storage.DownloadDirectoryAsync(
    "archive", "yearly/2026", @"C:\restore\2026");
```

Reports contain file, directory, byte, and skipped-file counts.

## When the destination already exists

`ConflictPolicy` on `StorageUploadOptions` and `StorageTransferOptions` mirrors FileZilla's
"target file already exists" choices:

| Policy | Behavior |
|---|---|
| `Fail` | return `storage.conflict` |
| `Overwrite` | replace it |
| `Skip` | keep it; the call succeeds and returns the existing item |
| `OverwriteIfNewer` | replace only when the source is newer (2 s clock slack) |
| `OverwriteIfSizeDiffers` | replace only when the sizes differ |
| `OverwriteIfNewerOrSizeDiffers` | either of the above |
| `Rename` | write `name (1).ext`, `name (2).ext`, … |
| `Resume` | append what the destination is missing (see below) |

When `ConflictPolicy` is not set, the `Overwrite` flag decides as before.

```csharp
await files.UploadFileAsync("backup/db.bak", @"C:\dumps\db.bak",
    new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewer });

var report = await storage.UploadDirectoryAsync(@"C:\site", "web", "public",
    new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewerOrSizeDiffers });
Console.WriteLine($"{report.Value!.Files} uploaded, {report.Value.SkippedFiles} unchanged");
```

- Directories are decided file by file.
- Moving a directory deletes only the source files that were transferred; skipped files stay.
- `UploadFileAsync` supplies the local file's time; for stream uploads set `SourceLastModified`.
  Unknown times or sizes count as newer or different.
- `DownloadToFileAsync` takes a `conflictPolicy` for the local file.

## Resume and append

`ConflictPolicy = Resume` continues an interrupted upload by appending only what the destination is
missing (FTP `APPE`, SFTP append mode, local files); a complete destination is left alone. The source
must be seekable, and resumed bytes are written in place rather than staged, so verify a checksum
afterwards when integrity matters.

```csharp
await files.UploadFileAsync("big/image.iso", @"D:\image.iso",
    new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume });

await files.DownloadToFileAsync("big/image.iso", @"D:\image.iso", conflictPolicy: StorageConflictPolicy.Resume);

await using var line = new MemoryStream("entry\n"u8.ToArray());
await files.AppendAsync("logs/today.log", line);

await files.CleanupStaleStagingAsync("", TimeSpan.FromDays(1)); // leftovers of crashed transfers
```

Check `StorageFeature.ResumableUpload` and `StorageFeature.Append` first; object stores return
`storage.unsupported`.

## Progress and speed limits

Upload, download, and transfer options take a `Progress` sink. Reports arrive at most every 250 ms
and carry `BytesTransferred`, `TotalBytes`, `BytesPerSecond`, `EstimatedRemaining`, and, for directory
transfers, the `ItemPath` of the current file.

```csharp
var progress = new Progress<StorageTransferProgress>(p =>
    Console.WriteLine($"{p.ItemPath}: {p.BytesTransferred:N0} B at {p.BytesPerSecond / 1024:N0} KiB/s"));

await storage.CopyAsync("sftp", "exports", "s3", "archive", new StorageTransferOptions { Progress = progress });
```

Speed limits are set per connection and shared by all of its concurrent transfers:

```json
"TransferLimits": { "MaxUploadBytesPerSecond": 1048576, "MaxDownloadBytesPerSecond": 5242880 }
```

`MaxTotalUploadBytesPerSecond` and `MaxTotalDownloadBytesPerSecond` in the `storage` section cap all
connections together. Limits also apply to relayed transfers between connections.

## Transfer queue

`CreateTransferQueue` runs transfers in the background, like FileZilla's queue:

```csharp
using CL.Storage.Queue;

await using var queue = storage.CreateTransferQueue(new StorageTransferQueueOptions
{
    MaxConcurrentTransfers = 4,
    MaxTransfersPerConnection = 2,
    AutomaticRetries = 2
});

queue.ProgressChanged += job => Console.WriteLine($"{job.Destination}: {job.Progress?.BytesTransferred:N0} B");
queue.EnqueueUploadDirectory(@"C:\exports", "sftp", "incoming");
queue.EnqueueCopy("s3", "reports/q3.pdf", "sftp", "outbox/q3.pdf", priority: StorageTransferPriority.High);

await queue.WaitForIdleAsync();
foreach (var failed in queue.FailedJobs)
    Console.WriteLine($"{failed.Source}: {failed.Error?.Code}");
queue.RetryFailed();
```

Jobs cover copies, moves, and file and directory uploads and downloads. The queue respects a global
and a per-connection limit, starts `High` priority jobs first, supports `Pause`/`Resume`/`Cancel`, and
re-queues transient failures automatically before moving a job to `FailedJobs`. `JobChanged` and
`ProgressChanged` suit a UI; the event bus receives `StorageTransferStartedEvent`,
`StorageTransferCompletedEvent`, and `StorageTransferFailedEvent`. Jobs live in memory only.

## Compare and sync

```csharp
using CL.Storage.Sync;

var diff = await storage.CompareAsync("sftp", "site", "s3", "backup/site");
foreach (var entry in diff.Value!.Entries.Where(e => e.Kind != StorageDiffKind.Same))
    Console.WriteLine($"{entry.Kind,-18} {entry.Reasons,-12} {entry.RelativePath}");

var report = await storage.SyncAsync("sftp", "site", "s3", "backup/site", new StorageSyncOptions
{
    Direction = StorageSyncDirection.Mirror,
    DeleteExtraneous = true,
    DryRun = true
});
```

`CompareAsync` and `SyncAsync` also work between any two `IStorageService` instances, such as a
`LocalStorageBackend` over a local folder. Comparison uses size and modification time by default
(two-second tolerance) and can add checksums through `StorageCompareOptions.CompareBy`.

| Direction | Behavior |
|---|---|
| `Update` (default) | copy new and changed files; never delete; never replace a newer destination of the same size |
| `Mirror` | make the destination match the source; delete extra items with `DeleteExtraneous` |
| `TwoWay` | copy each file toward the side where it is missing or older; no deletes |

Copied files keep the source's modification time where the destination supports it
(`PreserveTimestamps`, on by default). On services that cannot (S3, Azure, GCS, Swift) a copy is newer
than its source, and "changed" means "source newer", so repeated syncs stay no-ops. Per-file failures
are collected in `Failed`. `DryRun` returns the plan in `Actions` without changing anything.

## Watching for changes

```csharp
await foreach (var change in files.WatchAsync("incoming", cancellationToken: stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

Local connections use native file-system notifications, including renames. Every other provider is
polled: the directory is listed every `StorageWatchOptions.PollInterval` (30 s by default) and compared
by type, size, time, and ETag, so a rename appears as a delete plus a create. A failed poll is retried
on the next interval rather than reported as deletions. The library's own staging items never appear.

## Links in transfers

Relayed copies and moves (across connections, or directory copies) meet links as provider-specific
items. `StorageTransferOptions.LinkHandling` decides what happens:

| Mode | Behavior |
|---|---|
| `Reject` (default) | fail with `storage.unsupported` and roll back |
| `Skip` | leave links out |
| `Follow` | copy the target file's content; links to directories are refused, so loops cannot occur |
| `Recreate` | create an equivalent link; targets inside the copied tree point into the copy |

`Recreate` needs `ReadLinks` on the source and `CreateLinks` on the destination; SFTP cannot be a
`Recreate` source because SSH.NET cannot read link targets.
