# CL.Storage vNext Maximum-Function Extension Plan

**Status:** Implemented and offline-verified; live credential validation and legacy-package retirement remain release gates
**Date:** 2026-08-02
**Package:** `CodeLogic.Storage` / `CL.Storage`
**Compatibility posture:** The package is unreleased; deliberate breaking changes are allowed when they remove unsafe or misleading behavior.

## Outcome

Deliver one production-ready storage package that:

- exposes a coherent provider-neutral API for local, S3, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift;
- supports safe same-connection and cross-connection file and directory transfers;
- never reports a capability that the active backend does not implement;
- preserves cancellation, caller-owned upload streams, bounded memory, and stable provider-neutral errors;
- defaults to strict transport and server-identity verification;
- offers advanced provider functionality through capability-gated optional interfaces instead of bloating the common contract;
- has deterministic offline contract tests and a clearly documented live-provider release gate;
- provides the migration path needed to retire `CL.StorageS3` after live S3 parity validation.

## Non-negotiable invariants

1. Never delete or truncate the source before a move destination is complete.
2. Reject equal source/destination paths and directory destinations nested below their source.
3. `Overwrite=false` must use an atomic provider condition when available; otherwise document and expose that limitation through capabilities.
4. Failed cross-backend uploads must perform best-effort cleanup without deleting data that predated the operation.
5. `OperationCanceledException` propagates; it is never converted into a failed `Result`.
6. Upload streams remain caller-owned and open.
7. Download streams own every provider response/session/registry lease needed until disposal.
8. Byte-buffering APIs enforce their limit before or while buffering and never allocate beyond the declared maximum intentionally.
9. No certificate or host-key “accept any” switch is supported.
10. Secrets, tokens, signed query strings, credentials, and raw provider error bodies never enter logs or public error details.

## Delivered implementation snapshot

- Safe same-connection and cross-connection file/directory copy and move with bounded relay,
  staging, backup, rollback, lease retention, and explicit partial-failure states.
- Granular capabilities/limits; async enumeration; ordered bounded batches; progress, file, text,
  JSON, and streaming checksum helpers; directory import/export; health and runtime registration.
- Atomic mutation conditions where the provider can enforce them; strict rejection elsewhere.
- Optional metadata, tag, signed-URL, and version contracts. Tags are portable for S3-compatible and
  Azure Blob connections; versions are portable for S3, Azure Blob, and GCS.
- Strict transport identity defaults, bounded multipart/streaming paths, caller-owned uploads,
  resource-owning downloads, normalized paths, and sanitized provider errors.
- Deterministic offline coverage and package/migration documentation.

Provider administration, ACL/public-access policy, Azure leases, append-blob semantics, provider
change feeds, and manually controlled multipart sessions remain available through typed native-client
access. They are deliberately not advertised as portable capabilities. Live tests require operator
credentials/endpoints and are a release-environment gate; the legacy `CL.StorageS3` source remains
until those parity checks are complete.

## Target architecture

### Common contract

Retain the basic `IStorageService` operations and add shared helpers for:

- page-by-page and item-by-item asynchronous enumeration;
- metadata updates;
- batch info/delete/copy operations with per-item results;
- conditional create/update/delete using provider-neutral version conditions;
- checksums and integrity verification;
- progress reporting;
- explicit file-versus-directory transfer semantics.

The common surface remains bucket/container/root mounted. Provider administration such as bucket creation, lifecycle policies, or IAM remains optional/provider-specific.

### Capability model

Replace ambiguous booleans with granular feature flags and limits. At minimum describe:

- real or virtual directories and links;
- file and directory copy/move separately;
- server-side versus relayed copy;
- atomic move/replace;
- range reads;
- metadata read and write separately;
- conditional create/update/delete;
- server pagination and maximum page size;
- checksums;
- multipart/resumable upload;
- signed read/write URLs;
- versioning;
- tags, ACLs, leases/locks, append, and change notifications where supported.

Keep convenience properties for the original six capability names during migration, but make them truthful projections of the granular flags.

### Advanced optional interfaces

