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
using CodeLogic;

var init = await CodeLogic.CodeLogic.InitializeAsync();
if (init.ShouldExit) return;
await Libraries.LoadAsync<StorageLibrary>();
await CodeLogic.CodeLogic.ConfigureAsync();   // the class CodeLogic.CodeLogic, not the namespace
await CodeLogic.CodeLogic.StartAsync();

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
  The reason comes from the platform's own TLS stack — SChannel status codes on Windows, OpenSSL alerts on
  Linux — including a TLS 1.3 server that refuses the client certificate only after the handshake (only
  when a connection that was asked for a certificate failed with evidence of a refusal, and only for the
  attempt that used that connection; behind an HTTP proxy tunnel it is not detected). A stream that breaks
  after the handshake is
  `storage.connection_lost`, which is transient, with `tlsReason=connection_interrupted` as a hint.
- A client certificate is read from `ClientCertificatePath`, or from `ClientCertificateContent` (the PFX
  bytes, base64 in JSON) when it comes from a secret store; `ClientCertificatePassword` decrypts either.
  It is loaded once per connection and disposed with it. On Linux the private key is held in memory only.
  macOS does not support in-memory keys, so .NET imports the key into a temporary keychain that it deletes
  when the certificate is disposed with the connection. On Windows SChannel needs a key container: the key
  goes into a non-persisted container that is deleted when the connection is disposed; a process that
  crashes can leave that container file behind. Only when the user key store is unavailable (the profile
  is not loaded, as for some service accounts) is the machine-wide key store used, whose containers the
  machine's administrators can read; a wrong password never falls back. A PKCS#12 file without its private
  key is refused when the connection is registered.
- Legacy encodings such as `windows-1252`, `iso-8859-1`, `ibm437`, and `shift_jis` are supported for
  file names on older servers.
- `ServerTimeZone` converts listing times from servers that report local time.
- `LoginCommands` run after every login; a command the server rejects fails the connection so
  misconfiguration surfaces immediately.

### WebDAV options

`AuthenticationMode` accepts `None`, `Basic` (sent up front, saving a challenge round trip),
`BearerToken`, `Digest`, `Ntlm`, `Negotiate`, and `Windows` (current user). HTTPS endpoints support
`TrustedCertificateSha256` and `TrustedPublicKeySha256` pins, `RequireValidCertificateChain`, and a
PFX `ClientCertificatePath` for mutual TLS. `MaxConnectionsPerServer` caps concurrent connections. A `MOVE` or `COPY` onto an existing
folder is refused (`storage.conflict`) rather than replacing it; a `207 Multi-Status` answer, where some
members failed, is `storage.partial_failure` (`destinationState=partial`). WebDAV does not declare
`AtomicMove`, so the library relays folder moves there. An upload onto an existing folder is refused too
(the destination is read just before the `MOVE`, which leaves a short unguarded window). A non-recursive
folder delete locks the collection and deletes it only while empty; a server without WebDAV locks answers
`storage.unsupported` and the folder is left. FTP removes an empty folder with a raw `RMD`, and a recursive
FTP delete includes hidden files.

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
  `storage.server_busy`. A relayed copy within one FTP or SFTP connection needs two sessions; with
  `MaxSessions = 1` it fails at once with `storage.unsupported`. Renames and moves on the server need one.
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
- Registrations with identical settings, under any id, share one pool while they coexist, so
  `MaxSessions` applies to them together. When the last one is removed, its sessions close at once,
  unless `LingerSeconds` (0 to 3600, default 0) is set: then they stay open that long, and a
  registration added again with the same settings picks up the warm sessions. Leave it at 0 for servers
  with a strict per-user connection limit. Idle pools are closed when the library stops.
- Listing continuation tokens are tied to the settings rather than the registration id, so paging
  continues after the same settings are registered again under a new id. A token refers to a listing
  snapshot kept in the process for five minutes after its last use; after that, or in another process, the
  listing is walked again from where the token points. A listing of more than 250,000 items is not kept,
  so its token is always walked again. A pool keeps a copy of the settings it was created with.
