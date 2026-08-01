# CL.Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:test-driven-development` for every production behavior. Write each behavior test first, run it to an expected failure, implement the minimum production code, then rerun it to green. Preserve RED and GREEN commands/output in the task report.

**Goal:** Replace the focused `CL.StorageS3` library with one `CL.Storage` library that exposes a small provider-neutral storage API, per-backend JSON configuration, runtime registration/persistence, and typed native-client access.

**Architecture:** Every named connection is mounted at one configured root and implements `IStorageService`. `StorageLibrary` owns a case-insensitive registry, lifecycle, health, native-client escape hatches, and cross-backend transfer orchestration. Strongly typed provider configuration is split across CodeLogic config sections so irrelevant settings never appear in another provider's JSON.

**Tech Stack:** .NET 10, C# 13, CodeLogic 4 `Result`/configuration/events/lifecycle, xUnit 2.9.3.

## Global Constraints

- Work directly in `C:\Users\claus.HLAB-DC\Documents\GitHub\CodeLogic.Libs` on `main`; the user explicitly rejected a worktree.
- Preserve the pre-existing untracked `CL.StorageS3/chrome_pZ52VF8s1o.png`; never stage, edit, move, or delete it.
- Project/folder/assembly/root namespace: `CL.Storage`; NuGet package: `CodeLogic.Storage`; entry point: `StorageLibrary`.
- Target `net10.0`, C# 13, nullable and implicit usings enabled, XML documentation generated.
- Keep one NuGet package containing all providers.
- Common operations use CodeLogic `Result`/`Result<T>`; `OperationCanceledException` must propagate rather than become a failed result.
- Streams are caller-owned and must never be disposed by `CL.Storage`.
- Paths are `/`-separated and relative to a configured mount root. Normalize repeated separators and `.`; reject NUL and any `..` segment. Local/UNC paths must not escape through symlinks/reparse points when `FollowLinks` is false.
- Connection IDs are globally unique and case-insensitive. `Default` is the conventional default.
- Common operations are info, exists, paged list, create-directory, stream/byte upload and download, delete, same-connection copy/move, and cross-connection copy/move.
- Provider-only administration, policies, ACLs, lifecycle rules, and presigned URLs remain native-client operations.
- Direct SMB protocol support is excluded. Mounted UNC/SMB paths use the local provider; do not add SMBLibrary.
- Consumer drives and synchronization/scheduling are excluded.
- Security defaults are strict: HTTPS/FTPS where configured, normal certificate validation, and mandatory configured SHA-256 SFTP host-key fingerprints. Never add an accept-any certificate/host-key option.
- Native reusable clients are library-owned and callers must not dispose them. FTP/SFTP native sessions are returned as `IAsyncDisposable` leases.
- Public SDK types may appear only in provider-specific constructors/native access, never in provider-neutral models/interfaces.
- SDK retries remain SDK-owned; do not layer a generic write retry loop.
- Successful common writes/deletes/copies/moves publish generic events. Event publication failure is logged and never changes the already-completed storage result.
- Use exact package pins stated in the provider tasks.
- Do not modify unrelated libraries except catalog/docs/workflow integration explicitly required by Task 8.

---

### Task 1: Core Contract, Config Models, Path Rules, and Local Provider

Create the package/test scaffolding and the public neutral API. Do not add remote-provider packages yet.

**Files:**
- Create `CL.Storage/CL.Storage.csproj`, `CL.Storage/README.md`, `CL.Storage/CHANGELOG.md`.
- Create focused files under `CL.Storage/src/Abstractions`, `Models`, `Configuration`, `Errors`, and `Providers/Local`.
- Create `tests/Storage.Tests/Storage.Tests.csproj` and focused test files.

