# Changelog

## 2026-09-12

### Changed

- Unified the version line with the CodeLogic framework on **4.8.x**. Every official
  library and the framework now share one `major.minor`, so a given `4.8.<patch>`
  means the same generation across all packages.
- `version.txt` moved from `4.6` to `4.8`. The patch component remains the CI run
  number, composed at pack time; `AssemblyVersion` stays pinned at `Major.Minor.0.0`
  (now `4.8.0.0`) so every patch in the line loads interchangeably.

## Unreleased

### Fixed

- Ranged Google Cloud Storage downloads failed hash validation, because the stored CRC32C covers the
  whole object; validation is now skipped for byte ranges only.
- Google Cloud clients whose credentials cannot sign URLs (anonymous or emulator clients) threw
  from the backend constructor instead of just disabling signed URLs.
- WebDAV failures were all reported as `storage.provider_error`, and missing-directory detection never
  matched: the HTTP status is read from `WebDAVException.GetHttpCode()` (with a message fallback)
  instead of `ErrorCode`, which the client leaves at zero.
- FTP server replies wrapped in FluentFTP's generic `FtpException` are now unwrapped and classified.

### Changed (breaking)

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
