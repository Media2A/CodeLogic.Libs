# CodeLogic.Storage

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Storage)](https://www.nuget.org/packages/CodeLogic.Storage)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/LICENSE)

Provider-neutral, root-scoped storage for CodeLogic 4 and .NET 10. One API mounts local/UNC,
S3-compatible, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift
connections.

## Install and load

```bash
dotnet add package CodeLogic.Storage
```

```csharp
using CL.Storage;

await Libraries.LoadAsync<StorageLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var storage = Libraries.Get<StorageLibrary>();
IStorageService media = storage.GetStorage("media");
```

Every connection mounts exactly one local root, bucket/container prefix, or remote directory.
Paths passed to `IStorageService` are relative slash-separated paths below that mount; rooted
paths and `..` escapes are rejected.

## Providers and configuration

Provider connections live in typed, case-insensitive `Connections` dictionaries. Connection IDs
must be unique across all sections.

| Configuration section | Connection model | Mounted resource |
|---|---|---|
| `storage.local` | `LocalConnectionConfig` | local directory or UNC share |
| `storage.s3` | `S3ConnectionConfig` | bucket plus optional prefix |
| `storage.ftp` | `FtpConnectionConfig` | FTP/FTPS directory |
| `storage.sftp` | `SftpConnectionConfig` | SFTP directory |
| `storage.webdav` | `WebDavConnectionConfig` | WebDAV endpoint plus root |
| `storage.azure` | `AzureBlobConnectionConfig` | Blob container plus prefix |
| `storage.gcs` | `GoogleCloudConnectionConfig` | GCS bucket plus prefix |
| `storage.swift` | `SwiftConnectionConfig` | Swift container plus prefix |

The `storage` section selects `DefaultConnection`, controls the byte-buffering limit, and enables
bounded health probes. Example `config.storage.s3.json`:

```json
{
  "Connections": {
    "media": {
      "Enabled": true,
      "Bucket": "company-media",
      "Prefix": "production",
      "Region": "eu-north-1",
      "AuthenticationMode": "DefaultCredentialChain"
    },
    "minio": {
      "Enabled": true,
      "Bucket": "documents",
      "ServiceUrl": "https://minio.example.com",
      "ForcePathStyle": true,
      "AuthenticationMode": "StaticCredentials",
      "AccessKey": "...",
      "SecretKey": "..."
    }
  }
}
```

Clear-text custom S3 or WebDAV endpoints require `AllowInsecureHttp = true`. SFTP requires at
least one SHA-256 host-key fingerprint unless `AutoAcceptHostKey = true` is explicitly enabled.
That option trusts any SSH host key and is best limited to trusted development environments. FTPS
and WebDAV use normal certificate validation by default and optionally accept configured SHA-256
certificate pins; there is no accept-any switch.

### SFTP authentication, host keys, and jump hosts

```json
{
  "Host": "sftp.internal",
  "Username": "deploy",
  "AuthenticationMode": "Auto",
  "Password": "...",
  "PrivateKeyPath": "/secrets/id_ed25519",
  "PrivateKeyContent": null,
  "AdditionalPrivateKeyPaths": [],
  "PrivateKeyPassphrase": "...",
  "KnownHostsPath": "/home/app/.ssh/known_hosts",
  "HostKeyFingerprints": [],
  "Ciphers": ["aes256-gcm@openssh.com", "aes256-ctr"],
  "Encoding": "utf-8",
  "BufferSize": 262144,
  "JumpHost": {
    "Host": "bastion.example.com",
    "Username": "jump",
    "PrivateKeyPath": "/secrets/bastion_ed25519",
    "KnownHostsPath": "/home/app/.ssh/known_hosts"
  }
}
```

- `AuthenticationMode`: `Password`, `PrivateKey`, `KeyboardInteractive` (answers the password
  prompt), or `Auto`, which offers keys, then password, then keyboard-interactive, and also satisfies
  servers that demand several methods. Keys can be files or inline text (`PrivateKeyContent`) from a
  secret store. SSH agents are not supported by the underlying SSH library.
- Host keys are trusted through `HostKeyFingerprints`, an OpenSSH `KnownHostsPath` (plain, hashed,
  wildcard, and `[host]:port` entries), or `AutoAcceptHostKey` for development. A key marked
  `@revoked` in `known_hosts` is refused even with auto-accept. A rejected key reports
  `storage.host_key_rejected` with the presented fingerprint in `Details`.
- `KeyExchangeAlgorithms`, `Ciphers`, `MacAlgorithms`, and `HostKeyAlgorithms` restrict and order
  the offered algorithms, for hardening or for old servers. Unknown names fail validation and list
  what is supported.
- `JumpHost` tunnels through an SSH bastion. The target's key is still verified against the target's
  settings, and a configured `Proxy` applies to the bastion connection.

### FTP and FTPS options

```json
{
  "Host": "ftp.partner.example",
  "EncryptionMode": "Explicit",
  "TrustedPublicKeySha256": ["SHA256:..."],
  "TlsProtocols": ["Tls12", "Tls13"],
  "EncryptDataChannel": true,
  "DataConnectionMode": "AutoPassive",
  "ActivePortMin": 50000,
  "ActivePortMax": 50100,
  "ActiveExternalIp": "203.0.113.7",
  "Encoding": "windows-1252",
  "TransferType": "Binary",
  "ListingParser": "Auto",
  "ServerTimeZone": "Europe/Copenhagen",
  "ReadTimeoutSeconds": 60,
  "SocketKeepAlive": true,
  "LoginCommands": ["SITE UMASK 022"]
}
```

- `TrustedCertificateSha256` pins the whole certificate; `TrustedPublicKeySha256` pins only its
  public key, so it keeps working across renewals that keep the key. Pinned self-signed certificates
  are accepted unless `RequireValidCertificateChain` is set. Without pins, normal validation applies.
  TLS problems report `storage.tls_failure` with a `tlsReason` detail: `server_certificate_rejected`
  (with `presentedCertificateSha256` and `presentedPublicKeySha256`, ready to pin),
  `client_certificate_rejected` (a credential problem), `protocol_mismatch`, or `handshake_failed`.
- A client certificate is read from `ClientCertificatePath`, or from `ClientCertificateContent` (the PFX
  bytes, base64 in JSON) when it comes from a secret store; `ClientCertificatePassword` decrypts either.
- Legacy encodings such as `windows-1252`, `iso-8859-1`, `ibm437`, and `shift_jis` are supported for
  file names on older servers.
- `ServerTimeZone` converts listing times from servers that report local time.
- `LoginCommands` run after every login; a command the server rejects fails the connection so
  misconfiguration surfaces immediately.

### WebDAV options

`AuthenticationMode` accepts `None`, `Basic` (sent up front, saving a challenge round trip),
`BearerToken`, `Digest`, `Ntlm`, `Negotiate`, and `Windows` (current user). HTTPS endpoints support
`TrustedCertificateSha256` and `TrustedPublicKeySha256` pins, `RequireValidCertificateChain`, and a
PFX `ClientCertificatePath` for mutual TLS. `MaxConnectionsPerServer` caps concurrent connections.

### Proxies

Every remote provider can tunnel through an HTTP (`CONNECT`), SOCKS5, or SOCKS4 proxy:

```json
"Proxy": { "Type": "Socks5", "Host": "proxy.corp.local", "Port": 1080, "Username": "me", "Password": "..." }
```

SOCKS5 and HTTP proxies resolve the destination host name on the proxy side. SOCKS4 cannot, so
the host must resolve from the client, and SOCKS4 carries no password. FTP data connections are
tunnelled as well, so use passive mode, and the server's passive address must be reachable
from the proxy.

### Sessions, retries, and keep-alive

FTP and SFTP keep authenticated sessions in a per-connection pool, and FTP, SFTP, and WebDAV
retry transient failures automatically. Both are tuned per connection:

```json
{
  "Connections": {
    "partner": {
      "Host": "sftp.partner.example",
      "Username": "upload",
      "Password": "...",
      "HostKeyFingerprints": ["SHA256:..."],
      "Session": {
        "MaxSessions": 4,
        "MaxIdleSessions": 2,
        "IdleLifetimeSeconds": 120,
        "AcquireTimeoutSeconds": 30,
        "ValidateAfterIdleSeconds": 15,
        "KeepAliveSeconds": 60
      },
      "Retry": {
        "RetryCount": 3,
        "BaseDelayMs": 100,
        "MaxDelayMs": 30000,
        "RetryNonIdempotent": false
      }
    }
  }
}
```

- `MaxSessions` caps open sessions, busy or idle, so the library stays under a server's per-user
  connection limit. Callers beyond it wait up to `AcquireTimeoutSeconds`, then receive
  `storage.server_busy`.
- A pooled session idle longer than `ValidateAfterIdleSeconds` is probed (FTP `NOOP`, SFTP `stat`)
  before reuse. Sessions that time out, drop, or fail TLS mid-operation are closed instead of reused.
- `KeepAliveSeconds` sends FTP `NOOP` or SSH keep-alive packets while a session is open.
- Reads, listings, info, and directory creation retry on timeouts, refused or dropped connections,
  and busy servers, with exponential backoff and jitter; a server `Retry-After` is honored. Uploads
  retry only from a seekable stream, which is replayed from its starting position; staged uploads
  never leave a partial file. Deletes and moves retry only with `RetryNonIdempotent`, because the
  first attempt may already have succeeded.
- `StorageConnectionOpenedEvent`, `StorageConnectionLostEvent`, and `StorageConnectionRetryEvent`
  are published for monitoring, and each retry is logged as a warning.

## Common API

```csharp
await using var source = File.OpenRead("photo.jpg");
Result<StorageItem> uploaded = await media.UploadAsync(
    "photos/photo.jpg",
    source,
    new StorageUploadOptions
    {
        Overwrite = false,
        ContentType = "image/jpeg",
        Metadata = new Dictionary<string, string> { ["owner"] = "42" }
    });

Result<StoragePage> page = await media.ListAsync("photos", new StorageListOptions
{
    Recursive = true,
    PageSize = 250
});

Result<byte[]> range = await media.DownloadBytesAsync(
    "photos/photo.jpg",
    new StorageDownloadOptions { Offset = 1024, Length = 4096 });

Result deleted = await media.DeleteAsync(
    "photos",
    new StorageDeleteOptions { Recursive = true });
```

The common contract includes info/exists, paged recursive listing, physical or virtual directory
creation, streaming and bounded byte uploads/downloads, ranges, delete, copy, move, and cancellation.
Caller upload streams remain open. Returned download streams own their provider response and registry
lease and must be disposed.

Use `EnumeratePagesAsync` or `EnumerateItemsAsync` to walk continuation tokens without buffering a
complete remote tree. Bounded, order-preserving helpers are available for batch info, delete, copy,
and move operations.

## Safe transfers

The library can copy or move files and complete directory trees between any two mounted connections:

```csharp
StorageTransferReport copied = await storage.CopyAsync(
    "primary", "exports/2026",
    "archive", "yearly/2026",
    new StorageTransferOptions
    {
        Overwrite = true,
        MetadataPreservation = StorageMetadataPreservation.BestEffort
    });

StorageTransferReport moved = await storage.MoveAsync(
    "incoming", "ready/item.bin",
    "processed", "item.bin");
```

`CopyAsync` and `MoveAsync` return a `StorageTransferReport` (`IsSuccess`, `Error`, and `ToResult()`
work as on a `Result`). It says what happened — `Completed`, `Skipped` (with `SkipReason`), `Failed`
(nothing committed), or `NeedsReconciliation` (a mixed state) — and, for a single file, the path
actually written (`WrittenPath`, which differs after `Rename`), the bytes, the SHA-256 when verified,
and the destination's new `DestinationETag`/`DestinationVersionId`. When a transfer does not finish, the
state fields say exactly what it left: `DestinationCommitted`, `SourceDeleted`, `StagingLeftBehind`,
`BackupRestored`, `BackupLeftBehind`, and a `ResumeToken`.

Cross-provider data uses a `System.IO.Pipelines` relay capped at 1 MiB. Each destination file is
uploaded to a unique staging name and committed only after the complete source stream succeeds.
Existing destination files are backed up and restored if a later directory item fails. A move deletes
its source only after the entire destination commits. Equal paths and a directory destination below
its source are rejected.

The normal `IStorageService.CopyAsync` and `MoveAsync` methods use the same coordinator for recursive
work. Safe same-provider file copies remain server-side when the provider can guarantee them.

Local directory trees can be transferred without manually registering a temporary local connection:

```csharp
Result<StorageDirectoryTransferReport> upload = await storage.UploadDirectoryAsync(
    @"C:\exports\2026", "archive", "yearly/2026");

Result<StorageDirectoryTransferReport> download = await storage.DownloadDirectoryAsync(
    "archive", "yearly/2026", @"C:\restore\2026");
```

Links/reparse points in a local upload are rejected rather than followed. Reports contain file,
directory, and byte counts.

### Guaranteed transfers

For a single file, a transfer can be made to promise exactly what was planned:

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

- Content always goes to a staging object beside the destination first. Length and digest are checked,
  and a verified copy is confirmed on the destination (by the server's SHA-256 where it keeps one,
  otherwise by reading it back) before it is promoted. `VerifiedBy` says which.
- `DestinationCondition` is checked before the copy starts and again right before promotion.
  `ConditionEnforcement` reports how: `Atomic` when the provider enforces it in the same operation
  (create-new with `Overwrite = false` on connections with `ConditionalCreate`), otherwise
  `CheckedBeforeCommit`.
- A source pinned by ETag is read again after streaming; a change in between fails with
  `storage.conflict` and nothing is committed.
- A move deletes its source only while it is still the version that was copied — with a conditional delete
  where the provider has one — otherwise the report is `NeedsReconciliation` with `SourceDeleted = false`.
- Local files now carry an ETag (last-write time and size), so conditions work on local connections too.

### Streamed writes

`OpenWriteAsync` returns a stream to write a file's content into, for push-style producers:

```csharp
var opened = await files.OpenWriteAsync("exports/data.csv", new StorageUploadOptions { Verify = true });
await using var writer = opened.Value!;
await writer.WriteAsync(chunk);
Result<StorageItem> committed = await writer.CommitAsync(); // or AbortAsync(); disposing without commit aborts
```

Nothing appears at the destination until `CommitAsync` succeeds. At most 1 MiB is buffered, so a slow
destination slows the writer down. Conflict policies, `Condition`, `ExpectedLength`, `Verify`, and
`ExpectedSha256` apply as for uploads; conflict decisions happen before the first byte is written.

## File, text, JSON, progress, and integrity helpers

`StorageServiceExtensions` adds:

- `UploadFileAsync` and atomic `DownloadToFileAsync`;
- bounded `ReadTextAsync` / `WriteTextAsync` with explicit encodings;
- bounded `ReadJsonAsync<T>` / `WriteJsonAsync<T>`;
- `UploadWithProgressAsync` / `DownloadWithProgressAsync`;
- streaming `ComputeChecksumAsync` / `VerifyChecksumAsync` using MD5, SHA-256, SHA-384, or SHA-512.

```csharp
var progress = new Progress<StorageTransferProgress>(value =>
    Console.WriteLine($"{value.BytesTransferred} bytes"));

await media.UploadWithProgressAsync("large.bin", input, progress);
Result<StorageChecksumVerification> verified = await media.VerifyChecksumAsync(
    "large.bin", expectedSha256Hex);
```

MD5 is supplied only for interoperability; prefer SHA-256 or stronger for security-sensitive checks.

## Capabilities and advanced contracts

Capabilities are granular flags plus provider limits. Check them at runtime rather than inferring
behavior from a provider name:

```csharp
if (media.Capabilities.Supports(StorageFeature.MetadataWrite))
    await media.SetMetadataAsync("photo.jpg", new Dictionary<string, string> { ["reviewed"] = "yes" });
```

| Provider | Directories | Metadata | Tags | Conditional create/update/delete | Versions | Signed URLs |
|---|---|---|---|---|---|---|
| Local / UNC | physical | no | no | create | no | no |
| S3-compatible | virtual | read/write | read/write | yes/yes/yes | read/list/delete | read/write |
| FTP / FTPS | physical | no | no | no | no | no |
| SFTP | physical | no | no | no | no | no |
| WebDAV | physical | discovered properties are read-only | no | create | no | no |
| Azure Blob | virtual | read/write | read/write | yes/yes/yes | read/list/delete | SAS when credentials permit |
| Google Cloud Storage | virtual | read/write | no portable contract | yes/yes/yes | read/list/delete | when signing credentials permit |
| OpenStack Swift | virtual | read/write | no portable contract | yes/yes/yes | endpoint-specific/native | no portable TempURL contract |

Advanced functionality stays out of the basic interface and is exposed through capability-gated
optional contracts:

- `IStorageMetadataService`: merge or replace user metadata, optionally matching ETag/version;
- `IStorageTagService`: read, merge, or replace up to ten portable object tags;
- `IStorageSignedUrlService`: temporary read or write URLs with bounded expiry;
- `IStorageVersionService`: exact-object version pages and exact-version deletion.

Convenience extension methods (`GetMetadataAsync`, `SetMetadataAsync`, `GetTagsAsync`, `SetTagsAsync`,
`CreateSignedUrlAsync`, `ListVersionsAsync`, `EnumerateVersionPagesAsync`, and `DeleteVersionAsync`) return
`storage.unsupported` when the active backend does not implement the operation.

Atomic upload/delete identity checks use `StorageMutationCondition`:

```csharp
await media.UploadAsync("settings.json", replacement, new StorageUploadOptions
{
    Condition = new StorageMutationCondition
    {
        ExpectedETag = current.Value!.ETag,
        ExpectedVersionId = current.Value.VersionId
    }
});
```

Providers that cannot enforce the condition atomically reject it instead of performing a racy
check-then-write.

Creating only when absent (`Overwrite = false`) is atomic where `StorageFeature.ConditionalCreate` is
declared: Local, WebDAV, S3, Azure Blob, Google Cloud Storage, and Swift. FTP and SFTP do not declare it,
because their protocols have no atomic create-if-absent; a caller that must not race should check the
flag and refuse. Local overwrites are staged too: the new content is written to a temporary file in the
same directory and moved over the target, so a reader never sees a half-written file.

## Permissions, ownership, timestamps, and links

`StorageItem` now carries `UnixMode` (with `Permissions` as `rwxr-xr-x` text), `Owner`/`Group`
(FTP listings), `OwnerId`/`GroupId` (SFTP), `LinkTarget`, `Created`, `LastAccessed`, and `IsHidden`
wherever the provider reports them. Changing them goes through `IStorageAttributeService`, exposed as
extension methods on every `IStorageService`:

```csharp
await media.SetPermissionsAsync("reports/q3.csv", "640");
await media.SetPermissionsRecursiveAsync("public", fileMode: 0x1A4, directoryMode: 0x1ED); // 0644 / 0755
await media.SetOwnerAsync("reports/q3.csv", ownerId: 1001, groupId: 1001);
await media.SetTimestampsAsync("reports/q3.csv", lastModified: sourceTime);
await media.CreateLinkAsync("current", "releases/v42");
var link = await media.ReadLinkAsync("current");
```

| | Local | FTP | SFTP |
|---|---|---|---|
| Permissions | Unix only | `SITE CHMOD` | yes, incl. setuid/setgid/sticky |
| Owner/group | no | no | numeric IDs |
| Timestamps | modified + accessed | modified (`MFMT`/`MDTM`) | modified + accessed |
| Create link | yes (relative) | no | yes |
| Read link | yes | from listings | no (SSH.NET lacks `readlink`) |

Check `Capabilities` for `Permissions`, `Ownership`, `SetTimestamps`, `CreateLinks`, and
`ReadLinks`; unsupported calls return `storage.unsupported`. Link targets must stay inside the
mounted root. On Windows, creating local links needs Developer Mode or the symbolic-link privilege.

### When the destination already exists

`ConflictPolicy` on `StorageUploadOptions` and `StorageTransferOptions` mirrors FileZilla's
"target file already exists" choices: `Fail`, `Overwrite`, `Skip`, `OverwriteIfNewer`,
`OverwriteIfSizeDiffers`, `OverwriteIfNewerOrSizeDiffers`, and `Rename` (writes `name (1).ext`).
When it is not set, the `Overwrite` flag decides as before.

```csharp
await media.UploadFileAsync("backup/db.bak", @"C:\dumps\db.bak",
    new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewer });
var report = await storage.UploadDirectoryAsync(@"C:\site", "web", "public",
    new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.OverwriteIfNewerOrSizeDiffers });
Console.WriteLine($"{report.Value!.Files} uploaded, {report.Value.SkippedFiles} unchanged");
```

- Directories are decided file by file; `StorageDirectoryTransferReport.SkippedFiles` counts the rest.
- A skipped upload succeeds and returns the existing item, without a write event.
- Moving a directory deletes only the source files that were transferred; skipped files stay.
- "Newer" allows two seconds of clock slack. `UploadFileAsync` supplies the local file's time; for
  stream uploads set `SourceLastModified`. Unknown times or sizes count as newer or different.
- `DownloadToFileAsync` takes a `conflictPolicy` for the local file.
- Conditional policies on copy and move are applied by `StorageLibrary` and its connections, not by a
  backend's own `CopyAsync`/`MoveAsync`.

### Resume and append

`ConflictPolicy = Resume` writes through a resumable staging object (`.cl-storage-part-…`) and replaces
the destination only once it is complete, so the destination is never half-written. If an attempt fails,
the staged bytes stay; the next attempt for the same destination and the same source (same length,
modification time, ETag, or version) reads only the missing tail and appends it (FTP `APPE`, SFTP append,
local files). Uploads need a seekable stream and use `SourceLastModified` as part of the source's identity;
copies and moves identify the source themselves. A failed copy's report carries a `ResumeToken` that can
be stored and passed back in `StorageTransferOptions.ResumeToken`, even from another process after a
restart. Where the destination cannot append (object stores), the staging object is rewritten from the
start. Combine with `Verify` to hash the whole file, including the part staged earlier.
`DownloadToFileAsync(..., conflictPolicy: Resume)` continues a partial local file with a ranged download.
`AppendAsync` appends to a file directly, for example a log, and `CleanupStaleStagingAsync` removes
staging leftovers.

### Progress and speed limits

Upload, download, and transfer options take a `Progress` sink. Reports arrive at most every 250 ms and
carry `BytesTransferred`, `TotalBytes`, `BytesPerSecond`, `EstimatedRemaining`, and, for directory
transfers, the `ItemPath` of the current file plus `FilesCompleted`/`FilesTotal`; directory transfers
accumulate bytes across files. Set `StorageTransferOptions.PreScan` to list a directory first so its
reports carry totals and a time estimate. A server-side copy or move reports its start and its end.

```csharp
var progress = new Progress<StorageTransferProgress>(p =>
    Console.WriteLine($"{p.ItemPath}: {p.BytesTransferred:N0} B at {p.BytesPerSecond / 1024:N0} KiB/s"));
await storage.CopyAsync("sftp", "exports", "s3", "archive", new StorageTransferOptions { Progress = progress });
```

Speed limits are set per connection and shared by all of its concurrent transfers:

```json
"TransferLimits": { "MaxUploadBytesPerSecond": 1048576, "MaxDownloadBytesPerSecond": 5242880 }
```

`StorageConfig.MaxTotalUploadBytesPerSecond` and `MaxTotalDownloadBytesPerSecond` cap all
connections together. Limits are enforced inside each provider's upload and download, so they apply to
every path: `UploadAsync` and `DownloadAsync` streams used directly, the file helpers, and relayed
transfers between connections. Download progress carries `TotalBytes` even without a `Length`; the
item's size is looked up once when the stream cannot report it.

### Transfer queue

`OpenTransferQueueAsync` runs transfers in the background, like FileZilla's queue:

```csharp
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

- **Jobs are data.** A `StorageTransferJobSpec` describes the kind, both endpoints, and every option;
  `ToJson`/`FromJson` store it. Caller-chosen ids make enqueueing idempotent: the same id with the same
  work returns the existing job, the same id with different work fails with `storage.conflict`.
- **Durable, shared store.** `IStorageTransferJobStore` (in memory by default) holds every job. Workers claim
  a job with a lease carrying a fencing token and renew it while running; a save with a stale lease is
  refused, so a worker that lost its lease never records an outcome, and two processes never run one job.
- **Restarts.** The transfer records its phase as it goes. A job found running from an earlier process goes
  straight back to the queue when its destination was never touched (and a resumable transfer continues
  from its staged bytes); otherwise it becomes `Interrupted` for a person to decide.
- **States.** `Queued`, `Running`, `Paused`, `Completed`, `Failed`, `Cancelled`, `Blocked` (with
  `BlockReason` `Trust` — an untrusted host key or certificate — or `Credential`), `NeedsReconciliation`
  (the job's `LastReport` says what state it left), and `Interrupted`. Only transient failures are retried,
  with exponential backoff and jitter that honours a server's `Retry-After`.
- **Control.** `Pause`/`Resume` the queue, or `PauseJobAsync`/`ResumeJobAsync` one job (a running resumable
  transfer keeps its staged data), `CancelAsync`, `RetryAsync`, `RemoveAsync`, integer priorities with
  `SetPriorityAsync`, and `MoveUpAsync`/`MoveDownAsync`.
- **History and events.** `MaxFinishedJobs` caps history and `ClearAsync(states)` clears by state. Progress
  events are throttled (`ProgressInterval`), and `EventContext` raises `JobChanged`/`ProgressChanged` on a
  UI thread. The event bus receives started, completed, failed, cancelled, retrying, blocked, and
  needs-reconciliation events.
- **Adaptive concurrency.** With `AdaptiveConcurrency`, the queue starts with one transfer, adds one after
  each success, and halves after a transient failure, up to `MaxConcurrentTransfers`.
- `EnqueueDownloadAsync` takes `StorageDownloadOptions`, so a job can fetch an exact version or a range.

### Compare and sync

```csharp
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
(two-second tolerance) and can add checksums. Sync directions:

- `Update` copies new and changed files and never deletes; it will not replace a newer destination
  that has the same size.
- `Mirror` makes the destination match the source, deleting extra items with `DeleteExtraneous`.
- `TwoWay` copies each file toward the side where it is missing or older, without deletes.

Copied files keep the source's modification time where the destination supports it. On services
that cannot (S3, Azure, GCS, Swift) a copy is newer than its source, and "changed" means "source
newer", so repeated syncs stay no-ops. Per-file failures are collected in `Failed`. `DryRun` returns
the plan without changing anything.

### Raw commands and free space

With `AllowRawCommands: true` on an FTP or SFTP connection, `ExecuteCommandAsync` sends a raw FTP
command (`SITE ...`, `SYST`) or runs an SSH shell command as the connection's account. It is off by
default because commands are not confined to the connection's `Root`. A rejected command or non-zero
exit is returned as a result with `Succeeded = false`, not as an error. Servers that allow only SFTP
(`ForceCommand internal-sftp`) refuse shell commands.

`GetSpaceAsync` reports free and used space: SFTP through `statvfs@openssh.com`, FTP through `AVBL`
where the server implements it, and local connections from the volume. WebDAV and object stores
return `storage.unsupported`.

### Watching for changes

```csharp
await foreach (var change in media.WatchAsync("incoming", cancellationToken: stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

Local connections use native file-system notifications (including renames). Every other provider
is polled: the directory is listed every `PollInterval` (30 s by default) and compared by type, size,
time, and ETag, so a rename appears as a delete plus a create. A failed poll is retried on the next
interval rather than reported as deletions. The library's own staging items never appear.
Connections from `GetStorage()` watch natively too. If notifications arrive faster than they can be
buffered, a `StorageChangeKind.Overflow` change for the watched directory is reported: list it again.

### Links in transfers

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

## Server-side checksums

`ComputeChecksumAsync` and `VerifyChecksumAsync` ask the server for a stored digest first and only
download the content when there is none; `StorageChecksum.Source` says which happened.
`GetServerChecksumAsync` returns only the server's value, and `StorageChecksumMode.ComputeOnly`
forces a download.

| Provider | Server digest |
|---|---|
| S3 | MD5 from single-part, non-KMS ETags; SHA-256 when stored with the object |
| Azure Blob | MD5 (`Content-MD5`) |
| Google Cloud Storage | MD5 of non-composite objects |
| Swift | MD5 ETag, except segmented large objects |
| FTP | `HASH`/`XMD5`/`XSHA256`/`XSHA512` when the server offers them |
| SFTP, WebDAV, Local | none (always computed) |

## Runtime connections, health, and native clients

```csharp
await storage.AddOrUpdateConnectionAsync("backup", new SftpConnectionConfig
{
    Host = "sftp.example.com",
    Username = "backup",
    AuthenticationMode = SftpAuthenticationMode.PrivateKey,
    PrivateKeyPath = @"C:\keys\backup_ed25519",
    HostKeyFingerprints = ["SHA256:..."]
});

Result health = await storage.CheckConnectionHealthAsync("backup");
```

Runtime changes can be persisted to the typed provider JSON section or installed for the process only
with `persist: false`. Stable service proxies and active download/native leases keep an old backend
alive until in-flight operations drain during replacement or shutdown.

Reusable native SDK clients and scoped session clients remain available as an escape hatch:

```csharp
IAmazonS3 s3 = storage.GetNativeClient<IAmazonS3>("media");

var opened = await storage.OpenNativeConnectionAsync<AsyncFtpClient>("legacy-ftp");
if (opened.IsSuccess)
{
    await using var lease = opened.Value!;
    AsyncFtpClient ftp = lease.Client;
}
```

Do not dispose reusable clients returned by `GetNativeClient`; dispose session leases.

### Runtime-only mode

Applications that manage connections themselves can run the library without configuration files:

```csharp
var storage = new StorageLibrary(new StorageLibraryOptions { RuntimeOnly = true });
// after the CodeLogic lifecycle has started it:
await storage.AddOrUpdateConnectionAsync("partner", sftpSettings);
```

No `config.storage*.json` section is registered, read, or written, no default connection is required,
and `persist` only updates the in-memory copy. Library-wide settings can be passed as
`StorageLibraryOptions.Settings`.

### Testing settings and diagnosing connections

`TestConnectionAsync` tries settings without saving them, even before the library is initialized.
It reports each step (`validate`, `connect`, `list`, `details`). If the server's certificate or host
key is rejected, `ServerIdentity` still says what was presented, ready to pin:

```csharp
var report = await storage.TestConnectionAsync(settings);
if (!report.Succeeded && report.ServerIdentity is { Kind: "ssh-host-key" } key)
{
    // Ask the user: "The server presented {key.Fingerprint} ({key.Algorithm}). Trust it?"
    settings.HostKeyFingerprints = [key.Fingerprint];
}
// For TLS, pin key.PublicKeyFingerprint in TrustedPublicKeySha256 (survives certificate renewal).
```

`GetConnectionDiagnosticsAsync(id)` describes a registered connection: host, port, transport security,
the presented certificate or host key, what was negotiated (`tls`/`cipher` for FTPS; `kex`, `hostKey`,
`cipher`, `mac` for SSH), server system and software (FTP `SYST`, SSH version string, HTTP `Server`),
advertised features (FTP `FEAT`, WebDAV `DAV` and `Allow`), session pool counters, and the last health
check. `GetConnections()` includes the host, port, security, and last health for every connection.
Diagnostics never contain credentials.

Health checks publish `StorageConnectionHealthChangedEvent` when a connection's state changes (and on
its first check). Every failed service operation publishes `StorageOperationFailedEvent` with the
operation, path, and error code, including expected failures such as `storage.not_found`.

## Failures and compatibility

Expected failures use stable `storage.*` error codes such as `storage.not_found`,
`storage.conflict`, `storage.authentication_failed`, `storage.permission_denied`,
`storage.connection_lost`, `storage.server_busy`, `storage.quota_exceeded`, `storage.too_large`, and
`storage.unsupported`. `StorageErrorInfo.IsTransient` tells whether a failure is worth retrying, and
`StorageErrorInfo.TryGetDetail` exposes the provider's own code (`ftpReply`, `sftpStatus`, `httpStatus`).
Incomplete cleanup/source deletion is reported as `storage.partial_failure` with sanitized state and
error codes. Provider bodies, credentials, and signed query strings are not exposed. Caller
cancellation propagates as `OperationCanceledException`.

Migrating from the legacy S3-only package? See [MIGRATION.md](MIGRATION.md).

## Requirements

- CodeLogic 4
- .NET 10

MIT license.