**Required public surface:**
- `StorageProvider`: `Local`, `S3`, `Ftp`, `Sftp`, `WebDav`, `AzureBlob`, `GoogleCloudStorage`, `OpenStackSwift`.
- `StorageItemType`: `File`, `Directory`, `Link`.
- Immutable or init-only `StorageItem` with `Path`, `Name`, `ItemType`, nullable `Size`, nullable `LastModified`, nullable `ContentType`, nullable `ETag`, and read-only string metadata.
- `StoragePage` with read-only items and nullable opaque `ContinuationToken`.
- `StorageCapabilities` describing directories, native copy, native move, range reads, metadata, and server pagination.
- Options: `StorageListOptions` (`Recursive`, `PageSize=1000`, `ContinuationToken`), `StorageUploadOptions` (`Overwrite=true`, `CreateParents=true`, `ContentType`, metadata), `StorageDownloadOptions` (`Offset`, `Length`, nullable `MaxBufferedBytes`), `StorageDeleteOptions` (`Recursive`, `IgnoreMissing`), and `StorageTransferOptions` (`Overwrite=true`, `CreateParents=true`). Validate positive sizes/ranges and range overflow.
- `IStorageService` properties: `ConnectionId`, `Provider`, `Root`, `Capabilities`.
- `IStorageService` async methods: `GetInfoAsync`, `ExistsAsync`, `ListAsync`, `CreateDirectoryAsync`, stream `UploadAsync`, byte-array `UploadBytesAsync`, stream `DownloadAsync`, byte-array `DownloadBytesAsync`, `DeleteAsync`, `CopyAsync`, and `MoveAsync`; every method has an optional `CancellationToken`.
- `IStorageBackend : IStorageService, IAsyncDisposable` adds root-scoped `CheckHealthAsync`, typed reusable-client lookup, and typed native-session opening needed by later registry work.
- `NativeConnectionLease<TClient> : IAsyncDisposable` releases exactly once.
- `StorageErrors` factory maps stable codes: `storage.invalid_path`, `storage.not_found`, `storage.unauthorized`, `storage.timeout`, `storage.conflict`, `storage.unavailable`, `storage.unsupported`, `storage.too_large`, `storage.provider_error`.

**Configuration:**
- `StorageConfig` section `storage`: `Enabled=true`, `DefaultConnection="Default"`, `HealthCheckTimeoutSeconds=10`, `MaxBufferedDownloadBytes=67108864`.
- `LocalStorageConfig` section `storage.local`: `Dictionary<string, LocalConnectionConfig> Connections`.
- `LocalConnectionConfig`: `Enabled=true`, required `RootPath`, `FollowLinks=false`, `TimeoutSeconds=30`; include CodeLogic schema metadata and validation.

**Local behavior:**
- Treat local and mounted UNC roots identically.
- Contain all operations beneath the canonical root. With `FollowLinks=false`, reject any existing symlink/reparse point traversed between root and target.
- Stream asynchronously, leave caller streams open, support recursive listing/deletion, and use native filesystem copy/move.
- For byte downloads, enforce the option-specific maximum or the constructor/library default of 64 MiB and return `storage.too_large` before/while buffering.
- `ExistsAsync` returns success false only for absence; other I/O failures are failed results.

**Tests/TDD:**
- First tests must fail because the new API/project does not exist.
- Cover normalization/traversal, local CRUD, metadata/listing, recursive delete, overwrite conflicts, caller-owned streams, byte limit, cancellation, UNC-compatible root handling, health, and lease one-time disposal.
- Run `dotnet test tests/Storage.Tests/Storage.Tests.csproj --configuration Release -p:CodeLogicFromNuGet=true`.

### Task 2: Registry, Library Lifecycle, Persistence, Native Access, Events, and Health

Build `StorageLibrary`, its registry, provider factories, persistence, and generic events on top of Task 1.

**Public API:**
- `DefaultStorage`, `GetStorage(string connectionId="Default")`, `GetConnections()` returning sanitized `StorageConnectionInfo` values.
- `TClient GetNativeClient<TClient>(string connectionId="Default")` for reusable clients. Missing ID, lifecycle misuse, or type mismatch is programmer misuse and throws a descriptive exception. Never expose secrets.
- `Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(...)` for session providers.
- `Task<Result> AddOrUpdateConnectionAsync<TConfig>(string id, TConfig config, bool persist=true, CancellationToken ct=default)` for each built-in typed connection config.
- `Task<Result> RemoveConnectionAsync(string id, bool persist=true, CancellationToken ct=default)`.
- Runtime-only `Result RegisterBackend(string id, IStorageBackend backend, bool ownsBackend=true)` for custom adapters/prebuilt clients.

**Behavior:**
- `OnConfigureAsync` registers general and all provider config sections, even before their adapters are implemented; later tasks fill their config models.
- `OnInitializeAsync` validates global uniqueness, builds every enabled backend, and requires the enabled/default connection to exist when the library is enabled. Disabled library is a healthy no-op.
- Registry access and swaps are thread-safe. Validate/build a replacement before atomically publishing it. Drain active common-operation leases before disposing a replaced backend; document that previously returned raw native clients become invalid when their connection is replaced/removed.
- Persist changes through the appropriate strongly typed CodeLogic config model/file. A prebuilt/custom backend is runtime-only.
- Run health probes concurrently, scoped to each configured root, with `HealthCheckTimeoutSeconds`; aggregate healthy/degraded/unhealthy and name failed IDs.
- Add `StorageItemWrittenEvent`, `StorageItemDeletedEvent`, `StorageItemCopiedEvent`, and `StorageItemMovedEvent`, including connection/provider/path/timestamp and source/destination where relevant.

