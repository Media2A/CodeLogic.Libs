# Migrating from CodeLogic.StorageS3

`CodeLogic.Storage` supersedes the S3-only `CodeLogic.StorageS3` package for mounted object and file
storage. The new package deliberately changes configuration and APIs so S3, local filesystems, FTP,
SFTP, WebDAV, Azure Blob, GCS, and Swift share one safe contract.

## Package and library

```diff
- dotnet add package CodeLogic.StorageS3
+ dotnet add package CodeLogic.Storage
```

```diff
- await Libraries.LoadAsync<CL.StorageS3.StorageS3Library>();
- var s3 = Libraries.Get<CL.StorageS3.StorageS3Library>().DefaultService;
+ await Libraries.LoadAsync<CL.Storage.StorageLibrary>();
+ var storage = Libraries.Get<CL.Storage.StorageLibrary>();
+ IStorageService s3 = storage.GetStorage("media");
```

## Connection scope changed

The old service accepted a bucket on each operation. A new S3 connection mounts one bucket and an
optional prefix, so common operations accept only a path below that mount. Create one connection per
bucket/prefix boundary your application needs.

Old shape:

```csharp
await s3.PutObjectAsync("company-media", "production/photo.jpg", source);
```

New shape:

```json
{
  "Connections": {
    "media": {
      "Bucket": "company-media",
      "Prefix": "production",
      "Region": "eu-north-1",
      "AuthenticationMode": "DefaultCredentialChain"
    }
  }
}
```

```csharp
await storage.GetStorage("media").UploadAsync("photo.jpg", source);
```

Configuration is now in `storage` and `storage.s3` sections rather than `storages3`. Credentials can
use the AWS default credential chain or explicit static credentials. A clear-text custom endpoint is
rejected unless `AllowInsecureHttp` is deliberately enabled.

## Operation mapping

| StorageS3 | Storage |
|---|---|
| `GetService(id)` / `DefaultService` | `GetStorage(id)` / `DefaultStorage` |
| `PutObjectAsync(bucket, key, stream)` | `UploadAsync(path, stream)` |
| `PutObjectAsync(bucket, key, bytes)` | `UploadBytesAsync(path, bytes)` |
| `GetObjectStreamAsync` | `DownloadAsync` |
| `GetObjectAsync` | `DownloadBytesAsync` |
| `GetObjectInfoAsync` | `GetInfoAsync` |
| `ObjectExistsAsync` | `ExistsAsync` returning `Result<bool>` |
| `ListObjectsAsync` | `ListAsync`, `EnumeratePagesAsync`, or `EnumerateItemsAsync` |
| `CopyObjectAsync` | service `CopyAsync` or cross-connection `StorageLibrary.CopyAsync` |
| no common move | service or cross-connection `MoveAsync` |
| `DeleteObjectAsync` | `DeleteAsync` |
| `GeneratePresignedUrlAsync` | `CreateSignedUrlAsync` for reads or writes |
| `GetObjectTaggingAsync` / `PutObjectTaggingAsync` | `GetTagsAsync` / `SetTagsAsync` |
| version download | `StorageDownloadOptions.VersionId` |
| no version listing | `ListVersionsAsync` / `EnumerateVersionPagesAsync` |

Existence checks now use `Result<bool>` so authorization, timeout, and provider failure are not
silently confused with a missing item.

## Upload options

Portable upload options include overwrite/create-only behavior, parent creation, content type, user
metadata, and atomic ETag/version conditions. Provider-specific storage class, cache-control,
content-disposition, canned ACL, and public-URL behavior are intentionally not guessed across
providers. Use `GetNativeClient<IAmazonS3>` for those S3-specific operations.

The old `MakePublic` shortcut has no common replacement. Public ACL changes are security-sensitive and
should be performed explicitly with the native client or bucket policy.

Large S3 uploads are multipart automatically and keep one bounded part buffer. Upload streams remain
caller-owned.

## Buckets and provider administration

The common package is rooted inside an already provisioned bucket/container/directory. Bucket create,
delete, account-wide listing, lifecycle policy, IAM, and similar administration remain provider-specific:

```csharp
IAmazonS3 client = storage.GetNativeClient<IAmazonS3>("media");
```

This separation also allows bucket-scoped credentials to pass normal connection health checks.

