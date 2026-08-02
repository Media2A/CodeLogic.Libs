# CodeLogic.Storage

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Storage)](https://www.nuget.org/packages/CodeLogic.Storage)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)

Provider-neutral, root-scoped storage for CodeLogic 4 and .NET 10. One API mounts local/UNC,
S3-compatible, FTP/FTPS, SFTP, WebDAV, Azure Blob, Google Cloud Storage, and OpenStack Swift
connections.

## Install and load

```bash
dotnet add package CodeLogic.Storage
```

```csharp
using CL.Storage;

await Libraries.LoadAsync<StorageLibrary>();
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

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
least one SHA-256 host-key fingerprint. FTPS and WebDAV use normal certificate validation by
default and optionally accept configured SHA-256 certificate pins; there is no accept-any switch.

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
Caller upload streams remain open. Returned download streams own their provider response and registry
lease and must be disposed.

Use `EnumeratePagesAsync` or `EnumerateItemsAsync` to walk continuation tokens without buffering a
complete remote tree. Bounded, order-preserving helpers are available for batch info, delete, copy,
and move operations.

## Safe transfers

The library can copy or move files and complete directory trees between any two mounted connections:

```csharp
Result copied = await storage.CopyAsync(
    "primary", "exports/2026",
    "archive", "yearly/2026",
    new StorageTransferOptions
    {
        Overwrite = true,
        MetadataPreservation = StorageMetadataPreservation.BestEffort
    });

Result moved = await storage.MoveAsync(
    "incoming", "ready/item.bin",
    "processed", "item.bin");
```

Cross-provider data uses a `System.IO.Pipelines` relay capped at 1 MiB. Each destination file is
uploaded to a unique staging name and committed only after the complete source stream succeeds.
Existing destination files are backed up and restored if a later directory item fails. A move deletes
its source only after the entire destination commits. Equal paths and a directory destination below
its source are rejected.

The normal `IStorageService.CopyAsync` and `MoveAsync` methods use the same coordinator for recursive
work. Safe same-provider file copies remain server-side when the provider can guarantee them.

Local directory trees can be transferred without manually registering a temporary local connection:

```csharp
Result<StorageDirectoryTransferReport> upload = await storage.UploadDirectoryAsync(
    @"C:\exports\2026", "archive", "yearly/2026");

Result<StorageDirectoryTransferReport> download = await storage.DownloadDirectoryAsync(
    "archive", "yearly/2026", @"C:\restore\2026");
```

Links/reparse points in a local upload are rejected rather than followed. Reports contain file,
directory, and byte counts.

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
| Local / UNC | physical | no | no | create | no | no |
| S3-compatible | virtual | read/write | read/write | yes/yes/yes | read/list/delete | read/write |
| FTP / FTPS | physical | no | no | no | no | no |
| SFTP | physical | no | no | no | no | no |
| WebDAV | physical | discovered properties are read-only | no | create | no | no |
| Azure Blob | virtual | read/write | read/write | yes/yes/yes | read/list/delete | SAS when credentials permit |
| Google Cloud Storage | virtual | read/write | no portable contract | yes/yes/yes | read/list/delete | when signing credentials permit |
| OpenStack Swift | virtual | read/write | no portable contract | yes/yes/yes | endpoint-specific/native | no portable TempURL contract |

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

Providers that cannot enforce the condition atomically reject it instead of performing a racy
check-then-write.

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

Result health = await storage.CheckConnectionHealthAsync("backup");
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

## Failures and compatibility

Expected failures use stable `storage.*` error codes such as `storage.not_found`,
`storage.conflict`, `storage.unauthorized`, `storage.too_large`, and `storage.unsupported`.
Incomplete cleanup/source deletion is reported as `storage.partial_failure` with sanitized state and
error codes. Provider bodies, credentials, and signed query strings are not exposed. Caller
cancellation propagates as `OperationCanceledException`.

Migrating from the legacy S3-only package? See [MIGRATION.md](MIGRATION.md).

## Requirements

- CodeLogic 4
- .NET 10

MIT license.
