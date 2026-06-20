# CL.StorageS3

> S3-compatible object storage — one API across Amazon S3, MinIO, and Cloudflare R2.

`CL.StorageS3` adds object storage to a CodeLogic 4 application over [AWSSDK.S3](https://www.nuget.org/packages/AWSSDK.S3). You configure one or more named **connections** (each pointing at an S3, MinIO, or R2 endpoint), then perform bucket and object operations through an `S3StorageService` scoped to a connection. Every operation returns the framework `Result<T>` so exceptions are caught and wrapped — check `IsSuccess` / `IsFailure` and read `Error?.Message` rather than using try/catch. The two existence checks are the exception: they return a plain `Task<bool>`.

| | |
|---|---|
| **Package** | [`CodeLogic.StorageS3`](https://www.nuget.org/packages/CodeLogic.StorageS3) |
| **Library class** | `CL.StorageS3.StorageS3Library` |
| **Config file** | `config.storages3.json` |
| **Dependencies** | AWSSDK.S3 4.x |

## Install & load

```bash
dotnet add package CodeLogic.StorageS3
```

```csharp
using CL.StorageS3;

await Libraries.LoadAsync<StorageS3Library>();   // register before ConfigureAsync()
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();

var storage = Libraries.Get<StorageS3Library>();

var s3 = storage.DefaultService;          // the "Default" connection
var r2 = storage.GetService("R2");        // a named connection
```

`DefaultService` is shorthand for `GetService("Default")`. Each call returns a fresh `S3StorageService` bound to that connection's underlying client. The connection IDs you can pass are exactly the `connectionId` values from configuration.

## The multi-provider story

The library speaks one protocol — S3 — but each provider needs slightly different addressing and signing. You select a provider entirely through configuration; the code you write is identical across all three.

| Setting | AWS S3 | MinIO | Cloudflare R2 |
|---------|--------|-------|---------------|
| `serviceUrl` | `https://s3.amazonaws.com` | `http://localhost:9000` | `https://<account>.r2.cloudflarestorage.com` |
| `region` | your region (e.g. `us-east-1`) | ignored | `auto` (ignored when `serviceUrl` set) |
| `forcePathStyle` | `false` | `true` | `false` |
| `useHttps` | `true` | `false` (local) | `true` |
| `disablePayloadSigning` | `false` | `false` | `true` |

`region` is only consulted when `serviceUrl` is blank; once a service URL is set, the endpoint wins and the region is ignored. `forcePathStyle: true` selects `host/bucket/key` addressing (required by MinIO and most non-AWS services); `false` selects `bucket.host/key` virtual-host addressing (AWS and R2). `disablePayloadSigning: true` turns off streaming AWS4 payload signing, which Cloudflare R2 rejects.

## Bucket operations

```csharp
Result<bool> created = await s3.CreateBucketAsync("my-bucket");
Result<bool> deleted = await s3.DeleteBucketAsync("old-bucket");   // bucket must be empty

Result<List<BucketInfo>> buckets = await s3.ListBucketsAsync();
foreach (var b in buckets.Value!)
    Console.WriteLine($"{b.Name} (created {b.CreationDate:u})");

bool exists = await s3.BucketExistsAsync("my-bucket");   // plain bool, no Result
```

`CreateBucketAsync` publishes a `BucketCreatedEvent` on success.

## Uploading objects

`PutObjectAsync` has two overloads — one for a `byte[]` and one for a `Stream`. Both take an optional `UploadOptions` and return `Result<S3ObjectInfo>` describing the stored object (its size, ETag, content type, metadata, and public URL are read back after the write).

```csharp
// From bytes
byte[] bytes = await File.ReadAllBytesAsync("report.pdf");
Result<S3ObjectInfo> a = await s3.PutObjectAsync(
    "my-bucket", "docs/report.pdf", bytes,
    new UploadOptions
    {
        ContentType = "application/pdf",
        CacheControl = "public, max-age=3600",
        ContentDisposition = "inline; filename=\"report.pdf\"",
        StorageClass = "STANDARD",            // or REDUCED_REDUNDANCY, INTELLIGENT_TIERING, ...
        MakePublic = true,                    // sets the public-read canned ACL
        Metadata = { ["uploaded-by"] = "user-123" }
    });

// From a stream
await using var stream = File.OpenRead("photo.webp");
Result<S3ObjectInfo> b = await s3.PutObjectAsync(
    "my-bucket", "img/photo.webp", stream,
    new UploadOptions { ContentType = "image/webp" });

if (b.IsSuccess)
    Console.WriteLine(b.Value!.PublicUrl);    // empty unless publicUrl is configured
```

Each successful upload publishes an `ObjectUploadedEvent`. When the connection has `disablePayloadSigning` enabled (R2), the request automatically opts out of streaming payload signing.

## Downloading objects

Download as a `byte[]` for small payloads, or as a `Stream` to pipe large objects without buffering them in memory.

```csharp
Result<byte[]> bytes = await s3.GetObjectAsync("my-bucket", "docs/report.pdf");
if (bytes.IsSuccess)
    await File.WriteAllBytesAsync("report.pdf", bytes.Value!);

Result<Stream> streamResult = await s3.GetObjectStreamAsync("my-bucket", "video.mp4");
if (streamResult.IsSuccess)
{
    await using var src = streamResult.Value!;   // caller disposes the stream
    await using var dst = File.Create("video.mp4");
    await src.CopyToAsync(dst);
}
```

`DownloadOptions` adds byte-range reads and version retrieval:

```csharp
// Read bytes 0–1023 (a 1 KiB range)
Result<byte[]> head = await s3.GetObjectAsync(
    "my-bucket", "video.mp4",
    new DownloadOptions { RangeStart = 0, RangeEnd = 1023 });

// Retrieve a specific version
Result<Stream> old = await s3.GetObjectStreamAsync(
    "my-bucket", "config.json",
    new DownloadOptions { VersionId = "abc123" });
```

`GetObjectAsync` is built on top of `GetObjectStreamAsync` — it reads the stream fully into a byte array for you.

## Metadata & existence

```csharp
Result<S3ObjectInfo> info = await s3.GetObjectInfoAsync("my-bucket", "docs/report.pdf");
if (info.IsSuccess)
{
    var o = info.Value!;
    Console.WriteLine($"{o.Size} bytes, {o.ContentType}, modified {o.LastModified:u}");
    foreach (var (k, v) in o.Metadata)
        Console.WriteLine($"  {k} = {v}");
}

bool present = await s3.ObjectExistsAsync("my-bucket", "docs/report.pdf");   // plain bool
```

`GetObjectInfoAsync` performs a metadata-only HEAD request — it never downloads the object body.

## Deleting objects

```csharp
Result<bool> deleted = await s3.DeleteObjectAsync("my-bucket", "docs/report.pdf");
```

A successful delete publishes an `ObjectDeletedEvent`.

## Listing with pagination & prefixes

`ListObjectsAsync` returns one page at a time. Filter by `prefix`, cap a page with `maxKeys` (default `1000`), and walk pages with the continuation token.

```csharp
string? token = null;
do
{
    Result<ListObjectsResult> page = await s3.ListObjectsAsync(
        "my-bucket", prefix: "uploads/", continuationToken: token, maxKeys: 500);

    if (page.IsFailure) break;

    foreach (var obj in page.Value!.Objects)
        Console.WriteLine($"{obj.Key} ({obj.Size} bytes)");

    token = page.Value.IsTruncated ? page.Value.NextContinuationToken : null;
}
while (token is not null);
```

`CommonPrefixes` surfaces virtual "folders" when the underlying request uses a delimiter, and `Count` is a convenience for `Objects.Count`.

## Copying objects

```csharp
Result<bool> copied = await s3.CopyObjectAsync(
    sourceBucket: "my-bucket", sourceKey: "img/photo.webp",
    destBucket:   "backups",   destKey:   "2026/photo.webp");
```

Works within a single bucket or across buckets on the same connection.

## Presigned URLs

Generate a temporary, signed GET URL so a client can fetch a private object directly without credentials.

```csharp
Result<string> url = await s3.GeneratePresignedUrlAsync(
    "my-bucket", "docs/report.pdf", TimeSpan.FromMinutes(15));

if (url.IsSuccess)
    return Redirect(url.Value!);   // valid for 15 minutes
```

The URL is signed for the `GET` verb against the connection's credentials and endpoint.

## UploadOptions reference

```csharp
public class UploadOptions
{
    public string ContentType { get; set; } = "";                       // MIME type
    public Dictionary<string, string> Metadata { get; set; } = [];      // user metadata
    public string StorageClass { get; set; } = "";                      // e.g. STANDARD, REDUCED_REDUNDANCY
    public string CacheControl { get; set; } = "";                      // Cache-Control header
    public string ContentDisposition { get; set; } = "";               // Content-Disposition header
    public bool MakePublic { get; set; } = false;                       // sets public-read ACL

    public static UploadOptions Default();                              // no overrides
}
```

Empty-string fields are omitted from the request. `MakePublic` sets the canned `public-read` ACL; the returned `PublicUrl` only fills in when `publicUrl` is configured on the connection.

## DownloadOptions reference

```csharp
public class DownloadOptions
{
    public long? RangeStart { get; set; }    // start byte offset (null = start of object)
    public long? RangeEnd { get; set; }      // inclusive end byte offset (null = end of object)
    public string? VersionId { get; set; }   // specific version (null = current)

    public static DownloadOptions Default();
}
```

Setting either range bound issues a ranged request (`RangeStart ?? 0` … `RangeEnd ?? long.MaxValue`).

## Models

### S3ObjectInfo

```csharp
public class S3ObjectInfo
{
    public string Key { get; set; }
    public string BucketName { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string ETag { get; set; }
    public string StorageClass { get; set; }
    public string ContentType { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    public string PublicUrl { get; set; }   // "" unless a public URL is configured
}
```

`ContentType` and `Metadata` are populated for metadata and upload results; objects returned by `ListObjectsAsync` carry the fields available from a list response (key, size, last-modified, ETag, storage class).

### BucketInfo

```csharp
public class BucketInfo
{
    public string Name { get; set; }
    public DateTime CreationDate { get; set; }
    public string Region { get; set; }   // may be empty for non-AWS providers
}
```

### ListObjectsResult

```csharp
public class ListObjectsResult
{
    public List<S3ObjectInfo> Objects { get; set; }
    public string? NextContinuationToken { get; set; }   // pass to the next call
    public bool IsTruncated { get; set; }                // more results remain
    public List<string> CommonPrefixes { get; set; }     // virtual folders
    public int Count { get; }                            // Objects.Count
}
```

## Configuration

The library writes `config.storages3.json` (section `storages3`) on first run. Keys are camelCase. One or more connections live under `connections`, each identified by a unique `connectionId`.

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "accessKey": "your-access-key",
      "secretKey": "your-secret-key",
      "serviceUrl": "https://s3.amazonaws.com",
      "publicUrl": "https://cdn.example.com",
      "region": "us-east-1",
      "defaultBucket": "my-bucket",
      "forcePathStyle": false,
      "useHttps": true,
      "timeoutSeconds": 30,
      "maxRetries": 3,
      "disablePayloadSigning": false
    }
  ]
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `enabled` | `bool` | `true` | Master switch; when `false` no connections are registered and the health check reports *disabled*. |
| `connections` | `array` | `[]` | One or more connection blocks. At least one is required when enabled; `connectionId` values must be unique. |
| `connectionId` | `string` | `"Default"` | Identifier used to resolve the connection via `GetService`. |
| `accessKey` | `string` (secret) | `""` | Access key ID. Required. |
| `secretKey` | `string` (secret) | `""` | Secret access key. Required. |
| `serviceUrl` | `string` | `""` | Endpoint URL. Required. See the provider table above. |
| `publicUrl` | `string` | `""` | Base URL for building public object links; leave blank for private buckets. |
| `region` | `string` | `"us-east-1"` | AWS region; ignored when `serviceUrl` is set. |
| `defaultBucket` | `string` | `""` | Bucket used when code doesn't specify one. |
| `forcePathStyle` | `bool` | `true` | Path-style addressing (`host/bucket/key`). Required for MinIO and most non-AWS services; set `false` for AWS and R2. |
| `useHttps` | `bool` | `true` | Use HTTPS for the connection. |
| `timeoutSeconds` | `int` | `30` | HTTP request timeout (1–600). |
| `maxRetries` | `int` | `3` | Retry attempts on transient failures (0–20). |
| `disablePayloadSigning` | `bool` | `false` | Disables streaming AWS4 payload signing; set `true` for Cloudflare R2. |