- S3-compatible servers differ in which conditional headers they enforce. `ConditionalRequests = Auto`
  (the default) probes once per connection, when a conditional request first needs it, whether
  `PutObject`, `CopyObject`, and `DeleteObject` enforce their conditions (AWS does; MinIO enforces them on
  `PutObject` only, so a create-only copy there is checked just before and reported
  `CheckedBeforeCommit`). A condition found ignored or rejected is not sent. Until the probe has run the
  conditional capability flags are provisional; afterwards they name only what is enforced, and
  `GetConditionEnforcementAsync` asks directly. An inconclusive probe is retried after a back-off from 1 to
  32 minutes. The probe writes and deletes two `.cl-storage-probe-*` objects (it needs
  `PutObject`/`DeleteObject`; it leaves versions and delete markers on versioned buckets and undeletable
  versions under Object Lock, and fires notifications and replication). `Enforced` trusts everything;
  `NotEnforced` sends no conditional headers and has the library check conditions just before each write.
  Server-side copies above 5 GiB use parts of at least 128 MiB.

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
Recursive listings on S3, Azure Blob, Google Cloud, and Swift include folders that exist only as key
prefixes; they are sorted page by page and an inferred folder can appear again on a later page, so build
a tree by path. `IncludeHidden = false` also leaves out what hidden folders hold, on every page of a paged
listing.
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
(nothing committed), `NeedsReconciliation` (a mixed state), or `Cancelled` (the caller cancelled) — and, for a single file, the path
actually written (`WrittenPath`, which differs after `Rename`), the bytes, the SHA-256 when verified,
and the destination's new `DestinationETag`/`DestinationVersionId`. When a transfer does not finish, the
state fields say exactly what it left: `DestinationCommitted`, `SourceDeleted`, `StagingLeftBehind`,
`BackupRestored`, `BackupLeftBehind`, and a `ResumeToken`. `SourceDeleted` is `true` only when the whole
source of a move is gone. A token cancelled before the call, an unknown connection id, or a provider that
throws also come back as a report rather than an exception.

Cross-provider data uses a `System.IO.Pipelines` relay capped at 1 MiB. Each destination file is
uploaded to a unique staging name and committed only after the complete source stream succeeds.
Existing destination files are backed up (on FTP and SFTP the provider's replace renames them aside; other
destinations without a server-side copy have them renamed aside) and restored if a later directory item
fails. That rollback deletes or restores a committed file only while it is still the version this transfer
committed (a conditional request where the provider enforces one, otherwise a comparison just before); a
file changed meanwhile, or whose committed version is unknown, is left and reported. A single file's
committed destination is never rolled back: a leftover backup is reported (`Completed` with
`BackupLeftBehind`), and a destination that does not hold the committed length is `NeedsReconciliation`,
with the previous version kept and named in `BackupLeftBehind`. A cancel after the commit is not
`Cancelled`: a copy is `Completed`, and a move whose source is still there is `NeedsReconciliation`
(`sourceItemsDeleted=N` for a directory move stopped while deleting); the copy event is published either
way.

A move deletes its source only after the entire destination commits, file by file and only while each
file is still the version that was listed; files added or changed during the move stay, and the move is
`NeedsReconciliation`. Equal paths and a directory destination below its source are rejected.

The normal `IStorageService.CopyAsync` and `MoveAsync` methods use the same coordinator for recursive
work (and throw `OperationCanceledException` on a cancel, like every connection call). Safe
same-provider file copies remain server-side when the provider can guarantee them; on object stores a
native move copies the version it read and deletes only that version. A move that cannot be pinned (WebDAV,
or a source without ETag or version) compares the source just before the server's own move and reports
`CheckedBeforeCommit`. `ConflictPolicy = Rename` picks the free name first and then uses the server's
rename. Folder renames on one FTP, SFTP, or local connection are a single server-side rename when the
destination does not exist (the report counts the files by listing the destination); onto an existing
folder the transfer merges through the relay. A relay within one FTP or SFTP connection needs
`Session.MaxSessions` of at least 2.

Local directory trees can be transferred without manually registering a temporary local connection:

```csharp
Result<StorageDirectoryTransferReport> upload = await storage.UploadDirectoryAsync(
    @"C:\exports\2026", "archive", "yearly/2026");

Result<StorageDirectoryTransferReport> download = await storage.DownloadDirectoryAsync(
    "archive", "yearly/2026", @"C:\restore\2026");
```

Links (symbolic links and junctions) in a local upload are rejected rather than followed; other Windows
reparse points, such as OneDrive placeholders, are ordinary files. Reports contain file, directory, and
byte counts.

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
  otherwise by reading it back) before it is promoted. `VerifiedBy` says which. After promotion the
  destination is confirmed again by its length and, where the server keeps one, its SHA-256.
