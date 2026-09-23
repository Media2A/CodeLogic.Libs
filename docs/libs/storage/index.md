# CL.Storage

> Safe mounted storage across local filesystems, S3, FTP/FTPS, SFTP, WebDAV, Azure Blob,
> Google Cloud Storage, and OpenStack Swift — with the connection options, transfer features, and
> diagnostics of a desktop file-transfer client.

| | |
|---|---|
| **Package** | [`CodeLogic.Storage`](https://www.nuget.org/packages/CodeLogic.Storage) |
| **Library class** | `CL.Storage.StorageLibrary` |
| **Config files** | `config.storage.json` · `config.storage.<provider>.json` |
| **Target** | .NET 10 / CodeLogic 4 |
| **Result model** | `Result` and `Result<T>`; connection calls throw `OperationCanceledException` on cancel, while `StorageLibrary.CopyAsync`/`MoveAsync` and an applying sync report it |

This overview covers loading, the mount model, configuration, and the everyday API. The rest lives on
four sub-pages:

- **[Connections](connections.md)** — SFTP authentication, host keys, and jump hosts; FTP/FTPS and
  WebDAV options; proxies; session pools shared across registrations, retries, and keep-alive; runtime
  connections and runtime-only mode; testing settings and diagnosing a live connection.
- **[Transfers & Sync](transfers.md)** — safe copies and moves with transfer reports, guaranteed and
  verified transfers, streamed writes, conflict policies, staged resume and append, progress and speed
  limits, the durable transfer queue, compare and three-way sync with approved plans, watching for
  changes, and links in transfers.
- **[Files & Attributes](files.md)** — permissions, ownership, timestamps, links, server-side
  checksums, metadata/tags/versions/signed URLs, raw commands, and free space.
- **[Errors & Events](errors-events.md)** — the `storage.*` error codes, transient-failure helpers,
  and every event the library publishes.

## Install and start

```bash
dotnet add package CodeLogic.Storage
```

```csharp
using CL.Storage;

await Libraries.LoadAsync<StorageLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var storage = Libraries.Get<StorageLibrary>();
IStorageService files = storage.DefaultStorage;
IStorageService archive = storage.GetStorage("archive");
```

Throughout these pages `storage` is the `StorageLibrary` (connections, cross-connection transfers,
diagnostics) and `files` is one connection's `IStorageService` (file operations on that mount).

`GetStorage` returns a stable proxy. Replacing a connection does not invalidate the proxy, and the
old backend stays alive until active operations and returned download/native-session leases drain.

## Mount model and paths

Each connection mounts one security boundary:

- a local directory or UNC root;
- an S3/GCS bucket and optional prefix;
- an Azure/Swift container and optional prefix;
- an FTP, SFTP, or WebDAV directory.

API paths are relative to that mount. Backslashes are normalized to `/`, redundant `.` segments are
removed, and absolute or parent-escaping paths fail with `storage.invalid_path`. The empty path means
the mounted root for info/listing and idempotent root directory creation. Link targets, raw-command
paths, and every other path the library resolves stay inside the mount; only raw commands (off by
default) can reach outside it.

## Configuration

The main `storage` section contains the master enable switch, default connection ID, bounded byte
download limit, health-check settings, and library-wide speed limits. Providers register these
sections:

| Section | Model | Important settings |
|---|---|---|
| `storage.local` | `LocalConnectionConfig` | `RootPath`, `FollowLinks` |
| `storage.s3` | `S3ConnectionConfig` | `Bucket`, `Prefix`, `Region`, endpoint/auth, multipart bounds |
| `storage.ftp` | `FtpConnectionConfig` | host/root/auth, encryption/data mode, TLS pins and versions, encoding, listing parser |
| `storage.sftp` | `SftpConnectionConfig` | host/root, auth methods and keys, host-key pins or `known_hosts`, algorithms, jump host |
| `storage.webdav` | `WebDavConnectionConfig` | endpoint/root, Basic/Digest/NTLM/Negotiate/bearer auth, TLS pins, client certificate |
| `storage.azure` | `AzureBlobConnectionConfig` | container URI/prefix and credential mode |
| `storage.gcs` | `GoogleCloudConnectionConfig` | bucket/prefix, project/credential, upload chunk size, emulator URL |
| `storage.swift` | `SwiftConnectionConfig` | auth URL (Keystone or TempAuth), region, account/container/prefix |

Every section contains a case-insensitive `Connections` dictionary. IDs are globally unique, even
when the connections use different providers. Every remote connection accepts `Proxy` and
`TransferLimits`; FTP, SFTP, and WebDAV add `Retry`, and FTP and SFTP add `Session`. See
[Connections](connections.md).

### Security defaults

- S3, WebDAV, GCS, and Swift custom endpoints require HTTPS unless `AllowInsecureHttp` is enabled.
- Endpoint user info, query strings, and fragments are rejected so secrets do not become configuration URLs.
- WebDAV custom headers cannot replace authorization/host/framing headers or contain line breaks.
- FTPS and WebDAV validate the certificate chain by default. Certificate (`TrustedCertificateSha256`)
  or public-key (`TrustedPublicKeySha256`) pins deliberately trust a specific server; there is no
  accept-any switch.
- SFTP needs a trusted host key: SHA-256 `HostKeyFingerprints`, an OpenSSH `KnownHostsPath`, or
  `AutoAcceptHostKey = true` for trusted development environments only.
- Raw FTP/SSH commands are disabled unless `AllowRawCommands` is set, because they are not confined
  to the mount.
- Diagnostics, events, and error details never contain credentials, provider bodies, or signed URLs.

## Everyday operations

```csharp
await using var input = File.OpenRead("asset.bin");
var put = await files.UploadAsync("assets/asset.bin", input, new StorageUploadOptions
{
    ConflictPolicy = StorageConflictPolicy.OverwriteIfNewer,
    CreateParents = true,
    ContentType = "application/octet-stream"
});

var info = await files.GetInfoAsync("assets/asset.bin");
var exists = await files.ExistsAsync("assets/asset.bin");
var page = await files.ListAsync("assets", new StorageListOptions
{
    Recursive = true,
    PageSize = 500,
    NamePattern = "*.bin"
});

var download = await files.DownloadAsync("assets/asset.bin");
if (download.IsSuccess)
{
    await using var owned = download.Value!;
    await owned.CopyToAsync(output);
}
```

Upload streams are caller-owned and remain open. A successful `DownloadAsync` value owns its provider
response, session, and registry lease until disposed. `DownloadBytesAsync` is bounded by the global
limit or `StorageDownloadOptions.MaxBufferedBytes`.

Listings hide the library's own staging and backup items (`.cl-storage-*`, `.clstorage-*`); set
`IncludeInternal` to see them. `IncludeHidden = false` drops hidden items (dot-files, and items marked
hidden) together with what hidden folders hold, and `NamePattern` filters names with `*` and `?`
wildcards (case-insensitive). Recursive listings on S3, Azure Blob, Google Cloud, and Swift include
folders that exist only as key prefixes; they are sorted page by page, and an inferred folder can appear
again on a later page, so build a tree by path.

`EnumeratePagesAsync` and `EnumerateItemsAsync` walk provider tokens lazily and fail safely if a
provider repeats a token. Batch helpers preserve input order, cap item count/concurrency, and retain
one result per item rather than stopping at the first expected provider failure.

`StorageServiceExtensions` adds `UploadFileAsync` and atomic `DownloadToFileAsync`, bounded
`ReadTextAsync`/`WriteTextAsync` and `ReadJsonAsync<T>`/`WriteJsonAsync<T>`, and streaming checksums
(see [Files & Attributes](files.md#server-side-checksums)).

## Capabilities

Capabilities are granular flags plus provider limits. Check them at runtime rather than inferring
behavior from a provider name:

```csharp
if (files.Capabilities.Supports(StorageFeature.Permissions))
    await files.SetPermissionsAsync("deploy.sh", "750");
```

Optional features return `storage.unsupported` on connections that lack them. Flags include
`MetadataWrite`, `Tags`, `Versioning`, `SignedReadUrls`, `SignedWriteUrls`, `Permissions`, `Ownership`, `SetTimestamps`,
`CreateLinks`, `ReadLinks`, `Checksums`, `Append`, `ResumableUpload`, `AtomicMove`, `RawCommands`,
`SpaceInfo`, `ChangeNotifications`, `ConditionalCreate`, and `CaseInsensitivePaths`.

A flag says what the provider can do; on S3-compatible servers, whether a conditional request is really
enforced also depends on the server (see `ConditionalRequests` in
[Connections](connections.md#cloud-emulators-and-compatible-services)). A transfer reports how its
condition was enforced in `ConditionEnforcement`.

## Migration

The legacy `CodeLogic.StorageS3` package accepted a bucket on every operation. `CodeLogic.Storage`
mounts a bucket/prefix per connection and uses relative paths. This release also renames several
error codes, adds `ConflictPolicy`, extends `StorageItem` and `StorageConnectionInfo`, returns
`StorageTransferReport` from copies and moves, replaces `CreateTransferQueue` with
`OpenTransferQueueAsync`, makes two-way sync without a baseline report conflicts, and shares FTP/SFTP
session pools between registrations with identical settings. See the
package [`MIGRATION.md`](https://github.com/zyntal-com/CodeLogic.Libs/blob/main/CL.Storage/MIGRATION.md)
for every mapping.