Configuration is validated at initialization: a connection must supply `accessKey`, `secretKey`, and `serviceUrl`; `timeoutSeconds` must be positive; `maxRetries` must be non-negative; and `connectionId`s must not collide. Invalid configuration throws during startup.

## Events

All three events implement `IEvent` and are published to the CodeLogic event bus. Each carries the originating `ConnectionId`.

| Event | Published when | Payload |
|-------|----------------|---------|
| `ObjectUploadedEvent` | `PutObjectAsync` succeeds | `ConnectionId`, `BucketName`, `Key`, `Size`, `UploadedAt` |
| `ObjectDeletedEvent` | `DeleteObjectAsync` succeeds | `ConnectionId`, `BucketName`, `Key`, `DeletedAt` |
| `BucketCreatedEvent` | `CreateBucketAsync` succeeds | `ConnectionId`, `BucketName`, `CreatedAt` |

## Health check

`HealthCheckAsync()` tests every registered connection by listing its buckets, then aggregates:

- **Healthy** — all connections respond (or the library is disabled).
- **Degraded** — some connections respond, some fail (the report names the failed IDs).
- **Unhealthy** — the library isn't initialized, has no connections, or every connection fails.

```csharp
var status = await storage.HealthCheckAsync();
// status.Status : Healthy | Degraded | Unhealthy
```

For low-level access you can reach the underlying clients through `storage.ConnectionManager` (e.g. `ConnectionManager.GetClient(id)`), but most code should stay on `S3StorageService`.

## See also

- [Getting Started](../getting-started.md) — load, configure, and use any `CL.*` library.
- [API Reference](../api/index.md) — generated type/member documentation.
- [Package on NuGet](https://www.nuget.org/packages/CodeLogic.StorageS3)
