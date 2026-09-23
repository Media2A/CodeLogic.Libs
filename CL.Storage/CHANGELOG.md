# Changelog

## Unreleased (since 4.8.93)

Everything below is relative to the published **4.8.93**. Types and members that 4.8.93 never shipped
are listed under *Added*, even where they changed while this release was being built.

> **Release as 4.9, not as another 4.8.x (recommendation).** This release breaks source and binary
> compatibility with 4.8.93 (see *Changed (breaking)*). The repository publishes every library as
> `<version.txt>.<CI run>` and pins `AssemblyVersion` to `Major.Minor.0.0`, so today it would ship as
> `4.8.<run>` with the same `AssemblyVersion` 4.8.0.0 as 4.8.93. Then:
>
> - a project that floats on `4.8.*`, or a bot that takes patch updates, picks it up silently;
> - an assembly compiled against 4.8.93 (another package, or a plugin) still loads, and fails at runtime
>   with `MissingMethodException` or `MissingFieldException` the first time it calls a changed member,
>   for example `StorageLibrary.CopyAsync`, which now returns `StorageTransferReport` instead of `Result`.
>
> Bumping to 4.9 makes the break visible where it can be: floating `4.8.*` ranges and patch updaters do
> not pick it up, and the version says "read the migration guide". It does **not** make .NET refuse to load
> the assembly: .NET (Core) binds a reference to 4.8.0.0 to 4.9.0.0 without complaint, so dependants
> must be rebuilt either way, and packages that depend on CodeLogic.Storage should declare `[4.9, 4.10)`.
> Because `version.txt` is shared by every library in this repository and tracks the CodeLogic framework
> line, the owner has to choose between bumping the whole repository to 4.9, or giving CL.Storage its own
> major.minor in the pack step (the CI's `-p:Version=` override must then use it too). `version.txt` is
> not changed here.

### Changed (breaking)

**Copy and move**

- `StorageLibrary.CopyAsync` and `MoveAsync` return `StorageTransferReport` instead of `Result`. It has
  `IsSuccess`, `IsFailure`, `Error`, and `ToResult()`. Cancelling is reported
  (`Outcome = Cancelled`, `storage.cancelled`) instead of thrown, and so are a token cancelled before the
  call, an unknown connection id (`storage.not_found`), and a provider that throws.
  `IStorageService.CopyAsync`/`MoveAsync` on a connection still return `Result` and throw
  `OperationCanceledException` on a cancel, as in 4.8.93.
- Once a destination is committed it is never rolled back. A failure after the commit (for example a
  backup that could not be removed) is reported: `Completed` with `BackupLeftBehind` or
  `StagingLeftBehind`, and a provider error carrying `destinationState=complete` is a committed transfer.
  A destination that does not hold the committed length is `NeedsReconciliation` even without `Verify`.
- A directory move deletes its source file by file, each only while it is still the version listed, then
  removes the emptied folders without recursion. Files added or changed on the source during the move stay,
  and the move is `NeedsReconciliation` (`sourceChanged=N;sourceAdded=N`) where it used to delete the whole
  source. `SourceDeleted` is `true` only when the whole source is gone.
- A directory moved onto an existing directory on the same connection merges through the relay. Before,
  Local refused it and FTP, SFTP, and WebDAV replaced (deleted) the existing directory; those providers'
  own `MoveAsync` now refuses it with `storage.conflict`.
- A move deletes its source only while it is still the version that was copied; a native move on S3, Azure,
  Google Cloud, and Swift copies that version and deletes only it (never recursively).
- A move or resume no longer treats a destination of the same size as already complete: both sides must
  report the same digest.
- `StorageConflictPolicy.Resume` resumes a staging object and promotes it when complete, instead of
  appending to the destination in place; without `Append` it rewrites from the start instead of failing
  with `storage.unsupported`. Uploads need `SourceIdentity` or `SourceLastModified` to resume
  (`UploadFileAsync` sets both), otherwise `storage.invalid_content`. Copies resume only from sources
  with an ETag, time, or version.
- `Rename` names: the first free name is taken, `name (1).txt` goes on to `name (2).txt` (it used to
  become `name (1) (1).txt`), `file.` becomes `file. (1)`, and a folder at a candidate name counts as taken.
- Validation is stricter: an upload `Condition` together with a conflict policy other than `Overwrite`, and
  empty `SourceVersionId`/`ExpectedSourceETag`, are refused.
- FTP and SFTP overwrites no longer download and re-upload a backup of the old file (their replace renames
  it aside). A relay within one FTP connection with `Session.MaxSessions = 1` fails at once with
  `storage.unsupported` (`requiredSessions=2;maxSessions=1`) instead of timing out.
- `UploadDirectoryAsync`/`DownloadDirectoryAsync`: a cancelled transfer whose rollback failed returns a
  failed result (`storage.partial_failure`) instead of throwing.

**Transfer queue**

- `CreateTransferQueue` is replaced by `OpenTransferQueueAsync`. `Enqueue*` and every control method are
  asynchronous and return results: `Cancel`, `Retry`, `RetryFailed`, and `ClearFinished` became
  `CancelAsync`, `RetryAsync`, `RetryFailedAsync`, and `ClearAsync`.
- Job ids are strings (they were `Guid`), also on `StorageTransferStartedEvent`, `CompletedEvent`, and
  `FailedEvent`. Priorities are integers, higher first; `StorageTransferPriority` is gone.
- `StorageTransferJob` is no longer a positional record: its constructor and `Deconstruct` are gone, its
  properties are get-only views of `job.Record`, and `EnqueuedAt`/`FinishedAt` are on `job.Record`.
- `RetryDelay` became `RetryBaseDelay` and `RetryMaxDelay` (exponential backoff with jitter).
  `AutomaticRetries` defaults to 3 (it was 2).
- Disposing the queue leaves queued jobs queued in the store instead of cancelling them. Removing a job
  raises `JobRemoved` instead of a `JobChanged` with `Cancelled`.
- Finished jobs are pruned beyond `MaxFinishedJobs` (1,000 by default).
- Authentication and trust failures end `Blocked` instead of `Failed`; a `storage.partial_failure` ends
  `NeedsReconciliation`. `FailedJobs` and `RetryFailedAsync` cover `Failed` jobs only.
- `StorageTransferState` keeps its 4.8.93 numbers (`Queued` 0, `Running` 1, `Completed` 2, `Failed` 3,
  `Cancelled` 4); `Paused`, `Blocked`, `NeedsReconciliation`, and `Interrupted` follow them. Exhaustive
  switches need the new states. Every public enum now spells out its numbers.

**Sync and compare**

- Cancelling `ApplySyncAsync` or `SyncAsync` once it has started applying no longer throws
  `OperationCanceledException`: it returns a success whose `report.Cancelled` is `true` (steps not started
  are `NotRun`, and a two-way baseline is still saved). Cancelling while planning still throws.
- `StorageSyncAction` is a record with named properties and no `Error` (its constructor and `Deconstruct`
  changed). `StorageSyncReport` is no longer positional (its constructor and `Deconstruct` are gone);
  `Actions` and `Unchanged` are get-only and `Actions` lists the planned steps, conflicts included. Outcomes are in `report.Results`, and
  `report.Failed` is a list of `StorageSyncActionResult`.
- `TwoWay` without a baseline reports differing files as conflicts (`Block` by default) instead of letting
  the newer one win; set `ConflictPolicy = NewerWins` for the old behaviour.
- `Update` and `Mirror` never replace a newer destination with an older source, even when the sizes differ.
- `Mirror` with `DeleteExtraneous` deletes an extra folder file by file and then the emptied folders, and
  withholds all deletes after any failed copy. It never deletes a destination path the source left out (a
  link, a hidden item, anything under an excluded folder).
- Everything below a path that is a file on one side and a folder on the other is left alone.
- `StorageCompareOptions.LinkHandling = Recreate` is refused.
- Plans: applying a plan with options for another `SyncId`, for other connections, in an older plan format,
  or with options other than the plan's (only `MaxConcurrency`, `ItemRetries`, `ContinueOnError`,
  `DryRun`, `Progress`, and `StateStore` may change) is refused; two applies of one `SyncId` in a process
  run one after the other.
