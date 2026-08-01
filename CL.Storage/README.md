# CodeLogic.Storage

One provider-neutral storage library for CodeLogic applications. The same `IStorageService`
operations work with:

- local directories and UNC shares
- Amazon S3 and S3-compatible services such as MinIO, R2, and Spaces
- FTP, explicit FTPS, and implicit FTPS
- SFTP
- WebDAV
- Azure Blob Storage
- Google Cloud Storage
- OpenStack Swift

The common surface covers item information, existence checks, directory and recursive
listing, streamed and byte-array upload/download, ranges, directory creation, deletion,
copy, move, health checks, metadata, pagination, and cancellation.

## Named connections and JSON configuration

Each provider has a typed `Connections` dictionary in its own CodeLogic configuration
section and JSON file:

| Section | Connection type |
| --- | --- |
| `storage.local` | `LocalConnectionConfig` |
| `storage.s3` | `S3ConnectionConfig` |
| `storage.ftp` | `FtpConnectionConfig` |
| `storage.sftp` | `SftpConnectionConfig` |
| `storage.webdav` | `WebDavConnectionConfig` |
| `storage.azure` | `AzureBlobConnectionConfig` |
| `storage.gcs` | `GoogleCloudConnectionConfig` |
| `storage.swift` | `SwiftConnectionConfig` |

For example, `config.storage.s3.json` can contain:

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
      "Region": "us-east-1",
      "ForcePathStyle": true,
      "AuthenticationMode": "StaticCredentials",
      "AccessKey": "...",
      "SecretKey": "..."
    }
  }
}
```

Connection IDs are case-insensitive and unique across every provider. The default ID is
selected in the `storage` section.

## Common API

```csharp
IStorageService media = storage.GetStorage("media");

await using var input = File.OpenRead("photo.jpg");
var uploaded = await media.UploadAsync("photos/photo.jpg", input);

var page = await media.ListAsync("photos", new StorageListOptions
{
    Recursive = true,
    PageSize = 250
});

var bytes = await media.DownloadBytesAsync("photos/photo.jpg", new StorageDownloadOptions
{
    Offset = 1024,
    Length = 4096
});
```

Connections can be changed at runtime with the same typed models. By default, the change
is persisted back to the provider's JSON section:

```csharp
await storage.AddOrUpdateConnectionAsync("backup", new SftpConnectionConfig
{
    Host = "sftp.example.com",
    Username = "backup",
    AuthenticationMode = SftpAuthenticationMode.PrivateKey,
    PrivateKeyPath = @"C:\keys\backup_ed25519",
    HostKeyFingerprints = ["SHA256:..."]
});

await storage.RemoveConnectionAsync("backup", persist: true);
```

Pass `persist: false` for a runtime-only connection.

## Native clients and direct connections

Reusable SDK clients are available for providers such as S3, WebDAV, Azure Blob, Google
Cloud Storage, and Swift:

```csharp
IAmazonS3 s3 = storage.GetNativeClient<IAmazonS3>("media");
BlobContainerClient blobs = storage.GetNativeClient<BlobContainerClient>("azure-media");
```

Session-oriented providers expose a scoped native connection:

```csharp
var opened = await storage.OpenNativeConnectionAsync<AsyncFtpClient>("legacy-ftp");
if (opened.IsSuccess)
{
    await using var lease = opened.Value!;
    AsyncFtpClient ftp = lease.Client;
    // Use the connected FluentFTP client directly.
}
```

Every built-in backend also has a public constructor accepting its native client or client
factory. A directly constructed backend can be installed with
`StorageLibrary.RegisterBackend`, which makes custom endpoints and application-owned
clients possible without going through JSON configuration.

Native clients returned by the library remain library-owned. Do not dispose reusable
clients. Dispose native session leases when finished.