- `DestinationCondition` is checked before the copy starts and again right before promotion, then handed
  to the provider's move. `ConditionEnforcement` reports how: `Atomic` when the provider enforced it in the
  committing request (create-new on Local, WebDAV, Azure, Google Cloud, and S3 servers that enforce
  `If-None-Match` on `CopyObject`; replace-only-this-version on Azure, Google Cloud, and S3 servers that
  enforce `If-Match` on `CopyObject`), otherwise `CheckedBeforeCommit` (FTP, SFTP, Swift, a version
  condition on Local and WebDAV, MinIO). When the condition fails at promotion, the destination is left
  exactly as the other writer left it, and a destination deleted meanwhile is not brought back. Where
  each provider checks just before (and so leaves a short window) is tabled under Guaranteed transfers in
  `docs/libs/storage/transfers.md`; `files.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly)`
  asks a connection.
- `SourceVersionId` reads that version, and length, ETag, and a move's source deletion refer to it: moving an
  older version fails to delete the source (`NeedsReconciliation`), because the current object is another
  version.
- A source pinned by ETag is read again after streaming; a change in between fails with
  `storage.conflict` and nothing is committed.
- A move deletes its source only while it is still the version that was copied: with a conditional delete
  where the provider enforces one (Azure, Google Cloud, AWS S3), otherwise by comparing it just before
  (Local, FTP, SFTP, WebDAV, Swift, MinIO). A changed source is kept, and the report is
  `NeedsReconciliation` with `SourceDeleted = false`.
- Local files carry a weak ETag (`W/"…"`: last-write time, creation time, and size), so conditions work on
  local connections too, checked right before the file is replaced. On coarse file systems (FAT 2 s,
  exFAT, HFS+ 1 s, some SMB/NFS shares) or after a tool restores file times, two versions can share it.
  It is weak: a different ETag proves a change, a matching one never proves there was none (such checks
  fall through to size and time).
- Cancelling a copy or move returns a report with `Outcome = Cancelled` (error `storage.cancelled`)
  rather than throwing: staging and backup objects are removed, the destination is as it was, and a
  resumable transfer's `ResumeToken` continues it. A cancel that lands after the commit does not turn a
  finished transfer into `Cancelled`.
- `ExpectedSha256` implies `Verify`, before and after the commit.

### Streamed writes

`OpenWriteAsync` returns a stream to write a file's content into, for push-style producers:

```csharp
var opened = await files.OpenWriteAsync("exports/data.csv", new StorageUploadOptions { Verify = true });
await using var writer = opened.Value!;
await writer.WriteAsync(chunk);
Result<StorageItem> committed = await writer.CommitAsync(); // or AbortAsync(); disposing without commit aborts
```

With `Verify`, the committed item carries the content's `Sha256`, as verified uploads do. Disposing
without committing aborts without waiting (the staging object is removed in the background);
`DisposeAsync` and `AbortAsync` wait for it.

Nothing appears at the destination until `CommitAsync` commits. A failed `CommitAsync` leaves the
destination as it was unless its error carries `destinationState=complete`: the content was committed but
does not read back as written, or the provider left an internal object behind, named by `leftBehind`
entries (staged uploads report the same way). At most 1 MiB is buffered, so a slow
destination slows the writer down. Conflict policies, `Condition`, `ExpectedLength`, `Verify`, and
`ExpectedSha256` apply as for uploads; conflict decisions happen before the first byte is written. A
write that fails or is cancelled aborts the stream (its bytes may already be buffered); when the
destination stopped accepting data it throws `StorageWriteException`, whose `Error` says why.

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
| Local / UNC | physical | no | no | create (weak ETags) | no | no |
| S3-compatible | virtual | read/write | read/write | yes/yes/yes (per server: `ConditionalRequests`; provisional until probed) | read/list/delete | read/write |
| FTP / FTPS | physical | no | no | no | no | no |
| SFTP | physical | no | no | no | no | no |
| WebDAV | physical | discovered properties are read-only | no | create | no | no |
| Azure Blob | virtual | read/write | read/write | yes/yes/yes | read/list/delete | SAS when credentials permit |
| Google Cloud Storage | virtual | read/write | no portable contract | yes/yes/yes | read/list/delete | when signing credentials permit |
| OpenStack Swift | virtual | read/write | no portable contract | create (update/delete checked just before) | endpoint-specific/native | no portable TempURL contract |

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

