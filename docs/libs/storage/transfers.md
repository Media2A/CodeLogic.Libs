# Transfers & Sync

> Moving data safely: copies and moves between any two connections, guaranteed single-file transfers,
> streamed writes, what happens when the destination already exists, resume and append, progress and
> speed limits, a durable background queue, compare and three-way sync, watching for changes, and links.

The queue and sync types live in `CL.Storage.Queue` and `CL.Storage.Sync`.

## Safe copies and moves

The library copies or moves files and complete directory trees between any two mounted connections:

```csharp
StorageTransferReport copied = await storage.CopyAsync(
    "primary", "exports/2026",
    "archive", "yearly/2026",
    new StorageTransferOptions { MetadataPreservation = StorageMetadataPreservation.BestEffort });

StorageTransferReport moved = await storage.MoveAsync(
    "incoming", "ready/item.bin",
    "processed", "item.bin");
```

`CopyAsync` and `MoveAsync` return a `StorageTransferReport`; `IsSuccess`, `Error`, and `ToResult()` work
as on a `Result`. The report says what happened:

- `Completed`;
- `Skipped`, with a `SkipReason`;
- `Failed`, when nothing was committed;
- `NeedsReconciliation`, when the transfer left a mixed state;
- `Cancelled`, when the caller cancelled before anything was committed.

For a single file it also gives:

- `WrittenPath`, the path actually written (it differs after `Rename`);
- the number of bytes and, when verified, the SHA-256;
- the destination's new `DestinationETag` and `DestinationVersionId`.

When a transfer does not finish, these fields say exactly what it left behind: `DestinationCommitted`,
`SourceDeleted`, `StagingLeftBehind`, `BackupRestored`, `BackupLeftBehind`, and a `ResumeToken`.

The transfer coordinator:

1. leases both active connections;
2. validates normalized path relationships (equal paths and a destination inside its source are rejected);
3. stages each destination file under an internal name;
4. relays cross-provider data through a pipe with 1 MiB maximum read-ahead;
5. backs up overwritten files and rolls the destination tree back on failure;
6. deletes a move source only after every destination file commits.

Folder renames on the same FTP, SFTP, WebDAV, or local connection use a single server-side rename
(`AtomicMove`) instead of copying the tree. Safe same-provider file copies stay server-side when the
provider can guarantee them.

Local folders transfer without registering a connection:

```csharp
Result<StorageDirectoryTransferReport> upload = await storage.UploadDirectoryAsync(
    @"C:\exports\2026", "archive", "yearly/2026");

Result<StorageDirectoryTransferReport> download = await storage.DownloadDirectoryAsync(
    "archive", "yearly/2026", @"C:\restore\2026");
```

Reports contain file, directory, byte, and skipped-file counts.

## Guaranteed transfers

A single-file transfer can be made to promise exactly what was planned:

```csharp
var report = await storage.CopyAsync("sftp", "in/report.pdf", "s3", "archive/report.pdf", new StorageTransferOptions
{
    DestinationCondition = new StorageMutationCondition { ExpectedETag = seenDestination.ETag }, // replace only this version
    ExpectedSourceETag = plannedSource.ETag,        // or SourceVersionId, to read an exact version
    ExpectedSourceLength = plannedSource.Size,      // shorter or longer fails without committing
    Verify = true,                                  // SHA-256 during the copy, then the destination confirmed
    ExpectedSha256 = knownDigest                    // optional
});
```

- **Staging.** Content always goes to a staging object beside the destination first, and its length
  and digest are checked there.
- **Verification.** A verified copy is confirmed on the destination before it is promoted: by the
  server's SHA-256 where the server keeps one, otherwise by reading it back. `VerifiedBy` says which.
  After promotion the destination is confirmed again by its length and, where the server keeps one,
  its SHA-256.
- **Destination condition.** `DestinationCondition` is checked before the copy starts and again just
  before promotion. `ConditionEnforcement` reports how it was enforced:
  - `Atomic` when the provider enforces it in the same operation. This is the create-new case:
    `Overwrite = false` on a connection with `ConditionalCreate`. On S3 the promote completes a
    part-by-part copy with `If-None-Match`, which S3 and MinIO enforce.
  - `CheckedBeforeCommit` otherwise.

  When the condition fails at promotion, the destination is left exactly as the other writer left it.