## Events and errors

The old S3-only object events become provider-neutral `StorageItemWrittenEvent`,
`StorageItemDeletedEvent`, `StorageItemCopiedEvent`, and `StorageItemMovedEvent`. Cross-connection and
local-directory completion events include sanitized counts but never signed URLs or credentials.

Expected failures now use stable `storage.*` codes. Cancellation of a connection call is an
`OperationCanceledException`, not a failed result; `StorageLibrary.CopyAsync`/`MoveAsync` and a sync that is
already applying report it instead (see *Upgrading from CodeLogic.Storage 4.8.93* below). A move whose
destination completed but whose source deletion failed returns `storage.partial_failure` (reported as
`NeedsReconciliation`); callers should reconcile that state rather than retrying blindly.

### Finer error codes

Provider failures that previously surfaced as `storage.unauthorized`, `storage.unavailable`, or
`storage.provider_error` now use more specific codes. The Core error kind is unchanged for the
authorization and availability groups, so code that branches on the kind keeps working; code that
compares exact codes should switch to the table below or to `StorageErrorInfo.IsTransient`.

| Situation | Before | Now |
|---|---|---|
| Wrong password, key, or token (FTP 530, SSH auth, HTTP 401) | `unauthorized` / `provider_error` | `authentication_failed` |
| Logged in but not allowed (FTP 550 permission, SFTP, HTTP 403, local ACL) | `unauthorized` / `provider_error` | `permission_denied` |
| TLS handshake or certificate pin failure | `unauthorized` / `unavailable` | `tls_failure` |
| SSH host key not trusted | `unavailable` | `host_key_rejected` |
| DNS failure, connection refused, proxy failure | `unavailable` / `provider_error` | `connection_failed` |
| Connection dropped mid-operation | `unavailable` / `provider_error` | `connection_lost` |
| Rate limited or too many sessions (HTTP 429/503, FTP 421, SSH) | `unavailable` / `provider_error` | `server_busy` |
| Disk full or quota (FTP 452/552, HTTP 507, local ENOSPC) | `unavailable` / `provider_error` | `quota_exceeded` |

`StorageErrorInfo.IsTransient` returns true for `timeout`, `unavailable`, `connection_failed`,
`connection_lost`, and `server_busy`. Provider codes are available through
`StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.FtpReplyKey, out var reply)` and similar keys.

### Connection info and diagnostics

`StorageConnectionInfo` gained `Host`, `Port`, `Security`, and `LastHealth` init properties; its
positional members are unchanged. Use `TestConnectionAsync` to check settings before
`AddOrUpdateConnectionAsync`, and subscribe to `StorageOperationFailedEvent` or
`StorageConnectionHealthChangedEvent` for monitoring instead of wrapping every call.

## Upgrading from CodeLogic.Storage 4.8.93

This release breaks source and binary compatibility with 4.8.93. Rebuild everything that references
CodeLogic.Storage: an assembly compiled against 4.8.93 still loads, and fails with
`MissingMethodException` the first time it calls a changed member. `CHANGELOG.md` lists every change;
this section says what to do about each.

### Copy and move return a report

`StorageLibrary.CopyAsync` and `MoveAsync` return `StorageTransferReport` instead of `Result`:

```csharp
// 4.8.93
Result copied = await storage.CopyAsync("a", "x.bin", "b", "x.bin");

// now
StorageTransferReport report = await storage.CopyAsync("a", "x.bin", "b", "x.bin");
if (report.IsFailure) Handle(report.Error!);      // or: Result copied = report.ToResult();
```

- They no longer throw `OperationCanceledException`: check `report.Outcome == StorageTransferOutcome.Cancelled`
  (error `storage.cancelled`). A token cancelled before the call, an unknown connection id
  (`storage.not_found`), and a provider that throws are reports too. `IStorageService.CopyAsync`/`MoveAsync`
  on a connection keep returning `Result` and throwing `OperationCanceledException` on a cancel.
- Handle `NeedsReconciliation`: the destination committed but something after it did not finish (the move's
  source is still there, or a directory move found files added or changed while it ran). Read
  `DestinationCommitted`, `SourceDeleted`, `StagingLeftBehind`, and `BackupLeftBehind`; do not retry blindly.