An upload's condition is atomic on S3 (conditional uploads, which AWS and MinIO enforce), Azure Blob,
and Google Cloud Storage; on every other provider the content is staged and the condition checked
immediately before the staged file replaces the destination. A conditional delete is atomic on Azure,
Google Cloud, and S3 servers that enforce `If-Match` on `DeleteObject`, checked immediately before on
Swift and MinIO, and refused with `storage.unsupported` on Local, FTP, SFTP, and WebDAV.

Creating only when absent (`Overwrite = false`) is atomic for uploads where `StorageFeature.ConditionalCreate`
is declared: Local, WebDAV, S3, Azure Blob, Google Cloud Storage, and Swift. FTP and SFTP do not declare
it, because their protocols have no atomic create-if-absent; a caller that must not race should check the
flag and refuse. Copies and moves report their own `ConditionEnforcement`. Local overwrites are staged too: the new content is written to a temporary file in the
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
`OverwriteIfSizeDiffers`, `OverwriteIfNewerOrSizeDiffers`, and `Rename` (writes the first free name
`name (1).ext`, `name (2).ext`, …; a folder counts as taken, and `name (1).ext` goes on to `name (2).ext`).
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
- Moving a directory deletes only the source files that were transferred; skipped files stay, and
  `SourceDeleted` is `false`. Moving a single link with `LinkHandling = Skip` skips it (`SkipReason = Link`).
- "Newer" allows two seconds of clock slack. `UploadFileAsync` supplies the local file's time; for
  stream uploads set `SourceLastModified`. Unknown times or sizes count as newer or different.
- `DownloadToFileAsync` takes a `conflictPolicy` for the local file.
- Conditional policies on copy and move are applied by `StorageLibrary` and its connections, not by a
  backend's own `CopyAsync`/`MoveAsync`.

### Resume and append

`ConflictPolicy = Resume` writes through a resumable staging object (`.cl-storage-part-…`) and replaces
the destination only once it is complete, so the destination is never half-written. If an attempt fails,
the staged bytes stay; the next attempt for the same destination and the same source reads only the
missing tail and appends it (FTP `APPE`, SFTP append, local files).

- **The same source only.** Staged bytes are keyed by the source's identity, so a different source of
  the same length never continues them. Uploads need a seekable stream and `SourceLastModified`, or a
  `SourceIdentity` marked `SourceIdentityIsContentVersion = true` (a hash, ETag, or version id);
  `UploadFileAsync` sets the path and the time. A `SourceIdentity` alone is refused
  (`storage.invalid_content`): a path stays the same when a file is edited. Copies and moves identify the
  source by its ETag, time, or version, and do not resume a source that has none; a source with only a weak
  ETag (Local) resumes only with `Verify`.
- **One writer.** A staging object is held by one transfer at a time, across processes too, through a
  create-only lock marker beside it (`<part file>.lock`), until it is promoted. Another transfer of the same
  source to the same destination stages privately. A marker whose owner is gone is taken over (same
  machine: once its process no longer runs; another machine: after 24 hours); where no marker can be
  created, the transfer stages privately and is not resumable.
- **Tokens.** A failed or cancelled copy's report carries a `ResumeToken` that can be stored and passed
  back in `StorageTransferOptions.ResumeToken`, even from another process after a restart. A token is
  followed only for exactly the same source and only onto its own staging object; otherwise the transfer
  starts again, and nothing the token names is deleted. A token does not mean overwrite: with
  `Overwrite = false` or `ConflictPolicy = Fail` it fails validation.
- **Integrity.** With `Verify`, the part staged earlier is read again from the source and must match
  before the rest is appended, so the digest covers the whole file.
- **Already complete.** A destination is only treated as complete when both sides report the same digest;
  equal sizes are not enough. A resumed move whose destination is complete deletes its source.
- Where the destination cannot append (object stores), the staging object is rewritten from the start.
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
- **Durable, shared store.** `IStorageTransferJobStore` (in memory by default) holds every job. Every record
  has a `Revision`, and saves are compare-and-swap on it, so a stale copy never overwrites a newer state
  (a job finished elsewhere cannot be re-queued by a priority change here). Workers claim a job at the
  revision they read, with a lease carrying a fencing token that the store owns, and renew it while
  running; a save with a stale lease is refused, so a worker that lost its lease never records an outcome,
  and two processes never run one job.