- **Pinned version.** `SourceVersionId` reads that version, and its length, ETag, and a move's source
  deletion refer to it. Moving an older version does not delete the source, because the current object
  is another version (`NeedsReconciliation`).
- **Pinned source.** A source pinned by ETag is read again after streaming. If it changed in between,
  the transfer fails with `storage.conflict` and nothing is committed.
- **Moves.** A move deletes its source only while it is still the version that was copied, using a
  conditional delete where the provider has one. Otherwise the report is `NeedsReconciliation` with
  `SourceDeleted = false`.
- **Local files** carry an ETag (last-write time and size), so conditions work on them too, checked right
  before the file is replaced.
- **Cancellation** is reported, not thrown: the report's `Outcome` is `Cancelled` (error
  `storage.cancelled`), staging and backup objects are removed, and a resumable transfer's `ResumeToken`
  continues it.

`StorageUploadOptions` has the matching `ExpectedLength`, `Verify`, and `ExpectedSha256` for uploads.

## Streamed writes

`OpenWriteAsync` returns a stream to write a file's content into, for push-style producers:

```csharp
var opened = await files.OpenWriteAsync("exports/data.csv", new StorageUploadOptions { Verify = true });
await using var writer = opened.Value!;
await writer.WriteAsync(chunk);
Result<StorageItem> committed = await writer.CommitAsync(); // or AbortAsync(); disposing without commit aborts
```

The content is staged and checked like any other upload, and the destination appears only on commit.
With `Verify`, the committed item carries the content's `Sha256`, as verified uploads do. Disposing
without committing aborts without waiting; `DisposeAsync` and `AbortAsync` wait until the staging object
is gone.

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
| `Resume` | continue a staged copy (see below) |

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

`ConflictPolicy = Resume` writes through a resumable staging object (`.cl-storage-part-…`). The
destination is replaced only once that object is complete, so it is never half-written.

- **Continuing after a failure.** The staged bytes stay when an attempt fails. The next attempt reads
  only the missing tail and appends it (FTP `APPE`, SFTP append, local files), provided it has the same
  destination and the same source.
- **The same source only.** Staged bytes are keyed by the source's identity, so a different source of the
  same length never continues them. Uploads need a seekable stream and `SourceIdentity` (a stable name for
  the content) or `SourceLastModified`; `UploadFileAsync` sets both. Copies and moves identify the source
  by its ETag, time, or version, and do not resume a source that has none of them.
- **Across restarts.** A failed or cancelled copy's report carries a `ResumeToken`. Store it and pass it
  back in `StorageTransferOptions.ResumeToken`; this works from another process after a restart too. A
  token is followed only for exactly the same source.
- **Integrity.** With `Verify`, the part staged earlier is read again from the source and must match
  before the rest is appended, so the digest covers the whole file.
- **Already complete.** A destination counts as complete only when both sides report the same digest; an
  equal size is not enough. A resumed move whose destination is complete deletes its source.
- **Object stores** cannot append, so the staging object is rewritten from the start.

```csharp
await files.UploadFileAsync("big/image.iso", @"D:\image.iso",
    new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume });

await files.DownloadToFileAsync("big/image.iso", @"D:\image.iso", conflictPolicy: StorageConflictPolicy.Resume);

await using var line = new MemoryStream("entry\n"u8.ToArray());
await files.AppendAsync("logs/today.log", line);

await files.CleanupStaleStagingAsync("", TimeSpan.FromDays(1)); // leftovers of crashed transfers
```

`DownloadToFileAsync` resumes a partial local file with a ranged download. `AppendAsync` appends to a
file directly, which needs `StorageFeature.Append`.

## Progress and speed limits

Upload, download, and transfer options take a `Progress` sink. Reports arrive at most every 250 ms
and carry:

