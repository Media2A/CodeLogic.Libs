# Changelog

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
