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

`CopyAsync` and `MoveAsync` never throw for a transfer that could not run: a token cancelled before the
call, an unknown connection id (`storage.not_found`), or a provider that throws all come back as a
report.

For a single file it also gives:

- `WrittenPath`, the path actually written (it differs after `Rename`);
- the number of bytes and, when verified, the SHA-256;
- the destination's new `DestinationETag` and `DestinationVersionId`.

When a transfer does not finish, these fields say exactly what it left behind: `DestinationCommitted`,
`SourceDeleted`, `StagingLeftBehind`, `BackupRestored`, `BackupLeftBehind`, and a `ResumeToken`.
`SourceDeleted` is exact: it is `true` only when the whole source of a move is gone, so a directory move
that kept skipped files reports `Completed` with `SourceDeleted = false`.

Once a destination is committed it is never rolled back; what goes wrong afterwards is reported instead:

- a backup or staging object that the library, or the provider's own replace, could not remove makes the
  transfer `Completed` with `BackupLeftBehind` or `StagingLeftBehind` set;
- a destination that does not hold the committed length (or, when verified, digest) is
  `NeedsReconciliation`, even without `Verify`, because someone else may have written it.

The transfer coordinator:

1. leases both active connections;
2. validates normalized path relationships (equal paths and a destination inside its source are rejected);
3. stages each destination file under an internal name;
4. relays cross-provider data through a pipe with 1 MiB maximum read-ahead;
5. backs up overwritten files (on FTP and SFTP the provider's own replace renames the old file aside
   instead, so nothing is downloaded and uploaded again) and, when a later file fails, rolls back only
   the files that still hold what this transfer wrote;
6. deletes a move source only after every destination file commits, file by file and only while each
   file is still the version it listed, then removes the emptied folders without recursion.

A file added to, or changed in, the source of a directory move while it runs stays where it is, and the
move is `NeedsReconciliation` (details `sourceChanged=N;sourceAdded=N`).

Folder renames on the same FTP, SFTP, or local connection use a single server-side rename
(`AtomicMove`) instead of copying the tree, but only when the destination does not exist; onto an
existing folder the transfer relays and merges, on every provider. A native rename moves links as they
are: `LinkHandling` applies to relayed transfers. WebDAV folder moves always relay, because a WebDAV
`MOVE` can fail half-way (a `207 Multi-Status` answer is reported as `storage.partial_failure`).

A native file move on an object store (S3, Azure, Google Cloud, Swift) is a copy pinned to the version
the library read, then a delete of that version only. Safe same-provider file copies stay server-side
when the provider can guarantee them; `MetadataPreservation = Discard` always relays.