- **Restarts.** The transfer records its phase as it goes, and the phase never goes backwards. A job found
  running from an earlier process goes straight back to the queue when its destination was never touched
  (and a resumable transfer continues from its staged bytes); otherwise it becomes `Interrupted` for a
  person to decide. File uploads and downloads always count as touched once running. A process restarted
  with the same `WorkerId` takes back its own leases at once; use a distinct `WorkerId` per running queue.
- **A failing store.** Store calls are guarded: a failed claim is tried again a moment later, renewals and
  saves are retried while the lease still holds, and a transfer whose outcome cannot be recorded is
  recovered later instead of being lost. A store failure does not use up `AutomaticRetries`; attempts it
  stops are retried with a delay doubling from `RetryBaseDelay`, and after 8 in a row the job fails with
  `storage.unavailable`. A transfer
  that succeeded is recorded `Completed` even if it was being paused, cancelled, or shut down as it
  finished, and a move that committed stays `NeedsReconciliation` whatever arrives afterwards.
- **Writing a store.** The store's clock alone decides lease expiry; fencing tokens and revisions only ever
  grow for a job id, even across removal and re-adding; removals take the expected revision and lease;
  `ReleaseAsync` ends a lease without a save; keep `SchemaVersion` (or use
  `StorageTransferJobRecord.ToJson`/`FromJson`). Records from a newer schema are left alone: a JSON store
  skips rows it cannot read in `LoadAsync` (and may return null from `GetAsync`), and a store with its own
  revision and lease columns returns the columns' values, not the copies inside the JSON.
- **Shutdown.** Disposing the queue, or stopping the library, waits up to `ShutdownTimeout` (30 s) for
  running transfers, which end `Queued` or `Interrupted` by their phase, never `Failed`, and for the
  queue's background work. Once `DisposeAsync` returns the store is not used again (an attempt that
  outlived the timeout records nothing and is recovered once its lease lapses), so it may be closed. The
  library's synchronous `Dispose()` blocks while its queues stop. A queue that finishes opening while the
  library stops is disposed, and `OpenTransferQueueAsync` fails with `storage.unavailable`.