- `BytesTransferred`, `TotalBytes`, `BytesPerSecond`, and `EstimatedRemaining`;
- for directory transfers, the `ItemPath` of the current file plus `FilesCompleted`/`FilesTotal`.

Directory transfers accumulate bytes across files. Set `StorageTransferOptions.PreScan` to list a
directory first, so its reports carry totals and a time estimate. A server-side copy or move reports
its start and its end.

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
connections together. Limits are enforced inside each provider's upload and download, so they apply to
every path: `UploadAsync` and `DownloadAsync` streams used directly, the file helpers, and relayed
transfers. Download progress carries `TotalBytes` even without a `Length`: when the stream cannot
report it, the item's size is looked up once.

## Transfer queue

`OpenTransferQueueAsync` runs transfers in the background, like FileZilla's queue:

```csharp
using CL.Storage.Queue;

var opened = await storage.OpenTransferQueueAsync(new StorageTransferQueueOptions
{
    MaxConcurrentTransfers = 4,
    MaxTransfersPerConnection = 2,
    AutomaticRetries = 3,
    Store = myDurableStore          // optional: jobs survive restarts
});
await using var queue = opened.Value!;
queue.ProgressChanged += job => Console.WriteLine($"{job.Destination}: {job.Progress?.BytesTransferred:N0} B");

await queue.EnqueueUploadDirectoryAsync(@"C:\exports", "sftp", "incoming");
await queue.EnqueueCopyAsync("s3", "reports/q3.pdf", "sftp", "outbox/q3.pdf",
    new StorageTransferOptions { Verify = true, ConflictPolicy = StorageConflictPolicy.Resume },
    priority: 10, jobId: "q3-report");   // same id + same work = same job

await queue.WaitForIdleAsync();
```

**Jobs are data.** A `StorageTransferJobSpec` describes the kind, both endpoints, and every option.
`ToJson`/`FromJson` store it. Caller-chosen ids make enqueueing idempotent:

- the same id with the same work returns the existing job;
- the same id with different work fails with `storage.conflict`.

**A durable, shared store.** `IStorageTransferJobStore` holds every job; it is in memory by default.

- Every record has a `Revision`, and saves are compare-and-swap on it: a stale copy never overwrites a
  newer state, so a job finished in another process cannot be re-queued here.
- A worker claims a job at the revision it read, with a lease that carries a fencing token, and renews the
  lease while it runs. The store owns the lease fields.
- A save with a stale lease is refused, so a worker that lost its lease never records an outcome.
- As a result, two processes never run the same job.
- A failing store does not wedge the queue: a failed claim is tried again a moment later, renewals are
  retried while the lease holds, and a job whose outcome could not be recorded is recovered later.

**Restarts.** A transfer records its phase as it goes. When a new process finds a job that was running
in an earlier one:

- if the destination was never touched, the job goes straight back to the queue, and a resumable
  transfer continues from its staged bytes;
- otherwise the job becomes `Interrupted`, for a person to decide.

A process restarted with the same `WorkerId` takes back its own leases at once, so give every running
queue its own `WorkerId`. A transfer that succeeded is recorded `Completed` even if it was being paused,
cancelled, or shut down as it finished.

**States.**

| State | Meaning |
|---|---|
| `Queued`, `Running`, `Paused` | waiting, in progress, or held |
| `Completed`, `Failed`, `Cancelled` | finished |
| `Blocked` | needs a person: `BlockReason` is `Trust` (an untrusted host key or certificate) or `Credential` |
| `NeedsReconciliation` | left a mixed state; the job's `LastReport` says which |
| `Interrupted` | found running after a restart, with the destination already touched |

Only transient failures are retried, with exponential backoff and jitter that honours a server's
`Retry-After`.

**Control.**

- Pause or resume the whole queue with `Pause`/`Resume`, or one job with `PauseJobAsync`/`ResumeJobAsync`.
  A running resumable transfer keeps its staged data while paused.
- `CancelAsync`, `RetryAsync`, and `RemoveAsync` act on one job. `RetryAsync` also resets `Attempts`, so
  backoff starts from the base delay again.