- A tree over `MaxItems` (1,000,000 by default) fails the plan.
- `CompareAsync` fails on names that differ only by case on a case-insensitive side instead of returning
  two entries.
- An `Exclude` pattern that matches a folder excludes its contents, as in `.gitignore`; `IncludeHidden =
  false` leaves out hidden folders with their contents.
- A kept `cl-mtime` without an offset is read as UTC, and a kept time is always used.
- Conflict copies of `a.tar.gz` are named `a (conflict x).tar.gz`.

**Listings, providers, and connections**

- Recursive listings on S3, Azure Blob, and Swift include folders that exist only as key prefixes, so item
  counts change. They are sorted per page, and an inferred folder can repeat on a later page.
- `IncludeHidden = false` also leaves out the contents of hidden folders within one listing.
- Local items have a weak ETag, `W/"<write time>-<creation time>-<length>"` (it was null in 4.8.93).
  Sync conflict-copy names built from it therefore change.
- On Windows only symbolic links and junctions are links; other reparse points (OneDrive placeholders,
  deduplicated files, app execution aliases) are files and folders. Recursive local listings no longer
  descend into links to folders.
- Swift no longer declares `ConditionalUpdate` or `ConditionalDelete` (the server ignores `If-Match` on
  writes); conditions are checked just before instead. WebDAV no longer declares `AtomicMove`.