**Tests/TDD:**
- Cover case-insensitive duplicate rejection across config files, default resolution, disabled behavior, lifecycle misuse, add/update/remove with persistence and reload, failed replacement preserving the old backend, backend ownership/disposal, native type checks/leases, concurrent lookup/swap, health aggregation/timeout, and non-fatal event publication.

### Task 3: S3-Compatible Provider and Legacy Migration Coverage

Add `AWSSDK.S3` exactly `4.0.101.6` and implement AWS S3, MinIO, Cloudflare R2, and other compatible endpoints.

**Configuration (`config.storage.s3.json`):**
- Dictionary of `S3ConnectionConfig` keyed by ID.
- Required `Bucket`; optional `Prefix`, `ServiceUrl`, `Region="us-east-1"`.
- `S3AuthenticationMode`: `DefaultCredentialChain`, `StaticCredentials`.
- Secret `AccessKey`, `SecretKey`, optional `SessionToken` for static mode.
- `ForcePathStyle`, `DisablePayloadSigning`, `DisableDefaultChecksumValidation`, `TimeoutSeconds=30`, `MaxRetries=3`.
- Validate absolute HTTP(S) custom URLs, auth-specific requirements, nonnegative retry bounds, and R2 endpoint/signing expectations without hard-coding an account-ID format.

**Behavior:**
- Bind the common service to one bucket/prefix mount.
- Use `IAmazonS3`; support a public provider constructor accepting a prebuilt client plus mount and ownership flag.
- Use `TransferUtility`/multipart behavior for large uploads, SDK streaming downloads, delimiter/pagination listing, HEAD info/exists, virtual directory markers, native same-bucket/cross-bucket copy where the mount permits, and copy+delete move.
- Reusable native type is `IAmazonS3`; caller does not dispose library-owned clients.
- Do not repeat old defects: do not conflate access errors with absence, do not close upload streams, do not turn cancellation into failure, do not make successful writes depend on metadata enrichment/events, and URL scheme—not a dead `UseHttps` setting—controls transport.

**Tests/TDD:**
- Use an injectable S3 boundary/fake HTTP path for offline tests; add environment-gated live tests for S3-compatible endpoints.
- Cover request mapping, bucket/prefix containment, R2 flags, range reads, paging, stream ownership, not-found versus denied, cancellation, multipart selection, copy/move, and native access.
- Add migration tests/helpers only for documentation examples; do not auto-import old config or expose compatibility namespaces.

### Task 4: FTP/FTPS and SFTP Providers

Add `FluentFTP` exactly `54.2.0` and `SSH.NET` exactly `2025.1.0`.

**FTP/FTPS config (`config.storage.ftp.json`):** host, port, root, anonymous or username/password, `FtpEncryptionMode` (`None`, `Explicit`, `Implicit`), data connection type, client certificate path/password, optional trusted server-certificate SHA-256 pins, and timeout. Default to explicit FTPS. Never allow blanket certificate acceptance.

**SFTP config (`config.storage.sftp.json`):** host, port, root, username, auth mode (`Password`, `PrivateKey`), secret password/private-key passphrase, private-key path, one or more required SHA-256 host-key fingerprints, timeout.

**Behavior:**
- Common adapters support root-contained info/exists/list/directories/upload/download/delete/rename move.
- FTP/SFTP copy falls back to bounded read/write rather than claiming a server-side copy.
- `OpenNativeConnectionAsync<AsyncFtpClient>` and `<SftpClient>` return connected, caller-disposable leases; no reusable raw client is returned for these stateful sessions.
- Preserve cancellation where the dependency supports it; between-chunk cancellation is acceptable only where the dependency has no cancellable call.

**Tests/TDD:**
- Unit-test config/security validation and provider mapping with protocol boundaries.
- Add opt-in container/live tests for CRUD/list/move, FTPS trust failure/pin success, wrong SFTP fingerprint, wrong credentials, cancellation, and lease disposal.

### Task 5: WebDAV, Azure Blob, and Google Cloud Storage Providers

Add exact pins: `WebDAVClient` `2.7.0`, `Azure.Storage.Blobs` `12.29.1`, `Azure.Identity` `1.21.0`, and `Google.Cloud.Storage.V1` `4.15.0`.

**WebDAV (`config.storage.webdav.json`):** base URL, root, auth mode (`None`, `Basic`, `Bearer`, `Windows`), username/password/bearer token, timeout, and optional certificate SHA-256 pins. Require HTTPS by default; HTTP must be an explicit `AllowInsecureHttp=true` opt-in, not certificate bypass.

**Azure (`config.storage.azure.json`):** service URI, container, prefix, auth mode (`DefaultCredential`, `ConnectionString`, `SharedKey`, `Sas`), account name/key, connection string, SAS token, timeout. Bind native reusable access to `BlobContainerClient`.