- Priorities are integers, higher first. Change a waiting job's with `SetPriorityAsync`, or reorder with
  `MoveUpAsync`/`MoveDownAsync`.

**History and events.**

- `MaxFinishedJobs` caps history, and `ClearAsync(states)` clears jobs by state.
- Progress events are throttled by `ProgressInterval`.
- `EventContext` raises `JobChanged`, `ProgressChanged`, and `JobRemoved` on a UI thread.
- The event bus receives started, completed, failed, cancelled, retrying, blocked, and
  needs-reconciliation events.

**Adaptive concurrency.** With `AdaptiveConcurrency`, the queue starts with one transfer. It adds one
after each success and halves after a transient failure, up to `MaxConcurrentTransfers`.

`EnqueueDownloadAsync` takes `StorageDownloadOptions`, so a job can fetch an exact version or a range.

## Compare and sync

```csharp
using CL.Storage.Sync;

var diff = await storage.CompareAsync("sftp", "site", "s3", "backup/site");
foreach (var entry in diff.Value!.Entries.Where(e => e.Kind != StorageDiffKind.Same))
    Console.WriteLine($"{entry.Kind,-18} {entry.Reasons,-12} {entry.RelativePath}");

var options = new StorageSyncOptions
{
    Direction = StorageSyncDirection.TwoWay,
    StateStore = baselines, SyncId = "site",          // a baseline makes two-way three-way
    ConflictPolicy = StorageSyncConflictPolicy.Block,
    MaxDeletes = 100, MaxDeletePercent = 10,
    Verify = true,
    Compare = new StorageCompareOptions { Exclude = ["**/*.tmp", "cache/**"] }
};
var plan = (await storage.PlanSyncAsync("sftp", "site", "s3", "backup/site", options)).Value!;
// show plan.Actions, plan.Conflicts, and plan.Warnings; store plan.ToJson(); approve plan.Digest
var report = await storage.ApplySyncAsync("sftp", "site", "s3", "backup/site", plan, approvedDigest, options);
```

`CompareAsync` and the sync methods also work between any two `IStorageService` instances, such as a
`LocalStorageBackend` over a local folder. `SyncAsync` plans and applies in one call.

### Directions

| Direction | Behavior |
|---|---|
| `Update` (default) | copy new and changed files; never delete; never replace a newer destination with an older source |
| `Mirror` | make the destination match the source; delete extra items with `DeleteExtraneous`; leave a newer destination alone |
| `TwoWay` | change both sides, folders included (see below) |

With a baseline (`StateStore` + `SyncId`), `TwoWay` is a three-way sync. An edit or deletion on one side
is carried to the other (`PropagateDeletes`). Changes on both sides are conflicts: `BothModified`,
`BothCreated`, or `DeleteVersusModify`. A folder removed on one side is removed on the other only once
everything inside it goes too. Without a baseline, missing files and folders are copied and files that
differ are conflicts.

- A path that is a file on one side and a folder on the other is left alone, with everything below it.
- Where one side ignores case, a name spelled differently on each side (`Readme.TXT` / `readme.txt`) is
  one item, and each side keeps its own spelling.
- The baseline saved after a run records the versions both sides agreed on when the plan was made and the
  versions the run itself wrote, never a listing taken afterwards. A file edited during or just after a run
  is still seen as changed next time, and a step that did not complete is tried again. Neither side having
  changed while their content differs is a conflict, not "in sync".

### Conflict policies

| Policy | Behavior |
|---|---|
| `Block` (default) | plan the conflict; the plan cannot be applied until it is resolved, unless `ApplyWithConflicts` is set |
| `KeepBoth` | the source's version keeps the name; the destination's version is kept on both sides as `name (conflict xxxxxxxx).ext` (`… 2`, `… 3` when that name is taken) |
| `NewerWins` | the newer side wins; a modification beats a deletion |

### Plan, then apply

- The plan lists every step with the versions it depends on. It serializes with `ToJson()`, and
  `Digest` is a SHA-256 over its content.
