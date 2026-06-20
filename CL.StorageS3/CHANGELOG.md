# CL.StorageS3 — Changelog

All notable changes to **CodeLogic.StorageS3** are documented here. Versions follow
[Semantic Versioning](https://semver.org/).

## [4.5.2] — 2026-06-20

### Documentation

- Rewrote the storage guide and Quick Start to match the real `S3StorageService`
  API: every operation returns `Result` / `Result<T>` (existence checks return
  `bool`), uploads/downloads take `UploadOptions` / `DownloadOptions`, and the
  config file is `config.storages3.json` with camelCase keys and a `connections`
  array.
- Documented previously undocumented surface: `GetService` / `DefaultService`
  connection access, `CopyObjectAsync`, `GetObjectInfoAsync`,
  `GetObjectStreamAsync`, byte-range and `VersionId` downloads, `UploadOptions`
  fields (cache-control, content-disposition, storage class, public-read ACL,
  metadata), paginated `ListObjectsResult`, the `ObjectUploadedEvent` /
  `ObjectDeletedEvent` / `BucketCreatedEvent` events, low-level
  `ConnectionManager.GetClient` access, and the per-connection health check
  (Healthy / Degraded / Unhealthy).

## [4.5.0] — 2026-05-24

### Changed

- **Unified versioning.** All CodeLogic.Libs now share a single version line
  controlled by `version.txt` in the repo root. This is a version alignment
  release — no functional changes to this library.
## [4.0.4] — 2026-04-16

### Changed

- README + manifest refresh for the v4 baseline. No functional changes vs 4.0.3.
- `LibraryManifest.Version` now reads from assembly metadata.

## [4.0.2] — 2026-04-09

### Added

- `DisablePayloadSigning` option on `S3ConnectionConfig` for S3-compatible
  endpoints (Backblaze B2, MinIO with signing off, etc.) that don't accept
  signed payloads.

### Changed

- Annotated S3 configuration with `[ConfigField]` for the admin UI surface.
- Aligned with the v4 baseline across all libraries.

## [4.0.0] — 2026-04-09

Major rewrite. Republished as v4.0.0 to reset the version line under the
unified v4 baseline. S3-compatible object storage with multipart upload,
presigned URLs, and lifecycle helpers.

### Notes

- Earlier history is retained in the
  [git log](https://github.com/Media2A/CodeLogic.Libs/commits/main/CL.StorageS3).