- `CopyAsync`/`MoveAsync` on a backend return `storage.unsupported` for pins or destination conditions they
  cannot enforce (FTP, SFTP, WebDAV, Local for `SourceVersionId`) instead of ignoring them.
- A PKCS#12 client certificate without its private key fails registration.
- A TLS stream that fails after the handshake is `storage.connection_lost` (transient), no longer
  `storage.tls_failure`; `client_certificate_rejected` needs proof that this connection's certificate was
  refused.
- FTP and SFTP registrations with identical settings share one session pool, so `MaxSessions` caps them
  together. Listing continuation tokens on Local, FTP, SFTP, and WebDAV are tied to the settings instead of
  the connection id (a listing over 250,000 items is not kept, so its token is walked again).
- `StorageChangeKind` gained `Overflow`; exhaustive switches need the new case.

### Added

- **Transfer reports** (`StorageTransferReport`, `StorageTransferOutcome`, `StorageSkipReason`,
  `StorageConditionEnforcement`): outcome, skip reason, written path, digest, destination ETag/version, how
  a condition was enforced, and exactly what an unfinished transfer left (`DestinationCommitted`,
  `SourceDeleted`, `StagingLeftBehind`, `BackupRestored`, `BackupLeftBehind`, `ResumeToken`).
- **Guaranteed single-file transfers**: `DestinationCondition`, `SourceVersionId` (needs `Versioning`),
  `ExpectedSourceETag`, `ExpectedSourceLength`, `Verify`, and `ExpectedSha256` on `StorageTransferOptions`;
  `ExpectedLength`, `Verify`, `ExpectedSha256`, and `SourceIdentity` on `StorageUploadOptions`. Content is
  staged, checked, confirmed, and promoted; the destination condition is handed to the provider's move.
- **Staged resume** with `StorageResumeToken`, which survives restarts; one writer per staging object.
- **`OpenWriteAsync`**: a push-style write stream with `CommitAsync` and `AbortAsync`;
  `StorageWriteException` carries the storage error when the destination stops accepting data.