- `ApplySyncAsync` refuses a plan that is not the one approved.
- It also refuses a plan whose baseline moved on because another run saved in between.
- Each step re-checks its items first. A step whose item changed since planning is reported `Stale`
  and not taken.
- `DryRun` returns the plan without changing anything.

### Deletion safety

Deletions are withheld, and listed in `plan.Warnings` and `report.Withheld`, when:

- they would exceed `MaxDeletes` or `MaxDeletePercent`;
- a side is unexpectedly empty, as when a drive is not mounted or a root is wrong (unless
  `AllowEmptySide` is set);
- any other step failed in the same run.

A listing that fails part-way fails the plan. Folders are never deleted recursively: their files are
deleted one by one, each checked at apply time, and then the emptied folders. A folder that gained items
after the plan, or holds items the filters left out, is kept.

### Copies and comparison

- **Copies.** Each copy is a staged write that is conditional on the planned versions (create-new, or
  replace only that version), pinned to the planned source, and optionally verified (`Verify`).
- **Timestamps.** Copied files keep the source's modification time. On object stores that cannot set
  times, the time is kept in `cl-mtime` metadata and used by later comparisons.
- **What is compared.** Size and time, by default, with a two-second tolerance. `CompareBy.Checksum`
  uses the server's digest where there is one.
- **Hashing.** Without a server digest, files are hashed in parallel (`HashConcurrency`) within
  `MaxHashedFiles`/`MaxHashedBytes`. Beyond that budget a difference is `Undecidable`.
- **Filters.** `Include`/`Exclude` globs, where `**` spans folders, apply to both sides and to the
  baseline.
- **Case.** Names that differ only by case are refused on a case-insensitive side
  (`CaseInsensitivePaths`, or `CaseInsensitive`), instead of letting the last copy win.
- **Links.** `LinkHandling` skips links by default.
- **Size cap.** `MaxItems` caps the size of a tree.

### Runs

Transient failures are retried per step (`ItemRetries`). `ContinueOnError = false` stops at the first
failure. A cancelled run still returns its report, with `Cancelled` set. Read each step's outcome from
`report.Results`.

## Watching for changes

```csharp
await foreach (var change in files.WatchAsync("incoming", cancellationToken: stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

Local connections use native file-system notifications, including renames; this also works for
connections from `GetStorage()`. If notifications arrive faster than they can be buffered, a
`StorageChangeKind.Overflow` change for the watched directory is reported, and the caller should list
it again. If native watching stops for good (the folder was removed, a network share dropped), `Overflow`
is reported and watching continues by polling.

Every other provider is polled. The directory is listed every `StorageWatchOptions.PollInterval`
(30 s by default) and compared by type, size, time, and ETag, so a rename appears as a delete plus a
create. A failed poll is retried on the next interval rather than reported as deletions. The library's
own staging items never appear.

Polling a large remote tree is cheaper with `Incremental = true`:

- After the first listing, a poll lists only the root and the folders whose modification time changed
  (entries were added, removed, or renamed in them). The previous listing is reused for the rest.
- Every `FullRescanEvery` polls (10 by default), the whole tree is listed again. This catches edits to
  existing files, and changes deep inside folders whose own time did not change.
- Where folders have no times (object stores), listing folder by folder would cost more than one
  recursive listing, so every poll lists everything there.

```csharp
var options = new StorageWatchOptions { Recursive = true, Incremental = true, FullRescanEvery = 20 };
await foreach (var change in partner.WatchAsync("outbox", options, stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

## Links in transfers

Relayed copies and moves (across connections, or directory copies) meet links as provider-specific
items. `StorageTransferOptions.LinkHandling` decides what happens:

| Mode | Behavior |
|---|---|
| `Reject` (default) | fail with `storage.unsupported` and roll back |
| `Skip` | leave links out |
| `Follow` | copy the target file's content; links to directories are refused, so loops cannot occur |
| `Recreate` | create an equivalent link; targets inside the copied tree point into the copy |

`Recreate` needs `ReadLinks` on the source and `CreateLinks` on the destination. SFTP cannot be a
`Recreate` source, because SSH.NET cannot read link targets.
