# CodeLogic.StorageS3

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.StorageS3)](https://www.nuget.org/packages/CodeLogic.StorageS3)

S3-compatible object storage library for [CodeLogic](https://github.com/Media2A/CodeLogic). Works with Amazon S3, Cloudflare R2, MinIO, and any S3-compatible provider. Built on [AWSSDK.S3](https://aws.amazon.com/sdk-for-net/).

## Install

```bash
dotnet add package CodeLogic.StorageS3
```

## Quick Start

```csharp
await Libraries.LoadAsync<StorageS3Library>();

var s3 = Libraries.Get<StorageS3Library>();
var service = s3.DefaultService;            // S3StorageService for the "Default" connection

// Upload (every method returns a Result / Result<T>)
using var stream = File.OpenRead("photo.webp");
var put = await service.PutObjectAsync(
    "my-bucket", "uploads/photo.webp", stream,
    new UploadOptions { ContentType = "image/webp" });

if (put.IsSuccess)
    Console.WriteLine(put.Value!.PublicUrl);   // populated when PublicUrl is configured

// Download
var data = await service.GetObjectAsync("my-bucket", "uploads/photo.webp");
if (data.IsSuccess)
    File.WriteAllBytes("photo.webp", data.Value!);

// Delete
await service.DeleteObjectAsync("my-bucket", "uploads/photo.webp");

// List objects
var list = await service.ListObjectsAsync("my-bucket", prefix: "uploads/");
foreach (var obj in list.Value!.Objects)
    Console.WriteLine($"{obj.Key} ({obj.Size} bytes)");
```

All `S3StorageService` operations return `Result` / `Result<T>` — exceptions are caught
and wrapped, so check `IsSuccess` / `IsFailure` and read `Error` instead of using
try/catch. (`BucketExistsAsync` and `ObjectExistsAsync` return a plain `bool`.)

## Features

- **Objects** — upload (byte array or stream), download (byte array or stream), delete, copy, exists, metadata-only lookup
- **Range / version downloads** — `DownloadOptions` for byte-range reads and `VersionId` retrieval
- **Upload options** — `UploadOptions` for content type, cache-control, content-disposition, storage class, public-read ACL, and custom metadata
- **Buckets** — create, delete, list, exists
- **Listing** — prefix filter, paging via continuation tokens, common-prefix (folder) detection
- **Multiple Connections** — named connection IDs for multi-bucket or multi-provider setups; access any via `s3.GetService("id")`
- **Public URLs** — configurable public URL prefix; auto-populated on returned `S3ObjectInfo.PublicUrl`
- **Presigned URLs** — generate temporary GET access URLs
- **Events** — publishes `ObjectUploadedEvent`, `ObjectDeletedEvent`, and `BucketCreatedEvent` to the CodeLogic event bus
- **Cloudflare R2** — first-class support with `forcePathStyle` and `disablePayloadSigning`
- **Health Checks** — tests every configured connection (`Healthy` / `Degraded` / `Unhealthy`)

## Configuration

Auto-generated at `data/codelogic/Libraries/CL.StorageS3/config.storages3.json`:

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "accessKey": "your-access-key",
      "secretKey": "your-secret-key",
      "serviceUrl": "https://xxx.r2.cloudflarestorage.com",
      "publicUrl": "https://cdn.example.com",
      "region": "auto",
      "defaultBucket": "my-bucket",
      "forcePathStyle": true,
      "useHttps": true,
      "disablePayloadSigning": true
    }
  ]
}
```

## Documentation

- [Storage Guide](../docs/articles/storage.md)

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