- `StorageItem.Sha256`; `StorageTransferOptions.PreScan`; `FilesCompleted`/`FilesTotal` on progress.
- **Durable transfer queue**: `StorageTransferJobSpec` (jobs as data), `IStorageTransferJobStore` with
  revisions, conditional removal (`RemoveAsync(jobId, expectedRevision, lease)`), `ReleaseAsync`,
  store-owned leases and fencing; `StorageTransferJobRecord` with `ToJson`/`FromJson` and `IsReadable`;
  restart rules from recorded phases; idempotent caller-chosen ids; `Paused`, `Blocked`,
  `NeedsReconciliation`, and `Interrupted`; `PauseJobAsync`/`ResumeJobAsync`, `SetPriorityAsync`,
  `MoveUpAsync`/`MoveDownAsync`, `RemoveAsync`, `ClearAsync(states)`; exponential backoff honouring
  `Retry-After`; throttled progress; `EventContext`; adaptive concurrency; `ShutdownTimeout`;
  `JobRemoved`; cancelled, retrying, blocked, needs-reconciliation, and interrupted bus events
  (`StorageTransferInterruptedEvent`). Stopping the library disposes the queues it opened.
- `StorageErrors.Cancelled` (`storage.cancelled`, never transient; classify by code, not by the Core error
  kind). `StorageErrors.Create` rebuilds an error from a stored code, message, and details;
  `StorageErrorInfo.DestinationCommitted`, `DestinationStateKey`, and `LeftBehindKey`.
- **Three-way sync**: a baseline store (`IStorageSyncStateStore`), a classifier per side, `BothModified`/
  `BothCreated`/`DeleteVersusModify` conflicts with `Block`, `KeepBoth`, and `NewerWins`, deletions through
  the baseline, `PlanSyncAsync`/`ApplySyncAsync` with an approvable plan (`SchemaVersion`, connection ids,
  `OptionsDigest`), deletion safety (`MaxDeletes`, `MaxDeletePercent`, empty sides), include/exclude globs,
  case-collision refusal (`StorageFeature.CaseInsensitivePaths`), `cl-mtime`, budgeted parallel hashing,
  per-item retries (`ItemRetries`), continue-or-stop, `report.BaselineError`, and link handling.
- `StorageLibraryOptions.RuntimeOnly`: no configuration section is registered, read, or written; the
  settings passed in are copied.
- `tlsReason` on `storage.tls_failure` (`server_certificate_rejected` with the presented certificate,
  `client_certificate_rejected`, `protocol_mismatch`, `handshake_failed`), and `connection_interrupted` as a
  hint on `storage.connection_lost`.
- `ClientCertificateContent` on FTP and WebDAV connections: the client certificate as bytes. On Linux the
  key stays in memory; on macOS .NET keeps it in a temporary keychain; on Windows it goes into a
  non-persisted key container deleted with the connection.
- `S3ConnectionConfig.ConditionalRequests` (`Auto`, `Enforced`, `NotEnforced`): how far to trust an
  S3-compatible server's conditional requests; `Auto` probes copies and deletes once per connection.
- `StorageSessionConfig.LingerSeconds`; `StorageWatchOptions.Incremental`, `FullRescanEvery`, and
  `PollFailed`; `StorageChangeKind.Overflow`.
- Download progress carries `TotalBytes` even without a `Length`.
- Continuous integration runs the mutual-TLS tests on Windows (SChannel) too.

### Fixed

- `Mirror` copied an older source over a newer destination of the same size; its deletes ran even after
  copies failed; a folder deleted as extraneous took excluded files with it.
- Sync looked up each action with a linear search inside the copy loop (quadratic).
- Recursive listings on S3, Azure Blob, and Swift left out folders that exist only as key prefixes; Google
  Cloud repeated inferred folders after every page; a file named like a folder hid the folder.
- Connections from `GetStorage()` never watched natively; the native watcher dropped changes silently on
  overflow.
- FTP could not find dot-files on servers without MLST that hide them from `LIST` (vsftpd); they are found
  with `SIZE`/`MDTM`/`CWD` or a hidden-files listing, and `LIST -a` falls back to `LIST`.
