# CodeLogic.StorageS3

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.StorageS3)](https://www.nuget.org/packages/CodeLogic.StorageS3)

S3-compatible object storage library for [CodeLogic 3](https://github.com/Media2A/CodeLogic). Works with Amazon S3, Cloudflare R2, MinIO, and any S3-compatible provider. Built on [AWSSDK.S3](https://aws.amazon.com/sdk-for-net/).

## Install

```bash
dotnet add package CodeLogic.StorageS3
```

## Quick Start

```csharp
await Libraries.LoadAsync<StorageS3Library>();

var s3 = Libraries.Get<StorageS3Library>();
var service = s3.DefaultService;

// Upload
using var stream = File.OpenRead("photo.webp");
var url = await service.PutObjectAsync("my-bucket", "uploads/photo.webp", stream, "image/webp");

// Download
var data = await service.GetObjectAsync("my-bucket", "uploads/photo.webp");

// Delete
await service.DeleteObjectAsync("my-bucket", "uploads/photo.webp");

// List objects
var objects = await service.ListObjectsAsync("my-bucket", prefix: "uploads/");
```

## Features

- **Upload / Download / Delete** — standard object operations with stream support
- **Multiple Connections** — named connection IDs for multi-bucket or multi-provider setups
- **Public URLs** — configurable public URL prefix for CDN-served content
- **Presigned URLs** — generate temporary access URLs
- **Cloudflare R2** — first-class support with `forcePathStyle` and `disablePayloadSigning`
- **Health Checks** — verifies bucket accessibility

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

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](../LICENSE)
