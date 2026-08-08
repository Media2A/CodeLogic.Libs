# CL.Storage

> Safe mounted storage across local filesystems, S3, FTP/FTPS, SFTP, WebDAV, Azure Blob,
> Google Cloud Storage, and OpenStack Swift.

| | |
|---|---|
| **Package** | `CodeLogic.Storage` |
| **Library class** | `CL.Storage.StorageLibrary` |
| **Target** | .NET 10 / CodeLogic 4 |
| **Result model** | `Result` and `Result<T>`; cancellation throws `OperationCanceledException` |

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
the mounted root for info/listing and idempotent root directory creation. Root deletion and root
transfer source/destination operations that could be destructive are restricted by the relevant API.

## Configuration

The main `storage` section contains the master enable switch, default connection ID, bounded byte
download limit, and health-check settings. Providers register these sections:

| Section | Model | Important settings |
|---|---|---|
| `storage.local` | `LocalConnectionConfig` | `RootPath`, `FollowLinks` |
| `storage.s3` | `S3ConnectionConfig` | `Bucket`, `Prefix`, `Region`, endpoint/auth, multipart bounds |
| `storage.ftp` | `FtpConnectionConfig` | host/root/auth, encryption/data mode, certificate pins |
| `storage.sftp` | `SftpConnectionConfig` | host/root/auth, private key, host-key pins or explicit auto-accept |
| `storage.webdav` | `WebDavConnectionConfig` | endpoint/root/auth, headers, certificate pins |
| `storage.azure` | `AzureBlobConnectionConfig` | container URI/prefix and credential mode |
| `storage.gcs` | `GoogleCloudConnectionConfig` | bucket/prefix, project/credential, upload chunk size |
| `storage.swift` | `SwiftConnectionConfig` | auth URL, region, account/container/prefix, credentials |

Every section contains a case-insensitive `Connections` dictionary. IDs are globally unique, even
when the connections use different providers.

### Security defaults

- S3 and WebDAV custom endpoints require HTTPS unless `AllowInsecureHttp` is explicitly enabled.
- Endpoint user info, query strings, and fragments are rejected so secrets do not become configuration URLs.
- WebDAV custom headers cannot replace authorization/host/framing headers or contain line breaks.
- FTPS and WebDAV validate the certificate chain by default. Optional SHA-256 pins can deliberately
  trust a specific leaf certificate.
- SFTP requires at least one SHA-256 host-key fingerprint by default. Set `AutoAcceptHostKey` to
  `true` only for a trusted environment when connecting without a pinned host key is necessary.
- Upload metadata/header values reject control characters and provider-specific size overflows.

## Operations

```csharp
await using var input = File.OpenRead("asset.bin");
var put = await files.UploadAsync("assets/asset.bin", input, new StorageUploadOptions
{
    Overwrite = false,
    CreateParents = true,
    ContentType = "application/octet-stream"
});

var info = await files.GetInfoAsync("assets/asset.bin");
var exists = await files.ExistsAsync("assets/asset.bin");
var page = await files.ListAsync("assets", new StorageListOptions
{
    Recursive = true,
    PageSize = 500
});

var download = await files.DownloadAsync("assets/asset.bin");
if (download.IsSuccess)
{
    await using var owned = download.Value!;
    await owned.CopyToAsync(output);
}
```

Upload streams are caller-owned and remain open. A successful `DownloadAsync` value owns its provider
response, session, and registry lease until disposed. `DownloadBytesAsync` is intentionally bounded by
the global limit or `StorageDownloadOptions.MaxBufferedBytes`.

`EnumeratePagesAsync` and `EnumerateItemsAsync` walk provider tokens lazily and fail safely if a provider
repeats a token. Batch helpers preserve input order, cap item count/concurrency, and retain one result per
item rather than stopping at the first expected provider failure.

## Copy, move, and local directory transfer

```csharp
await storage.CopyAsync(
    "source", "folder",
    "destination", "backup/folder");

await storage.MoveAsync(
    "incoming", "ready.bin",
    "archive", "2026/ready.bin");
```