- SFTP `AppendAsync` failed on a missing file.
- S3 metadata read back with an `x-amz-meta-` prefix; a `CompleteMultipartUpload` whose answer was lost
  failed over the object it had committed; SSE-C ETags were taken for MD5.
- Swift declared conditional updates and deletes, but the server ignores `If-Match`, so an upload with a
  wrong ETag overwrote the object.
- WebDAV treated `207 Multi-Status` on `COPY`/`MOVE` as success, and dropped items it listed in the
  server's own spelling of the root.
- Moving a directory onto an existing one on FTP, SFTP, or WebDAV deleted the existing directory.
- Windows reparse points that are not links (OneDrive placeholders, deduplicated files) were treated as
  links and skipped.

## 2026-09-22

### Fixed

- Swift items never carried an `ETag`, because Swift sends it unquoted and the typed header parser
  rejects that, so every conditional delete against Swift failed as a conflict.
- FTP listing times were shifted by the client machine's UTC offset: the FTP client now converts to
  UTC, and unspecified times are no longer reinterpreted as local time.
- WebDAV uploads, moves, and copies each stranded a pooled HTTP connection until garbage collection,
  because the WebDAV client library never disposes those responses. With a connection limit the next
  request hung; without one, sockets piled up under load. The backend now issues MOVE and COPY itself.
- Legacy code-page encodings (windows-1252, iso-8859-x, ibm437, shift_jis) were unavailable because
  .NET does not register them by default.
- A Google Cloud object whose timestamp had fewer than three fractional digits failed the whole
  operation with `storage.provider_error`; timestamps are now parsed leniently.
- Unclassified provider failures now carry the exception type (never its message) in `Details`.
- Ranged Google Cloud Storage downloads failed hash validation, because the stored CRC32C covers the
  whole object; validation is now skipped for byte ranges only.
- Google Cloud clients whose credentials cannot sign URLs (anonymous or emulator clients) threw
  from the backend constructor instead of just disabling signed URLs.
- WebDAV failures were all reported as `storage.provider_error`, and missing-directory detection never
  matched: the HTTP status is read from `WebDAVException.GetHttpCode()` (with a message fallback)
  instead of `ErrorCode`, which the client leaves at zero.
- FTP server replies wrapped in FluentFTP's generic `FtpException` are now unwrapped and classified.
- On Linux and macOS, WebDAV items whose names need URL escaping (spaces, for example) were reported
  as missing right after being written: server-relative hrefs were parsed as file paths.
- Public-key (SPKI) pin checks disposed the server certificate they were given, so anything reading
  it later in the TLS callback saw a disposed certificate.

### Changed (breaking)

- `UploadWithProgressAsync` no longer hides seeking, so uploads with progress can be retried.

- `DownloadToFileAsync` gained a `conflictPolicy` parameter before `cancellationToken`.

- Listings no longer show the library's own staging and backup items (`.cl-storage-*`,
  `.clstorage-*`); set `IncludeInternal` to see them, for example to clean up after a crash.

- `ComputeChecksumAsync` and `VerifyChecksumAsync` gained a `mode` parameter before
  `cancellationToken`; positional callers passing a token must name it.

- FTP, SFTP, and WebDAV now declare `AtomicMove`, so renaming a folder through the library uses a
  single server-side rename (RNFR/RNTO, SFTP rename, WebDAV MOVE) instead of copying the whole tree
  through the client and deleting the original.

- SFTP no longer requires `HostKeyFingerprints` when `KnownHostsPath` is set.

- `StorageConnectionInfo` gained `Host`, `Port`, `Security`, and `LastHealth`. Positional
  deconstruction is unchanged; code comparing whole records for equality now also compares these.

- WebDAV always installs a certificate validation callback (to record the presented certificate).
  Without pins it still accepts only certificates with no policy errors.

- `FtpStorageBackend`, `SftpStorageBackend`, and `WebDavStorageBackend` constructors take optional
  session and retry settings. Retries are on by default (3 attempts); pass
  `new StorageRetryConfig { RetryCount = 0 }` to restore single-attempt behavior.