Use optional interfaces discoverable through the backend/service rather than placing every provider feature on `IStorageService`:

- `IStorageMetadataService`
- `IStorageSignedUrlService`
- `IStorageVersionService`
- `IStorageMultipartService`
- `IStorageTagService`
- `IStorageAclService`
- `IStorageLeaseService`
- `IStorageAppendService`
- `IStorageChangeFeedService`

Every optional interface has its own capability flag and provider-neutral models. Native-client access remains the final escape hatch.

### Transfer coordinator

Add library-level `CopyAsync` and `MoveAsync` overloads accepting source and destination connection IDs.

- Same connection: prefer a safe provider-native operation when capability and item type allow it.
- Different connections or missing native support: relay through a `System.IO.Pipelines.Pipe` capped at 1 MiB.
- Hold both registry operation leases for the complete transfer.
- Run producer and consumer concurrently and cancel the peer on failure.
- Support recursive directory transfer with stable relative-path mapping.
- Track newly created destinations so cleanup cannot delete pre-existing content.
- Delete a move source only after every destination operation succeeds.
- Return structured transfer failure details describing destination/source state without secrets.

## Delivery phases

### Phase 0 — Trustworthy baseline

- Fix stale tests using `ProviderConnectionConfig`/removed `Root` members.
- Remove or obsolete the unused forward-compatible configuration type.
- Run the complete current Storage test suite green.
- Add regression tests for equal-path copy/move, move-to-descendant, cancellation, and caller stream ownership.
- Add a CI gate that builds and tests `CL.Storage` directly.

**Exit gate:** library and tests compile; all baseline tests pass; no known destructive transfer reproduction remains.

### Phase 1 — Transfer and mutation safety

- Add centralized normalized path relationship helpers.
- Guard every direct backend, not only `StorageLibrary`, so publicly constructed backends are safe.
- Replace SFTP delete-before-rename overwrite behavior with safe staging/rename behavior or a conflict result when atomic replacement is unavailable.
- Make local/SFTP/FTP/WebDAV overwrite uploads stage and replace where the provider permits it.
- Normalize paths before publishing events.
- Avoid “deleted” events for `IgnoreMissing` no-ops by returning mutation outcome data internally.
- Preserve cancellation in Azure/GCS implicit-directory lookup and every helper path.

**Exit gate:** shared mutation-safety contract tests pass against local and deterministic fake backends.

### Phase 2 — Bounded cross-connection transfers

- Implement the transfer coordinator and public library overloads.
- Add file and recursive directory copy/move.
- Add bounded-memory, non-seekable, cleanup, cancellation, and registry-drain tests.
- Make same-connection fallback use the same coordinator when native capability is absent.

**Exit gate:** large generated streams prove the 1 MiB relay bound; all failure-ordering tests pass.

### Phase 3 — Contract expansion

- Introduce granular capabilities and provider limits.
- Add page/item async enumeration helpers.
- Add version/ETag conditions to upload, delete, copy, and move options.
- Add checksums, progress, preservation, and cleanup policies.
- Add metadata read/write interface and batch operation models.
- Add per-connection health, `TryGetStorage`, async custom registration, and `IAsyncDisposable` library shutdown.
- Define root, empty-directory, link, range-at-EOF, and file/directory collision semantics in XML/package docs.

**Exit gate:** public API approval tests and the provider-neutral contract suite are green.

### Phase 4 — Provider completion

#### Local/UNC

- Atomic temp-file upload/replace.
- Recursive copy with link policy and cycle detection.
- Optional metadata sidecar or explicitly unsupported metadata write.
- Real timeout behavior only where enforceable; otherwise remove the misleading setting.
- Optional file watching/change feed.

#### S3-compatible

- `TransferUtility` or explicit multipart upload with bounded part concurrency.
- Atomic conditional create (`If-None-Match`) and version-aware mutations.
- Server paging, range validation, metadata replacement, tags, versions, checksums, and signed URLs.
- S3-compatible behavior tests for AWS, MinIO, R2, and custom endpoints.

#### FTP/FTPS