The transfer coordinator:

1. leases both active connections;
2. validates normalized path relationships;
3. stages each destination file under a unique internal name;
4. relays cross-provider data through a pipe with 1 MiB maximum read-ahead;
5. backs up overwritten files and rolls the destination tree back on failure;
6. deletes a move source only after every destination file commits.

An incomplete restore, staging cleanup, or post-copy source deletion is
`storage.partial_failure`. Its details contain provider-neutral error/state codes, never raw provider
messages or credentials.

For local import/export, use `UploadDirectoryAsync` and `DownloadDirectoryAsync`. Both return a
`StorageDirectoryTransferReport`; uploads reject links/reparse points instead of following them.

## Conditions, metadata, tags, versions, and signed URLs

Use capability checks before advanced work:

```csharp
if (files.Capabilities.Supports(StorageFeature.MetadataWrite))
{
    await files.SetMetadataAsync("asset.bin", new Dictionary<string, string>
    {
        ["reviewed"] = "true"
    });
}
```

`StorageMutationCondition` applies atomic expected ETag/version guards to upload and delete operations.
S3, Azure Blob, GCS, and Swift enforce the conditions supported by their native protocol. Other
providers return `storage.unsupported` rather than running a racy client-side check.

Optional advanced contracts are reachable through extension methods:

```csharp
var metadata = await files.GetMetadataAsync("asset.bin");

var tags = await files.SetTagsAsync(
    "asset.bin",
    new Dictionary<string, string> { ["tier"] = "archive" },
    new StorageTagUpdateOptions { Mode = StorageTagUpdateMode.Merge });

var signed = await files.CreateSignedUrlAsync("asset.bin", new StorageSignedUrlOptions
{
    Method = StorageSignedUrlMethod.Read,
    ExpiresIn = TimeSpan.FromMinutes(10)
});

var versions = await files.ListVersionsAsync("asset.bin");
await files.DeleteVersionAsync("asset.bin", "provider-version-id");
```

Treat signed URLs as credentials and never log them. S3, Azure Blob, and GCS expose version
read/list/delete functionality. SAS/signing flags are added only when the active credential can sign.
S3-compatible and Azure Blob connections expose portable object tag reads plus bounded merge/replace
updates. Check `StorageFeature.Tags`; other providers leave tag administration to their native client.

## Convenience and integrity helpers

- `UploadFileAsync` and failure-safe `DownloadToFileAsync`;
- bounded text and JSON read/write;
- upload/download progress wrappers;
- SHA-256/SHA-384/SHA-512 and interoperability MD5 streaming checksums;
- per-item batch info/delete/copy/move.

The JSON/text methods have explicit size bounds, and checksum computation streams through a pooled
64 KiB buffer.

## Health, mutation, and native access

```csharp
Result health = await storage.CheckConnectionHealthAsync("archive");

await storage.AddOrUpdateConnectionAsync("archive", new S3ConnectionConfig
{
    Bucket = "archive-bucket",
    Region = "eu-north-1"
});

IAmazonS3 native = storage.GetNativeClient<IAmazonS3>("archive");
```

Set `persist: false` for a runtime-only connection update. `RegisterBackendAsync` installs a custom
backend after an optional health probe. Reusable native clients are library-owned; FTP/SFTP native
connections are returned in `NativeConnectionLease<T>` and must be disposed.

The native escape hatch is the right place for provider administration (bucket/container creation,
IAM/lifecycle policies) and non-portable options such as S3 ACL/storage-class settings.

## Migration

The legacy `CodeLogic.StorageS3` package accepted a bucket on every operation. `CodeLogic.Storage`
mounts a bucket/prefix per connection and uses relative paths. See the package
[`MIGRATION.md`](https://github.com/Media2A/CodeLogic.Libs/blob/main/CL.Storage/MIGRATION.md) for
operation/configuration mappings and the native replacement for S3-only administration.