- **States.** `Queued`, `Running`, `Paused`, `Completed`, `Failed`, `Cancelled`, `Blocked` (with
  `BlockReason` `Trust` — an untrusted host key or certificate — or `Credential`), `NeedsReconciliation`
  (the job's `LastReport` says what state it left), and `Interrupted`. Only transient failures are retried,
  with exponential backoff and jitter that honours a server's `Retry-After`.
- **Control.** `Pause`/`Resume` the queue, or `PauseJobAsync`/`ResumeJobAsync` one job (a running resumable
  transfer keeps its staged data), `CancelAsync`, `RetryAsync` (which also resets `Attempts`), `RemoveAsync`,
  integer priorities with `SetPriorityAsync` for waiting jobs, and `MoveUpAsync`/`MoveDownAsync`. Control
  calls on a running job return once its transfer has stopped, successfully only if they took effect, or
  with `storage.timeout` after `ControlTimeout` (30 s; zero returns once the request is recorded), in which
  case the request still applies when the attempt stops. Moving a job among others that share its order
  makes room first instead of failing. A null job id is a failed result.
  `FailedJobs` and `RetryFailedAsync` cover `Failed` jobs; retry `Blocked`, `NeedsReconciliation`, and
  `Interrupted` jobs one by one with `RetryAsync`.
- **History and events.** `MaxFinishedJobs` (1,000 by default) caps history and `ClearAsync(states)` clears
  by state. Progress events are throttled (`ProgressInterval`), and `EventContext` raises `JobChanged`/
  `ProgressChanged`/`JobRemoved` on a UI thread; without it handlers run outside the queue's locks, and must
  not wait synchronously on the queue's methods. The event bus receives started, completed, failed,
  cancelled, retrying, blocked, needs-reconciliation, and interrupted events.
- **Adaptive concurrency.** With `AdaptiveConcurrency`, the queue starts with one transfer, adds one after
  each success, and halves after a transient failure, up to `MaxConcurrentTransfers`.
- `EnqueueDownloadAsync` takes `StorageDownloadOptions` (its `options` parameter, before `conflictPolicy`),
  so a job can fetch an exact version or a range. A retried job keeps the highest phase an earlier attempt
  reached.

### Compare and sync

```csharp
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
`LocalStorageBackend` over a local folder.

**Directions.**
- `Update` copies new and changed files and never deletes. It never replaces a newer destination with an
  older source; if the two also differ in size, the plan warns about it.
- `Mirror` makes the destination match the source; it deletes extra items when `DeleteExtraneous` is set,
  but likewise leaves a newer destination alone.
- `TwoWay` changes both sides, directories included:
  - With a baseline (`StateStore` + `SyncId`), it is a three-way sync. An edit or deletion on one side is
    carried to the other (`PropagateDeletes`), and changes on both sides are conflicts: `BothModified`,
    `BothCreated`, or `DeleteVersusModify`. A folder removed on one side is removed on the other only once
    everything inside it goes too.
  - Without a baseline, missing files and folders are copied and files that differ are conflicts.
- A path that is a file on one side and a folder on the other is left alone, with everything below it.
- Where one side ignores case, a name spelled differently on each side (`Readme.TXT` / `readme.txt`) is
  one item, and each side keeps its own spelling, folders included (an empty folder too).

**Conflict policies.**
- `Block` (the default) plans a conflict. The plan cannot be applied until the conflict is resolved,
  unless `ApplyWithConflicts` is set.
- `KeepBoth` keeps the source's version under the original name, and keeps the destination's version on
  both sides as `name (conflict xxxxxxxx).ext`, named from its identity (`… 2`, `… 3` when that name is
  already taken).
- `NewerWins` applies only when you ask for it; a modification beats a deletion, and two versions with the
  same time are left alone without blocking the rest.

**Plan, then apply.**
- The plan lists every step with the versions it depends on. It serializes with `ToJson()`, and
  `Digest` is a SHA-256 over its content, including its `SchemaVersion`, both connection ids, and
  `OptionsDigest`.
- `ApplySyncAsync` refuses a plan that is not the one approved, one made for other connections or in an
  older format, and options that differ from the plan's (only `MaxConcurrency`, `ItemRetries`,
  `ContinueOnError`, `DryRun`, `Progress`, `StateStore`, `ApplyWithConflicts`, and
  `Compare.HashConcurrency` may change at apply). Plans from earlier preview builds are refused: plan again.
- A plan is bound to connection ids only, so keep ids stable between planning and applying. The baseline is
  bound to `SyncId` only: give each pair of folders its own.
- It also refuses a plan whose baseline moved on because another run saved in between.
- Each step first re-checks its items. A step whose item changed since planning is reported `Stale`
  and not taken. Deletes, overwrites, and conflict renames require exactly the planned version (no time
  tolerance), so on FTP servers without `MLSD` (times listed to the minute) they can end `Stale`.
- Planning fails, rather than throws, for a provider whose listing throws or a filter pattern that runs too
  long.
- `DryRun` returns the plan without changing anything.

**Deletion safety.** Deletions are withheld, and listed in `plan.Warnings` and `report.Withheld`, when:
- they would exceed `MaxDeletes` or `MaxDeletePercent`;
- a side is unexpectedly empty, as when a drive is not mounted or a root is wrong (unless
  `AllowEmptySide`);
- any other step failed in the same run.

A listing that fails part-way fails the plan. Folders are never deleted recursively: their files are
deleted one by one, each checked at apply time, and then the emptied folders. A folder that gained items
after the plan, or holds items the filters left out, is kept; the library's own staging older than 24 hours
does not keep it (it is removed first). A path the source left out (a link, a hidden item, anything below
an excluded folder) is never deleted from the destination, and nothing is written onto or through what the
destination left out.

**The baseline.** After a two-way run, the baseline records the versions both sides agreed on when the plan
was made and the versions the run itself wrote, never a listing taken afterwards. A file edited during or
just after a run is therefore still seen as changed next time. A step that failed, was withheld, or went
stale keeps its previous entry, so the next run tries again, and so do paths that are excluded, hidden,
or under a file/folder clash (entries below an excluded folder go once neither side has that folder). Neither side having changed while their content differs is reported as a
conflict, not as in sync.

**Copies.** Each copy goes through a staged write that is conditional on the planned versions (create-new,
or replace-only-that-version), pinned to the planned source, and optionally verified (`Verify`, which
confirms the destination and records the SHA-256 in the baseline). Copied files keep the source's
modification time; on object stores that cannot set times, the time is kept in `cl-mtime` metadata (read
as UTC when it has no offset) and used by later comparisons. A failed copy restarts from the beginning.

**Comparison.**
- Size and time are compared by default (two-second tolerance). `CompareBy.Checksum` uses the server's
  digest where there is one.
- Otherwise it hashes in parallel (`HashConcurrency`) within `MaxHashedFiles`/`MaxHashedBytes`, taken for
  both sides at once. Beyond that budget, or when one file cannot be read, a difference is `Undecidable`;
  one-way sync leaves such a pair alone while size and time agree. Each side uses a digest its server
  keeps where it can, so only the other side is downloaded.
- `Include`/`Exclude` globs (`**` spans folders) apply to both sides and to the baseline. Excluding a folder
  excludes its contents, as in `.gitignore`.
- Names that differ only by case are refused on a case-insensitive side (`CaseInsensitivePaths`, or
  `CaseInsensitive`) rather than letting the last copy win; one collision fails the whole comparison.
- `LinkHandling` skips links by default. `Follow` filters and compares a link as what it leads to inside the
  connection, listing a followed folder with its target's contents (up to 8 links deep; cycles are left
  out); copies read through it (`StorageSyncAction.ReadPath`), and nothing is deleted or replaced through
  a followed link.
- `MaxItems` (1,000,000 by default) caps the size of a tree.

**Runs.** Transient failures are retried per step (`ItemRetries`). `ContinueOnError = false` stops at the
first failure. A provider or state store that throws fails its step or is reported in
`report.BaselineError`.

**Cancelling.** Once `ApplySyncAsync` or `SyncAsync` has started applying, cancelling does not throw
`OperationCanceledException` (4.8.93 did): the result is a success whose `report.Cancelled` is `true`,
`Results` says what was applied, and steps not started are `NotRun`. A two-way sync with a baseline still
saves it: completed steps are recorded and every other path keeps its previous entry. A successful result
therefore does not mean the sync finished; check `report.Cancelled`. Cancelling while planning, or while
waiting for another apply of the same `SyncId`, still throws.

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
interval rather than reported as deletions (and passed to `PollFailed`); a failed first listing, or one
cut short by a folder vanishing mid-poll, is retried rather than reported as mass creates or deletes. The
library's own staging items never appear.
Connections from `GetStorage()` watch natively too. If notifications arrive faster than they can be
buffered, a `StorageChangeKind.Overflow` change for the watched directory is reported: list it again. If
native watching stops for good (the folder was removed, a network share dropped) or cannot start,
`Overflow` is reported and watching continues by polling.

Polling a large remote tree is cheaper with `Incremental = true`: after the first listing, a poll lists
only the root and the folders whose modification time changed (entries were added, removed, or renamed
in them), and reuses the previous listing for the rest. Every `FullRescanEvery` polls (10 by default)
the whole tree is listed again, which catches edits to existing files and changes deep inside folders
whose own time did not change. Where folders have no times (object stores), listing folder by folder
would cost more than one recursive listing, so every poll lists everything there.

```csharp
var options = new StorageWatchOptions { Recursive = true, Incremental = true, FullRescanEvery = 20 };
await foreach (var change in partner.WatchAsync("outbox", options, stopping))
    Console.WriteLine($"{change.Kind}: {change.Path}");
```

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
| S3 | MD5 from single-part ETags of objects without SSE-KMS or SSE-C; SHA-256 when stored with the object |
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

Result<HealthStatus> health = await storage.CheckConnectionHealthAsync("backup");
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
Incomplete source deletion or restore is reported as `storage.partial_failure` with sanitized state and
error codes; a failure raised after the destination was written carries `destinationState=complete`
(`StorageErrorInfo.DestinationCommitted`). Classify errors by code, not by the Core error kind
(`storage.cancelled` is never transient). Provider bodies, credentials, and signed query strings are not
exposed. Caller cancellation propagates as `OperationCanceledException` from connection calls and sync
planning; `StorageLibrary.CopyAsync`/`MoveAsync` report it (`Outcome = Cancelled`), and a sync cancelled
while applying returns a success with `report.Cancelled = true`.

Migrating from the legacy S3-only package? See [MIGRATION.md](MIGRATION.md).

## Requirements

- CodeLogic 4
- .NET 10

MIT license.