- Split coarse failures into precise error codes: `storage.authentication_failed`,
  `storage.permission_denied`, `storage.tls_failure`, `storage.host_key_rejected`,
  `storage.connection_failed`, `storage.connection_lost`, `storage.server_busy`, and
  `storage.quota_exceeded`. Code that compared against `storage.unauthorized` or
  `storage.unavailable` for provider failures must also accept the new codes; see `MIGRATION.md`.
- FTP errors are now classified from the server reply code (421, 425/426, 450/550, 452/552,
  530, 553, ...) instead of collapsing to `storage.provider_error`. TLS failures are no longer
  reported as credential failures.
- SFTP reports an untrusted host key as `storage.host_key_rejected` with the presented fingerprint
  in `Details`, and distinguishes refused connections, dropped sessions, and too-many-sessions.
- WebDAV, S3, Azure Blob, Google Cloud Storage, and Swift share one HTTP status mapping: 401 vs 403
  are distinguished, 429/503 become `storage.server_busy` (carrying `retryAfterMs` when the server
  sent `Retry-After`), and 507 becomes `storage.quota_exceeded`.
- A full local disk now returns `storage.quota_exceeded`, and local access denial returns
  `storage.permission_denied`.

### Added

- `TestConnectionAsync` checks connection settings that have not been saved: it validates them,
  connects, lists the root, and reads server details, reporting each step. When a certificate or host key
  is rejected, the report still carries what the server presented so a setup screen can offer to pin it.
- `GetConnectionDiagnosticsAsync` describes a live connection: address, transport security, the
  presented certificate or host key, negotiated TLS/SSH algorithms, server system and software, FTP
  `FEAT` or WebDAV `DAV`/`Allow` features, session pool counters, and the last health check.
- `StorageConnectionHealthChangedEvent` when a health check finds a connection in a new state, and
  `StorageOperationFailedEvent` for every failed service operation (with operation, path, and code).
- `WatchAsync` change streams: native notifications for local connections (`ChangeNotifications`),
  polling with snapshot diffs for every other provider.
- `IStorageCommandService.ExecuteCommandAsync` for raw FTP and SSH commands (opt-in with
  `AllowRawCommands`) and `IStorageSpaceService.GetSpaceAsync` for SFTP, FTP (`AVBL`), and local.
- Directory `CompareAsync` and `SyncAsync` (`Update`, `Mirror` with optional deletes, `TwoWay`) across
  any two connections, with dry runs, timestamp preservation, checksum comparison, and parallel copies.
- `StorageLibrary.CreateTransferQueue`: a background queue with global and per-connection concurrency,
  priorities, pause/resume, cancellation, automatic re-queueing of transient failures, a retryable
  failed list, progress and state events, and started/completed/failed bus events.
- `Progress` on upload, download, and transfer options, with speed, remaining time, and the current
  file; relayed directory transfers report one running total.
- Per-connection `TransferLimits` and library-wide `MaxTotalUploadBytesPerSecond` /
  `MaxTotalDownloadBytesPerSecond` speed limits, shared by concurrent transfers.
- `IStorageAppendService.AppendAsync` (Local, FTP, SFTP), `StorageConflictPolicy.Resume` for
  interrupted uploads and `DownloadToFileAsync`, and `CleanupStaleStagingAsync`.
- `StorageConflictPolicy` (`Fail`, `Overwrite`, `Skip`, `OverwriteIfNewer`, `OverwriteIfSizeDiffers`,
  `OverwriteIfNewerOrSizeDiffers`, `Rename`) on uploads, transfers, directory uploads/downloads, and
  `DownloadToFileAsync`, with `SkippedFiles` in directory reports and partial moves that keep skipped
  sources.
- `StorageListOptions.IncludeInternal`, `IncludeHidden`, and `NamePattern` (`*`/`?` wildcards).
- `StorageTransferOptions.LinkHandling` (`Reject`, `Skip`, `Follow`, `Recreate`) for relayed
  transfers that meet symbolic links.