- `SourceDeleted` is `true` only when the whole source is gone; a directory move that kept skipped files is
  `Completed` with `SourceDeleted = false`.
- A committed destination is never rolled back. A backup the library could not remove after a successful
  transfer used to make it `NeedsReconciliation`; it is now `Completed` with `BackupLeftBehind` set.
- A directory moved onto an existing directory on the same connection now merges. Code that relied on FTP,
  SFTP, or WebDAV replacing the existing directory must delete it first.
- `Rename` names differ: `name (1).txt` goes on to `name (2).txt`, and `file.` becomes `file. (1)`.
- FTP connections with `Session.MaxSessions = 1` cannot relay within the connection; raise it to 2.
- `UploadDirectoryAsync`/`DownloadDirectoryAsync` return a failed result instead of throwing when a cancelled
  transfer could not roll back.

### Resume

- `ConflictPolicy.Resume` resumes through a staging object; the destination is replaced only when complete.
  A partial destination left by the old in-place resume is not continued; it is replaced once the staged
  copy completes. On object stores `Resume` now rewrites instead of returning `storage.unsupported`.
- Resuming an upload needs `SourceIdentity` or `SourceLastModified` (`UploadFileAsync` sets both).
  `SourceIdentity` must change whenever the content does: a content id or version, or a path together with
  `SourceLastModified`.
- A destination of the same size is no longer "already complete": both sides need the same digest.
- A stored `ResumeToken` together with `Overwrite = false` or `ConflictPolicy = Fail` fails validation; use
  `ConflictPolicy = Resume`.

### Local ETags and links

- Local items now have an ETag (they had none): a weak `W/"…"` of write time, creation time, and length.
  Code that treated a null ETag as "local" should check `Provider`. The ETag can repeat on coarse file
  systems or after a tool restores file times; do not use it as proof of unchanged content.
- On Windows only symbolic links and junctions are links; OneDrive placeholders and other reparse points
  are files. Recursive local listings no longer descend into links to folders.

### Transfer queue

| 4.8.93 | Now |
|---|---|
| `storage.CreateTransferQueue(options)` | `(await storage.OpenTransferQueueAsync(options)).Value!` |
| `queue.EnqueueCopy(...)` returns a job | `await queue.EnqueueCopyAsync(...)` returns `Result<StorageTransferJob>` |
| `StorageTransferPriority.High` | an integer, e.g. `priority: 10` (higher starts first) |
| `Guid` job ids (also on the started/completed/failed events) | `string` ids, optionally chosen by the caller |
| `queue.Cancel(id)`, `Retry(id)`, `RetryFailed()`, `ClearFinished()` | `CancelAsync`, `RetryAsync`, `RetryFailedAsync`, `ClearAsync()` |
| `RetryDelay` | `RetryBaseDelay` and `RetryMaxDelay` (exponential backoff) |
| `new StorageTransferJob(...)`, `Deconstruct` | not available; read the get-only properties |
| `job.EnqueuedAt`, `job.FinishedAt` | `job.Record.EnqueuedAt`, `job.Record.FinishedAt` |
| removal raised `JobChanged` with `Cancelled` | removal raises `JobRemoved` |

- `AutomaticRetries` defaults to 3 (it was 2). Set it explicitly to keep 2.
- Disposing the queue leaves queued jobs queued in the store instead of cancelling them; with the default
  in-memory store they are simply gone with the queue.
- At most `MaxFinishedJobs` (1,000) finished jobs are kept; set `null` to keep them all.
- Authentication and trust failures stop as `Blocked` instead of `Failed`, and a partial failure as
  `NeedsReconciliation`. `FailedJobs`/`RetryFailedAsync` cover `Failed` only; retry the others one by one
  with `RetryAsync`. Handle the new states `Paused`, `Blocked`, `NeedsReconciliation`, and `Interrupted`.
- Stored `StorageTransferState` numbers keep their meaning: 0–4 are `Queued`, `Running`, `Completed`,
  `Failed`, and `Cancelled`, as in 4.8.93; new states are numbered after them.

### Sync

- **Cancellation.** `ApplySyncAsync` and `SyncAsync` no longer throw `OperationCanceledException` once they
  have started applying: the result is a *success* with `report.Cancelled = true`. Code that treated "no
  exception" as "the sync finished" must check `report.Cancelled`. Steps not started are `NotRun`; a
  two-way baseline is still saved. Cancelling while planning still throws.