A relay within one FTP connection holds two sessions at once (one reading, one writing), so it needs
`Session.MaxSessions` of at least 2. With 1 it fails at once with `storage.unsupported`
(`requiredSessions=2;maxSessions=1`) instead of waiting for a session that never frees up. SFTP copies
within one connection use a single session.

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
  before promotion, and then handed to the provider's move, which enforces it in the same request where
  it can. `ConditionEnforcement` reports how it was enforced:
  - `Atomic` when the provider enforced it in the committing request: create-new (`Overwrite = false`)
    on Local, WebDAV, Azure, Google Cloud, and S3 servers that enforce `If-None-Match` on `CopyObject`; replace-only-this-version on Azure, Google Cloud, and S3 servers that enforce
    `If-Match` on `CopyObject`.
  - `CheckedBeforeCommit` otherwise: FTP and SFTP (their protocols have no conditional rename), Swift
    (its server-side copy takes no destination condition), a version condition on Local and WebDAV, and
    S3-compatible servers that ignore those headers. AWS S3
    enforces them; MinIO does not on `CopyObject` (see `ConditionalRequests` in
    [Connections](connections.md#cloud-emulators-and-compatible-services)).

  When the condition fails at promotion, the destination is left exactly as the other writer left it; a
  destination deleted by someone else meanwhile is not brought back.
- **Pinned version.** `SourceVersionId` reads that version, and its length, ETag, and a move's source
  deletion refer to it. Moving an older version does not delete the source, because the current object
  is another version (`NeedsReconciliation`).
- **Pinned source.** A source pinned by ETag is read again after streaming. If it changed in between,
  the transfer fails with `storage.conflict` and nothing is committed.
- **Moves.** A move deletes its source only while it is still the version that was copied, using a
  conditional delete where the provider has one. Otherwise the report is `NeedsReconciliation` with
  `SourceDeleted = false`. A move that committed but kept its source still publishes the copy event, so
  watchers and caches see the new file.
- **Local files** carry a weak ETag (`W/"…"`) made of the last-write time, creation time, and size, so
  conditions work on them too, checked right before the file is replaced. It is only as fine as the file
  system's clock: on FAT (2 s), exFAT, HFS+ (1 s), some SMB and NFS shares, or after a tool restores file
  times, two versions can share it. It is not proof that the content is unchanged.
- **Cancellation** is reported, not thrown: the report's `Outcome` is `Cancelled` (error
  `storage.cancelled`), staging and backup objects are removed, and a resumable transfer's `ResumeToken`
  continues it. A cancel that arrives after the destination was committed does not make the transfer
  `Cancelled`: the report says what was committed. `IStorageService.CopyAsync`/`MoveAsync` on a
  connection throw `OperationCanceledException`, like every other connection call.

`StorageUploadOptions` has the matching `ExpectedLength`, `Verify`, and `ExpectedSha256` for uploads.
`ExpectedSha256` implies `Verify`, before and after the commit. An upload's `Condition` on a connection
that cannot enforce it in its own upload is checked right before the staged file replaces the
destination; an upload returns the stored item, not how the condition was enforced.

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

A write that fails or is cancelled aborts the stream: its bytes may already be buffered, so writing them
again would duplicate them. Start a new stream instead. When the destination stopped accepting data, the
write throws `StorageWriteException` (an `IOException`) whose `Error` says why.

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
| `Rename` | write the first free name: `name (1).ext`, `name (2).ext`, … (a folder counts as taken; `name (1).ext` goes on to `name (2).ext`) |
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
- Moving a single link with `LinkHandling = Skip` skips it (`SkipReason = Link`) and leaves it in place.
- With a pinned `SourceVersionId`, "newer" and "size differs" are judged by that version, not the latest.
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
  same length never continues them. Uploads need a seekable stream and `SourceIdentity` or
  `SourceLastModified`; `UploadFileAsync` sets both. `SourceIdentity` must change whenever the content
  changes: use a content id or version, or a path only together with `SourceLastModified`. A path alone
  lets an edited file of the same length continue the old prefix (only `Verify` would catch it). Copies
  and moves identify the source by its ETag, time, or version, and do not resume a source that has none.
- **One writer.** Two transfers of the same source to the same destination in one process never append to
  one staging object: the second stages privately. A staging object whose size changed under a transfer
  is never promoted.
- **Across restarts.** A failed or cancelled copy's report carries a `ResumeToken`. Store it and pass it
  back in `StorageTransferOptions.ResumeToken`; this works from another process after a restart too. A
  token is followed only for exactly the same source, and only onto its own `.cl-storage-part-…` object;
  a token naming anything else is ignored, and nothing it names is deleted.
- **A token does not mean overwrite.** Only `ConflictPolicy = Resume` replaces an existing destination; a
  `ResumeToken` together with `Overwrite = false` or `ConflictPolicy = Fail` fails validation.
- **Integrity.** With `Verify`, the part staged earlier is read again from the source and must match
  before the rest is appended, so the digest covers the whole file.
- **Already complete.** A destination counts as complete only when both sides report the same digest; an
  equal size is not enough. A resumed move whose destination is complete deletes its source.
- **Object stores** cannot append, so the staging object is rewritten from the start.
- A staging object whose size cannot be read (for any reason but "not found") is kept, and the attempt
  fails rather than appending after bytes it cannot count.

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
- A failing store does not wedge the queue: a failed claim is tried again a moment later, renewals and
  saves are retried while the lease holds, and a job whose outcome could not be recorded is recovered
  later. A store failure never uses up a job's `AutomaticRetries`.
- Removing, clearing, and pruning are conditional on the record's revision and lease too, so they never
  delete a job another process is running or has just retried.

**Writing a store.** `InMemoryStorageTransferJobStore` is the reference implementation. A store must:

- decide lease expiry by its own clock alone (a database should use its own `now()`). The queue reads
  `LeaseExpiresAt` with its own clock only as a hint, to decide when to retry a claim or when a renewal is
  overdue; a skewed worker clock costs a refused claim or an early stop, never two workers holding one job;
- issue, on every claim, a fencing token greater than any issued before for that job id, including before
  the id was removed and added again;
- remove a record only at the expected revision (`RemoveAsync(jobId, expectedRevision, lease)`), and end a
  lease without a save (`ReleaseAsync`);
- keep `SchemaVersion` with the record and its spec, or store `StorageTransferJobRecord.ToJson()` and read
  it back with `FromJson`. A queue leaves records written by a newer schema alone.

**Restarts.** A transfer records its phase as it goes. When a new process finds a job that was running
in an earlier one:

- if the destination was never touched, the job goes straight back to the queue, and a resumable
  transfer continues from its staged bytes;
- otherwise the job becomes `Interrupted`, for a person to decide (and a `StorageTransferInterruptedEvent`
  is published). File uploads and downloads cannot tell the queue when they reach their destination, so
  once running they always count as touched. With `RequeueInterruptedWhenSafe = false`, every job found
  running becomes `Interrupted`.

The recorded phase never goes backwards within an attempt, so a directory copy that began committing is
never mistaken for an untouched one.

A process restarted with the same `WorkerId` takes back its own leases at once, so give every running
queue its own `WorkerId`. A transfer that succeeded is recorded `Completed` even if it was being paused,
cancelled, or shut down as it finished, and a move whose destination committed stays
`NeedsReconciliation` even when a cancel or pause arrives, so a committed move is never run again.

Disposing the queue, or stopping the library that opened it, waits up to `ShutdownTimeout` (30 s by
default) for running transfers; each one ends `Queued` or `Interrupted` by its phase, with its lease
released, and never `Failed`. After disposal, calls that change jobs return `storage.unavailable`.

**States.**

| State | Meaning |
|---|---|
| `Queued`, `Running`, `Paused` | waiting, in progress, or held |
| `Completed`, `Failed`, `Cancelled` | finished (`MaxFinishedJobs`, 1,000 by default, keeps the newest) |
| `Blocked` | needs a person: `BlockReason` is `Trust` (an untrusted host key or certificate) or `Credential` |
| `NeedsReconciliation` | left a mixed state; the job's `LastReport` says which |
| `Interrupted` | found running after a restart, with the destination already touched |

Only transient failures are retried, with exponential backoff and jitter that honours a server's
`Retry-After` (up to 30 days). `FailedJobs` and `RetryFailedAsync` cover `Failed` jobs only; `Blocked`,
`NeedsReconciliation`, and `Interrupted` jobs are retried one by one with `RetryAsync` once a person has
looked at them. A provider's `storage.partial_failure` ends a job as `NeedsReconciliation`.

The numbers of `StorageTransferState` are fixed: `Queued` 0 to `Cancelled` 4 as in 4.8.93, then `Paused`,
`Blocked`, `NeedsReconciliation`, and `Interrupted`. New states are only ever added at the end.

**Control.**

- Pause or resume the whole queue with `Pause`/`Resume`, or one job with `PauseJobAsync`/`ResumeJobAsync`.
  A running resumable transfer keeps its staged data while paused.
- `CancelAsync`, `RetryAsync`, and `RemoveAsync` act on one job. `RetryAsync` also resets `Attempts`, so
  backoff starts from the base delay again.
- Pausing, cancelling, or removing a running job waits until its transfer has stopped, and succeeds only
  if it took effect; when the job finished first, or committed a move, the call returns `storage.conflict`.
- Methods that return a `Result` never throw `OperationCanceledException`; an enqueue whose store call was
  cancelled after the store committed returns the stored job.
- Priorities are integers, higher first. Change a waiting job's with `SetPriorityAsync`, or reorder with
  `MoveUpAsync`/`MoveDownAsync`. Within a priority, jobs run in `Order`, a time-based number that stays
  unique across queues sharing a store.
- Enqueueing checks that the options fit the kind: an upload has no source connection, a download no
  destination connection, and only an upload takes `StorageUploadOptions`.

**History and events.**

- `MaxFinishedJobs` caps history, and `ClearAsync(states)` clears jobs by state.
- Progress events are throttled by `ProgressInterval`.
- `EventContext` raises `JobChanged`, `ProgressChanged`, and `JobRemoved` on a UI thread.
- The event bus receives started, completed, failed, cancelled, retrying, blocked, needs-reconciliation,
  and interrupted events.
- The last progress report of a job always arrives, even when it fails; later reports are dropped.

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
`LocalStorageBackend` over a local folder. `SyncAsync` plans and applies in one call; when conflicts block
it, its failure names up to ten of them and carries `conflicts=N` in its details.

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
  one item, and each side keeps its own spelling, folders included: a new file under `Docs/` goes into the
  destination's existing `docs/`. Case is folded with `ToUpperInvariant`, and names in the two Unicode
  normal forms (NFC and NFD) are the same item.
- The baseline saved after a run records the versions both sides agreed on when the plan was made and the
  versions the run itself wrote, never a listing taken afterwards. A file edited during or just after a run
  is still seen as changed next time, and a step that did not complete is tried again. Neither side having
  changed while their content differs is a conflict, not "in sync".

### Conflict policies

| Policy | Behavior |
|---|---|
| `Block` (default) | plan the conflict; the plan cannot be applied until it is resolved, unless `ApplyWithConflicts` is set |
| `KeepBoth` | the source's version keeps the name; the destination's version is kept on both sides as `name (conflict xxxxxxxx).ext` (`… 2`, `… 3` when that name is taken) |
| `NewerWins` | the newer side wins; a modification beats a deletion; two versions with the same time are left alone (`NotRun`) without blocking the rest |

### Plan, then apply

- The plan lists every step with the versions it depends on. It serializes with `ToJson()`, and
  `Digest` is a SHA-256 over its content, which includes its `SchemaVersion`, both connection ids, whether
  case was ignored, and `OptionsDigest`, a digest of the options that shape the plan.
- `ApplySyncAsync` refuses a plan that is not the one approved, a plan made for other connections or in an
  older format (plan again), and options that differ from the plan's. Only `MaxConcurrency`,
  `ItemRetries`, `ContinueOnError`, `DryRun`, `Progress`, and `StateStore` may differ at apply;
  `TimeTolerance`, `ApplyWithConflicts`, and the deletion limits must be set when planning.
- It also refuses a plan whose baseline moved on because another run saved in between.
- Each step re-checks its items first. A step whose item changed since planning, or whose path changed
  type (a folder where a file was), is reported `Stale` and not taken. A check that fails for any reason
  other than "not found" fails the step (or retries it), rather than treating the item as gone.
- Two applies of the same `SyncId` run one after the other within a process; the lock does not reach other
  processes.
- `DryRun` returns the plan without changing anything.

### Deletion safety

Deletions are withheld, and listed in `plan.Warnings` and `report.Withheld`, when:

- they would exceed `MaxDeletes` or `MaxDeletePercent` (counted over files; `NaN` is refused);
- a side is unexpectedly empty, as when a drive is not mounted or a root is wrong (unless
  `AllowEmptySide` is set);
- any other step failed in the same run.

A listing that fails part-way fails the plan. Folders are never deleted recursively: their files are
deleted one by one, each checked at apply time, and then the emptied folders. A folder that gained items
after the plan, or holds items the filters left out, is kept, and keeps its baseline entry. A path the
source left out (a link, a hidden item, anything below an excluded folder) is never deleted from the
destination.

### Copies and comparison

- **Copies.** Each copy is a staged write that is conditional on the planned versions (create-new, or
  replace only that version), pinned to the planned source, and optionally verified (`Verify`, which
  confirms the destination after the promote and records the SHA-256 in the baseline). A source without an
  ETag or version (FTP, SFTP) is checked again after streaming, by size and time. A `KeepBoth` rename is
  pinned to the planned version too; on FTP and SFTP, which cannot pin, size and time are checked just
  before an atomic server-side rename. A copy that committed is recorded in the baseline even when a cancel
  lands right after it. A failed copy starts again from the beginning on its next attempt.
- **Missing root.** A destination root that does not exist yet is created by the plan's first step.
- **Timestamps.** Copied files keep the source's modification time. On object stores that cannot set
  times, the time is kept in `cl-mtime` metadata and used by later comparisons; a kept time without an
  offset is read as UTC.
- **What is compared.** Size and time, by default, with a two-second tolerance. `CompareBy.Checksum`
  uses the server's digest where there is one.
- **Hashing.** Without a server digest, files are hashed in parallel (`HashConcurrency`) within
  `MaxHashedFiles`/`MaxHashedBytes`, taken for both sides of a pair at once; a file of unknown size is not
  hashed under a byte budget. Beyond the budget a difference is `Undecidable`, as is a pair where one file
  could not be read; a connection failure still fails the comparison. One-way sync leaves an
  `Undecidable` pair alone, with a warning, while size and time agree. `ChecksumAlgorithm` is a
  preference: each side uses a digest its server keeps (MD5 or SHA-256) where it can, so only the other
  side is downloaded. On object stores whose listings omit metadata this costs one `HEAD` per file whose
  sizes are equal.
- **Filters.** `Include`/`Exclude` globs, where `**` spans folders, apply to both sides and to the
  baseline. Excluding a folder excludes everything inside it, as in `.gitignore`
  (`Exclude = ["**/node_modules"]`). `IncludeHidden = false` leaves out hidden folders with their
  contents. A path excluded, hidden, or under a file/folder clash keeps its baseline entry, so lifting the
  filter later does not bring back a deletion made meanwhile.
- **Case.** Names that differ only by case are refused on a case-insensitive side
  (`CaseInsensitivePaths`, or `CaseInsensitive`), instead of letting the last copy win; one such
  collision fails the whole comparison. Local connections decide `CaseInsensitivePaths` by probing their
  root, and assume the folders below it behave the same.
- **Links.** `LinkHandling` skips links by default, together with anything listed below a link to a
  folder. `Follow` compares what a link points to, when the target is inside the connection.
- **Size cap.** `MaxItems` (1,000,000 by default) caps the size of a tree; a larger one fails the plan.
- **Out-of-root items.** An item a provider lists outside the compared folder fails the comparison
  (`storage.provider_error`) instead of disappearing; a root spelled in the server's own case is accepted.

### Runs

Transient failures are retried per step (`ItemRetries`). `ContinueOnError = false` stops at the first
failure; steps it stopped are `NotRun`. Read each step's outcome from `report.Results`. A provider or
state store that throws fails its step or is reported in `report.BaselineError`; it never loses the
report.

**Cancelling.** Once `ApplySyncAsync` or `SyncAsync` has started applying, cancelling does not throw
`OperationCanceledException` (4.8.93 did): the result is a success whose `report.Cancelled` is `true`.
`Results` says what was applied, and steps not started are `NotRun`. For a two-way sync with a baseline,
the baseline is still saved: completed steps are recorded and every other path keeps its previous entry.
A successful result therefore does not mean the sync finished; check `report.Cancelled`. Cancelling while
planning (`PlanSyncAsync`, or the planning part of `SyncAsync`), or while waiting for another apply of the
same `SyncId`, still throws `OperationCanceledException`.

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
create. A failed poll is retried on the next interval rather than reported as deletions, and reported to
`StorageWatchOptions.PollFailed`; a first listing that fails is retried rather than taken as an empty
folder, and a listing cut short part-way (a folder vanishing mid-poll) is retried too. The library's own
staging items never appear; a rename to or from one is reported as a delete or a create. If native
watching cannot even start, `Overflow` is reported and the directory is polled.

Polling a large remote tree is cheaper with `Incremental = true`:

- After the first listing, a poll lists only the root and the folders whose modification time changed
  (entries were added, removed, or renamed in them). The previous listing is reused for the rest.
- Every `FullRescanEvery` polls (10 by default), the whole tree is listed again. This catches edits to
  existing files, and changes deep inside folders whose own time did not change. FTP servers that report
  folder times to the minute can hide a change made in the same minute until then.
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

On Windows only symbolic links and junctions are links. Other reparse points, such as OneDrive
Files-On-Demand placeholders, deduplicated files, and app execution aliases, are ordinary files and
folders. A recursive local listing never descends into a link to a folder.
