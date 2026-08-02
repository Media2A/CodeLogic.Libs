# Changelog

## Unreleased

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

- Recursive service copy/move now always uses the safe coordinator; same-provider file staging remains
  server-side when the backend advertises a safe native copy.
- Object-provider listing distinguishes an exact file path from a virtual directory and treats root
  directory creation as an idempotent no-op.
- S3 uploads use explicit bounded multipart handling for seekable and non-seekable streams.
- Google Cloud downloads stream through a bounded pipe and listings use real provider paging.
- WebDAV metadata-read capability is now callable through `IStorageMetadataService`; property writes
  remain explicitly unsupported by the portable adapter.