- **Report shape.** `StorageSyncAction` has named properties and no `Error`; build it with an object
  initializer. `StorageSyncReport` has no positional constructor or `Deconstruct`. Read step outcomes from
  `report.Results` (each has `Outcome` and `Error`). `report.Failed` is a list of `StorageSyncActionResult`
  (the action is `.Action`, the error `.Error`). `report.Actions` lists the planned steps, conflicts
  included; `Copied` and `Deleted` still count what was done, and withheld deletions are in `report.Withheld`.
- **Two-way.** Without a baseline, differing files are conflicts, and the default `ConflictPolicy = Block`
  refuses to apply the plan. Set `ConflictPolicy = NewerWins` for the old behaviour, or add `StateStore`
  and `SyncId` for a real three-way sync.
- **Mirror and Update** leave a newer destination alone, even when its size differs (the plan warns). Mirror
  deletes an extra folder file by file, withholds deletes after a failed copy, and never deletes what the
  source left out.
- **Filters.** An `Exclude` pattern that matches a folder now excludes its contents (`"**/node_modules"` is
  enough; `"**/node_modules/**"` still works). `IncludeHidden = false` leaves out hidden folders' contents.
- **Refused now:** `LinkHandling = Recreate`; a plan applied with another `SyncId`, other connections, other
  options, or made by an earlier version (plan again); trees over `MaxItems` (1,000,000).
- **Case.** `CompareAsync` fails when a case-insensitive side has two names that differ only by case.
- `StorageSyncActionKind` numbers are unchanged (0–3 as before), with new kinds after them.

### Listings, watching, and providers

- Recursive listings on S3, Azure Blob, and Swift include folders that exist only as key prefixes, so item
  counts grow; build trees by path, as an inferred folder can repeat across pages.
- Handle `StorageChangeKind.Overflow` from `WatchAsync` by listing the watched directory again.
- Swift no longer declares `ConditionalUpdate`/`ConditionalDelete`, and WebDAV no longer declares
  `AtomicMove`; code that checks the flags takes the checked or relayed path.
- A backend's own `CopyAsync`/`MoveAsync` returns `storage.unsupported` for a pin or destination condition it
  cannot enforce, instead of ignoring it.
- A client certificate file without its private key now fails registration.
- A TLS stream that breaks after the handshake is `storage.connection_lost` (transient), not
  `storage.tls_failure`.
- For S3-compatible servers other than AWS and MinIO, check which conditional requests they enforce and set
  `ConditionalRequests` (`Auto` probes copies and deletes and trusts uploads).

### Shared sessions and tokens

- FTP and SFTP registrations with identical settings share one session pool, so `MaxSessions` caps them
  together. Give registrations that must have their own limit different settings (for example a different
  `Session` section).
- Listing continuation tokens on Local, FTP, SFTP, and WebDAV are tied to the settings instead of the
  connection id.

### Custom transfer-job stores

4.8.93 had no store hook; a store written against a preview of this release must follow the final contract:

- `RemoveAsync(jobId, expectedRevision, lease, cancellationToken)` returns whether the record was removed
  (or already gone), refusing a changed revision or a stale lease;
- `ReleaseAsync(lease, cancellationToken)` ends a lease without a save;
- the store's own clock decides lease expiry; fencing tokens only grow for a job id, even across removal;
- keep `SchemaVersion` (or store `StorageTransferJobRecord.ToJson()`).

### Runtime-only mode

Applications that registered every connection at runtime with `persist: false` and disabled the
configured ones can construct `new StorageLibrary(new StorageLibraryOptions { RuntimeOnly = true })`
instead; no `config.storage*.json` file is created.

## Recommended rollout

1. Add `CodeLogic.Storage` beside the legacy package.
2. Create mounted `storage.s3` connections for the buckets/prefixes used by the application.
3. Migrate object data paths and result handling one call site at a time.
4. Move bucket administration and non-portable object options to typed native-client calls.
5. Verify capability flags against every S3-compatible endpoint used in production.
6. Remove `CodeLogic.StorageS3` after its configuration and events are no longer consumed.