- Deleting a local link removes the link itself, even when `FollowLinks` is off, and never its target.
- `IStorageChecksumService.GetServerChecksumAsync` for S3, Azure Blob, Google Cloud Storage, Swift, and
  FTP. `ComputeChecksumAsync`/`VerifyChecksumAsync` use the server digest when available (new
  `StorageChecksumMode` parameter) and report it in `StorageChecksum.Source`.
- `IStorageAttributeService` with permissions (including recursive file/directory modes), numeric
  ownership, timestamps, and symbolic links, plus `StorageItem.UnixMode`, `Permissions`, `Owner`,
  `Group`, `OwnerId`, `GroupId`, `LinkTarget`, `Created`, `LastAccessed`, and `IsHidden`, and
  `UnixPermissions` for octal and `rwx` conversion.
- WebDAV: Digest, NTLM, and Negotiate authentication, public-key (SPKI) pins,
  `RequireValidCertificateChain`, client certificates for mutual TLS, and `MaxConnectionsPerServer`.
  Basic credentials are sent up front instead of after a 401 challenge.
- FTP/FTPS: public-key (SPKI) pins, `RequireValidCertificateChain`, revocation checks, TLS version
  selection, `EncryptDataChannel`, active-mode port range and external IP, file-name `Encoding`
  (including legacy code pages), ASCII `TransferType`, `ListingParser`, `ServerTimeZone`, separate
  connect/read/data timeouts, `SocketKeepAlive`, and `LoginCommands` run after each login.
- SFTP: `KeyboardInteractive` and `Auto` authentication (keys, password, keyboard-interactive, and
  multi-method servers), inline private keys (`PrivateKeyContent`), several keys, OpenSSH
  `known_hosts` verification (hashed, wildcard, `[host]:port`, and `@revoked` entries), algorithm
  allow-lists, file-name `Encoding`, `BufferSize`, and `JumpHost` tunnelling through an SSH bastion.
- HTTP, SOCKS5, and SOCKS4 proxy support (`Proxy`) for FTP (including data connections), SFTP,
  WebDAV, S3, Azure Blob, Google Cloud Storage, and Swift.
- Google Cloud Storage `ServiceUrl` and `AllowInsecureHttp` for private endpoints and emulators, plus
  an `Anonymous` authentication mode for public buckets and fake-gcs-server.
- Swift `TempAuthV1` authentication (`X-Auth-User` / `X-Auth-Key`) and `AllowInsecureHttp`.
- FTP, SFTP, and WebDAV retry transient failures (timeouts, refused or dropped connections, busy
  servers) with exponential backoff, jitter, and `Retry-After` support, configured per connection
  through `Retry`. Deletes and moves retry only with `RetryNonIdempotent`; uploads retry only from
  seekable streams.
- FTP and SFTP session pools are configurable per connection through `Session`: `MaxSessions` caps
  open sessions and makes excess callers wait (`storage.server_busy` on timeout), idle sessions are
  probed before reuse, and sessions that fail mid-operation are retired instead of reused.
- `Session.KeepAliveSeconds` enables FTP NOOP and SSH keep-alive packets.
- `StorageConnectionOpenedEvent`, `StorageConnectionLostEvent`, and `StorageConnectionRetryEvent`,
  plus a warning log line per retry.
- `StorageErrorInfo` with `IsTransient`, `IsConnectionFault`, `TryGetRetryAfter`, and `TryGetDetail`
  for retry decisions and provider diagnostics (`ftpReply`, `sftpStatus`, `httpStatus`).

### Added

- Added mounted local/UNC, S3-compatible, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud
  Storage, and OpenStack Swift backends behind one `IStorageService` contract.
- Added typed provider configuration, runtime add/update/remove persistence, stable service proxies,
  per-connection health, custom-backend registration, native clients, scoped native sessions, and
  asynchronous library disposal.