- Strict TLS validation plus optional SHA-256 pins; remove certificate bypass.
- Improve protocol-status-to-storage-error mapping.
- Safe overwrite staging where rename support permits it.
- Recursive relayed directory copy.
- Explicit capability reporting for REST/range, MLSD metadata, hash commands, and atomic rename.

#### SFTP

- Mandatory configured host-key pins.
- Safe overwrite rename without pre-deleting the destination.
- Remote canonical-path checks to prevent symlink escape where the server supports them.
- Recursive relayed directory copy.
- Optional permissions/timestamps and checksum extensions.

#### WebDAV

- HTTPS by default; explicit `AllowInsecureHttp` only for deliberate local/test endpoints.
- Certificate pins and Windows authentication; remove certificate bypass.
- PROPPATCH metadata writes, lock-token support, capability discovery, and conditional ETag operations.
- Verify range support rather than advertising it unconditionally.

#### Azure Blob

- Conditional mutations, metadata/tags, leases, versions/snapshots, checksums, and SAS URLs.
- Poll server-side copies to a verified terminal success state.
- Recursive transfer guards and consistent virtual-directory behavior.

#### Google Cloud Storage

- True provider paging and streaming downloads rather than full-prefix/full-file prebuffering.
- Project/quota-project and timeout/retry configuration.
- Generation/metageneration conditions, resumable upload, checksums, metadata, versions, and signed URLs.

#### OpenStack Swift

- Application-credential and preissued-token authentication.
- A public authenticated `SwiftClient` rather than exposing an unauthenticated raw `HttpClient`.
- Robust marker paging, metadata POST, bulk delete when advertised, segmented large objects, and TempURL support.
- Preserve original provider-neutral errors through helper methods.

**Exit gate:** every advertised feature has deterministic boundary coverage and capability-conditioned contract coverage.

### Phase 5 — Test matrix and observability

- Shared contract suite for all providers.
- Fake/boundary tests for every SDK or HTTP adapter.
- Environment-gated live suites: MinIO/S3, FTP/FTPS, SFTP, WebDAV, Azurite/Azure, GCS, and Swift.
- Fault injection for timeouts, partial reads/writes, expired auth, retries, paging mutation, and disposal.
- Structured metrics for latency, bytes, failures, active transfers, and health without path/secret leakage.
- Concurrency stress tests for replacement, stop, health, native leases, and transfers.

### Phase 6 — Migration and release

- Remove `CL.StorageS3` source, tests, package entries, and old documentation after feature parity is proven.
- Publish a migration guide for APIs, configuration, events, presigned URLs, and native access.
- Replace docs catalog/workflow entries with `CL.Storage`.
- Build, test every repository test project, pack, inspect the NuGet, and validate package metadata.
- Require a clean worktree except intentional changes.

## Provider-neutral API additions under consideration

These names are provisional and must receive tests before production implementation:

```csharp
Task<Result> CopyAsync(
    string sourceConnectionId,
    string sourcePath,
    string destinationConnectionId,
    string destinationPath,
    StorageTransferOptions? options = null,
    CancellationToken cancellationToken = default);

Task<Result> MoveAsync(...);

IAsyncEnumerable<Result<StoragePage>> EnumeratePagesAsync(...);
IAsyncEnumerable<Result<StorageItem>> EnumerateItemsAsync(...);

Task<Result<HealthStatus>> CheckConnectionHealthAsync(
    string connectionId,
    CancellationToken cancellationToken = default);

Task<Result> RegisterBackendAsync(
    string id,
    IStorageBackend backend,
    bool ownsBackend = true,
    CancellationToken cancellationToken = default);
```

## Definition of done

- Zero build warnings and all tests green.
- Every capability is verified by a contract test.
- All public async APIs accept and preserve cancellation.
- No known same-path, descendant-path, partial-overwrite, or failed-move data-loss path.
- Cross-provider transfers are bounded and cleanup-safe.
- No blanket TLS or host-key bypass remains.
- Remote provider behavior is covered offline and has documented live-test commands.
- Documentation and package contents describe the actual implementation, not planned behavior.
- `CL.StorageS3` is retired only after parity and migration documentation are complete.