**GCS (`config.storage.gcs.json`):** project ID, bucket, prefix, auth mode (`ApplicationDefault`, `ServiceAccountFile`, `ServiceAccountJson`), service-account path/secret JSON, timeout. Bind native reusable access to `StorageClient`.

**Behavior:**
- WebDAV uses PROPFIND/listing and native COPY/MOVE when supported.
- Azure and GCS map virtual directories, metadata, ranges, pages, generation/ETag conflicts, and SDK transfer/checksum behavior to the common contract.
- Public constructors accept prebuilt `IClient`, `BlobContainerClient`, or `StorageClient` with ownership flags.

**Tests/TDD:**
- Boundary tests for auth construction, URI/root containment, listing/page mapping, range handling, metadata, conflicts, cancellation, stream ownership, and native access.
- Add opt-in WebDAV/Azurite integration tests and credential-gated Azure/GCS tests.

### Task 6: OpenStack Swift Provider

Implement Swift directly with reusable `HttpClient`, `ResponseHeadersRead`, and `System.Text.Json`; do not add `openstack.net`.

**Config (`config.storage.swift.json`):** auth URL, region, container, prefix, auth mode (`ApplicationCredential`, `PreissuedToken`), application credential ID/secret, preissued storage endpoint/token, timeout.

**Behavior:**
- Implement Keystone v3 application-credential token acquisition plus preissued endpoint/token mode.
- Cache tokens until shortly before expiry with single-flight refresh; retry once after 401 only when the body is safely replayable.
- Stream object PUT/GET/HEAD/DELETE, marker-based JSON listing, virtual directory prefixes, range reads, metadata headers, native object copy when advertised, and copy+delete move.
- Expose a public reusable `SwiftClient` native type without exposing secrets.
- Sanitize/redact tokens and endpoint query secrets from logs/errors.

**Tests/TDD:**
- Use a real fake `HttpMessageHandler` boundary and literal HTTP fixtures.
- Cover Keystone request/response, token caching/refresh, 401 replay rules, request escaping, root containment, paging markers, range/metadata mapping, cancellation, errors, and native access. Add an environment-gated Swift live test.

### Task 7: Bounded Cross-Backend Transfers and Shared Contract Suite

Implement library-level cross-connection `CopyAsync` and `MoveAsync` using a bounded `System.IO.Pipelines` relay with a 1 MiB maximum buffered payload.

**Behavior:**
- Acquire source/destination registry operation leases for the entire transfer.
- Concurrently stream source download into the pipe and destination upload from it; never buffer the complete payload or require seekability.
- Respect overwrite/create-parent options.
- Cancel the peer operation on either-side failure. Best-effort delete an incomplete destination; retain and return the primary failure with cleanup failure in sanitized details.
- Move deletes the source only after destination upload succeeds. If source deletion fails, return failure identifying that the destination exists and source remains.
- Same-connection services prefer native copy/move where their capability says so and otherwise use the same bounded fallback.

**Tests/TDD:**
- Build one reusable provider contract suite against local and deterministic fake backends, with capability-conditioned assertions.
- Prove bounded memory with a large generated non-seekable stream, cancellation propagation, upload/download failure cleanup, source preservation, move delete ordering, overwrite conflict, and registry replacement waiting for an active transfer.

### Task 8: Retire StorageS3, Documentation, CI, Packaging, and Final Verification

After all new functionality is green:

- Remove tracked `CL.StorageS3` source/project/docs and `tests/StorageS3.Tests`; preserve its untracked PNG.
- Replace `CL.StorageS3` with `CL.Storage` in `.github/workflows/release.yml`, `.github/workflows/docs.yml`, `docs/docfx.json`, root README, docs indexes/cards, API index, and library TOC. The catalog remains twelve libraries.
- Replace `docs/libs/storages3.md` with `docs/libs/storage.md` covering common usage, every config file/provider, security, native access, runtime persistence, capabilities, health, and cross-backend transfers.
- Add a manual migration guide mapping old API/config/events to the new API. No aliases, compatibility package, or automatic config importer.
- Ensure package README/changelog are packed and PackageId is `CodeLogic.Storage`.
- Build docs metadata if available, then run:
  - `dotnet build CL.Storage/CL.Storage.csproj --configuration Release -p:CodeLogicFromNuGet=true`
  - `dotnet test tests/Storage.Tests/Storage.Tests.csproj --configuration Release -p:CodeLogicFromNuGet=true`
  - every existing `tests/*/*.Tests.csproj` project
  - `dotnet pack CL.Storage/CL.Storage.csproj --configuration Release --output artifacts -p:CodeLogicFromNuGet=true`
- Inspect the `.nupkg` contents/nuspec for README, changelog, license, target framework, package ID, dependency versions, and CodeLogic dependency.
- Confirm `git status` contains only intended tracked changes plus the preserved pre-existing PNG.