- Added granular `StorageFeature` flags and provider limits for page, object, metadata, batch, and
  multipart boundaries.
- Added bounded cross-connection file and recursive-directory copy/move, plus rollback-safe local
  directory upload/download reports.
- Added page/item async enumeration and bounded order-preserving batch info/delete/copy/move helpers.
- Added file, text, JSON, progress, and streaming checksum convenience APIs.
- Added optional metadata, object-tag, signed-URL, and object-version contracts. S3 and Azure Blob
  support bounded tag reads plus merge/replace updates. S3, Azure Blob, and GCS support exact version
  listing/deletion; S3, Azure, and signing-capable GCS credentials support temporary URLs.
- Added atomic ETag/version upload and delete conditions where providers can enforce them.
- Added typed write/delete/copy/move and cross-connection completion events.
- Added a migration guide from `CodeLogic.StorageS3`.

### Safety

- Added an explicit, default-off `AutoAcceptHostKey` SFTP connection option for trusted
  environments where a server host-key fingerprint cannot be configured.
- Accept SSH.NET's canonical unpadded Base64 SHA-256 host-key fingerprints while continuing to
  reject malformed, noncanonical, or non-SHA-256 values before SFTP trust decisions.
- Centralized path normalization and source/destination relationship checks; equal transfers and
  directory moves/copies below their source are rejected by the library and direct backends.
- Staged local, FTP, SFTP, and WebDAV overwrites so a failed upload cannot truncate existing data.
- Made recursive transfers use unique staging objects, preserve caller upload streams, hold registry
  leases, and cap relay read-ahead at 1 MiB.
- Back up pre-existing destination files and restore them when a later directory item fails. Incomplete
  cleanup or source deletion returns sanitized `storage.partial_failure` state.
- Propagate caller cancellation and keep provider response/session ownership attached to returned
  download streams.
- Enforce byte-buffering, metadata, multipart, serialization, and batch limits.
- Removed certificate and host-key bypass behavior. Clear-text custom endpoints require explicit opt-in;
  SFTP host-key trust is mandatory; FTPS/WebDAV certificate pins use SHA-256.
- Reject header injection, transport-managed custom headers, unsafe endpoint URL components, malformed
  metadata, and unsupported version/metadata options instead of silently ignoring them.
- Sanitized public provider errors so credentials, signed query strings, raw response bodies, and
  provider exception messages are not exposed.

### Changed

- FTP, SFTP, WebDAV, and local listings now page from a single cached, ordinally sorted snapshot per
  listing pass instead of re-materialising and re-sorting the whole listing on every page. Paging a
  recursive listing of N entries at page size P cost `ceil(N/P)` complete directory walks and is now
  one walk; ordering and item content are unchanged. Continuation tokens remain opaque and are still
  rejected when malformed, but their internal format changed, so a token minted by an earlier version
  is not accepted by this one. A token is only meaningful within the listing that minted it;
  presenting one to a different listing resumes that listing from the path the token carries rather
  than failing, because an evicted snapshot cannot be told apart from a foreign one.
- FTP and SFTP reuse pooled, already-authenticated sessions across operations rather than opening and
  tearing down a connection per call. Idle sessions are health-checked before reuse, bounded in count
  and idle lifetime, and closed when the backend is disposed. Sessions handed out through
  `OpenNativeConnectionAsync` are retired rather than pooled, since caller code may leave them in an
  unexpected state.
- Recursive service copy/move now always uses the safe coordinator; same-provider file staging remains
  server-side when the backend advertises a safe native copy.
- Object-provider listing distinguishes an exact file path from a virtual directory and treats root
  directory creation as an idempotent no-op.
- S3 uploads use explicit bounded multipart handling for seekable and non-seekable streams.
- Google Cloud downloads stream through a bounded pipe and listings use real provider paging.
- WebDAV metadata-read capability is now callable through `IStorageMetadataService`; property writes
  remain explicitly unsupported by the portable adapter.

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.
