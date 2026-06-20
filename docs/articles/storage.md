# Object Storage — CL.StorageS3

CL.StorageS3 provides unified access to Amazon S3 and S3-compatible object storage
(MinIO, Cloudflare R2, Backblaze B2, and others). It supports bucket management plus
object upload, download, delete, copy, listing, metadata lookup, and presigned URLs.

All `S3StorageService` operations return `Result` / `Result<T>` — exceptions are caught
internally and wrapped, so you check `IsSuccess` / `IsFailure` and read `Error` rather
than using try/catch. The two existence checks (`BucketExistsAsync`, `ObjectExistsAsync`)
return a plain `bool`.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.StorageS3.StorageS3Library>();
```

---

## Configuration (`config.storages3.json`)

Auto-generated under `<FrameworkRoot>/Libraries/CL.StorageS3/config.storages3.json`.
Keys are camelCase. One or more connections are configured under `connections`, each
identified by a unique `connectionId`.

### Amazon S3

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "accessKey": "AKIAIOSFODNN7EXAMPLE",
      "secretKey": "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
      "serviceUrl": "https://s3.amazonaws.com",
      "region": "eu-west-1",
      "defaultBucket": "my-app-storage",
      "forcePathStyle": false,
      "useHttps": true
    }
  ]
}
```

### MinIO

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "accessKey": "minioadmin",
      "secretKey": "minioadmin",
      "serviceUrl": "http://minio.internal:9000",
      "defaultBucket": "my-app-storage",
      "forcePathStyle": true,
      "useHttps": false
    }
  ]
}
```

### Cloudflare R2

```json
{
  "enabled": true,
  "connections": [
    {
      "connectionId": "Default",
      "accessKey": "your-r2-access-key",
      "secretKey": "your-r2-secret-key",
      "serviceUrl": "https://<account>.r2.cloudflarestorage.com",
      "publicUrl": "https://cdn.example.com",
      "region": "auto",
      "defaultBucket": "my-bucket",
      "forcePathStyle": true,
      "disablePayloadSigning": true
    }
  ]
}
```

### Connection fields

| Field | Default | Notes |
|---|---|---|
| `connectionId` | `Default` | Unique key used to look up the connection. |
| `accessKey` / `secretKey` | `""` | Required credentials. |
| `serviceUrl` | `""` | Endpoint URL. Required. When set, `region` is ignored. |
| `publicUrl` | `""` | Base URL used to build `S3ObjectInfo.PublicUrl`. Blank = not generated. |
| `region` | `us-east-1` | Only used when `serviceUrl` is empty. |
| `defaultBucket` | `""` | Informational; operations still take an explicit bucket. |
| `forcePathStyle` | `true` | Required for MinIO and most non-AWS services. |
| `useHttps` | `true` | Use HTTPS for the connection. |
| `timeoutSeconds` | `30` | HTTP request timeout (1–600). |
| `maxRetries` | `3` | Retry attempts on transient failures (0–20). |
| `disablePayloadSigning` | `false` | Enable for Cloudflare R2 (disables STREAMING-AWS4 payload signing). |

Configuration is validated at startup: at least one connection is required when enabled,
connection IDs must be unique, and each connection must have `accessKey`, `secretKey`,
and `serviceUrl`.

---

## Getting a Storage Service

The library exposes one `S3StorageService` per connection.

```csharp
var s3 = context.GetLibrary<CL.StorageS3.StorageS3Library>();

// The "Default" connection
var storage = s3.DefaultService;

// A specific named connection
var backups = s3.GetService("Backups");
```

---

## Uploading Objects

```csharp
// Upload from a byte array
var result = await storage.PutObjectAsync(
    "my-app-storage", "uploads/avatar-123.jpg", imageBytes,
    new UploadOptions { ContentType = "image/jpeg" });

if (result.IsSuccess)
    Console.WriteLine(result.Value!.PublicUrl);   // set when publicUrl is configured

// Upload from a stream
await using var stream = File.OpenRead("report.pdf");
await storage.PutObjectAsync(
    "my-app-storage", "reports/2026-04.pdf", stream,
    new UploadOptions { ContentType = "application/pdf" });
```

### Upload options

```csharp
var opts = new UploadOptions
{
    ContentType        = "application/pdf",
    CacheControl       = "public, max-age=86400",
    ContentDisposition = "inline; filename=\"report.pdf\"",
    StorageClass       = "STANDARD",          // or REDUCED_REDUNDANCY, INTELLIGENT_TIERING, ...
    MakePublic         = true,                // sets the public-read canned ACL
    Metadata           = new Dictionary<string, string>
    {
        ["uploaded-by"]   = "user-123",
        ["original-name"] = "Q1 Report.pdf"
    }
};

await storage.PutObjectAsync("my-app-storage", "reports/q1.pdf", pdfBytes, opts);
```

---

## Downloading Objects

```csharp
// Download to a byte array
var data = await storage.GetObjectAsync("my-app-storage", "uploads/avatar-123.jpg");
if (data.IsSuccess)
    File.WriteAllBytes("avatar.jpg", data.Value!);

// Download as a stream (caller disposes the stream)
var streamResult = await storage.GetObjectStreamAsync("my-app-storage", "reports/2026-04.pdf");
if (streamResult.IsSuccess)
{
    await using var s = streamResult.Value!;
    await using var outFile = File.Create("local-copy.pdf");
    await s.CopyToAsync(outFile);
}

// Check existence (returns bool, not a Result)
bool exists = await storage.ObjectExistsAsync("my-app-storage", "uploads/avatar-123.jpg");
```

### Range and version downloads

```csharp
// Download only the first 1 KiB
var head = await storage.GetObjectAsync("my-app-storage", "big.bin",
    new DownloadOptions { RangeStart = 0, RangeEnd = 1023 });

// Download a specific object version
var v = await storage.GetObjectAsync("my-app-storage", "doc.pdf",
    new DownloadOptions { VersionId = "abc123..." });
```

---

## Deleting and Copying Objects

```csharp
// Delete a single object
await storage.DeleteObjectAsync("my-app-storage", "uploads/old-avatar.jpg");

// Copy within or across buckets
await storage.CopyObjectAsync(
    sourceBucket: "my-app-storage", sourceKey: "uploads/avatar.jpg",
    destBucket:   "my-app-backups", destKey:   "snapshots/avatar.jpg");
```

---

## Listing Objects

`ListObjectsAsync` returns a `ListObjectsResult` with the page of objects, a continuation
token, a truncation flag, and any common prefixes (virtual folders).

```csharp
var page = await storage.ListObjectsAsync("my-app-storage", prefix: "uploads/");
if (page.IsSuccess)
{
    foreach (var obj in page.Value!.Objects)
        Console.WriteLine($"{obj.Key}  {obj.Size:N0} bytes  {obj.LastModified:yyyy-MM-dd}");
}

// Page through everything
string? token = null;
do
{
    var result = await storage.ListObjectsAsync(
        "my-app-storage", prefix: "reports/", continuationToken: token, maxKeys: 100);
    if (result.IsFailure) break;

    // ... process result.Value!.Objects ...
    token = result.Value!.NextContinuationToken;
}
while (token is not null);
```

---

## Object Metadata

```csharp
// Read metadata without downloading the object body
var info = await storage.GetObjectInfoAsync("my-app-storage", "reports/q1.pdf");
if (info.IsSuccess)
{
    var o = info.Value!;
    Console.WriteLine(o.ContentType);            // application/pdf
    Console.WriteLine(o.Size);                    // bytes
    Console.WriteLine(o.ETag);
    Console.WriteLine(o.Metadata["uploaded-by"]); // user-123
}
```

---

## Presigned URLs

Generate a temporary GET URL for direct client download:

```csharp
var url = await storage.GeneratePresignedUrlAsync(
    "my-app-storage", "uploads/avatar-123.jpg", TimeSpan.FromHours(1));

if (url.IsSuccess)
    return url.Value!;   // hand to the client; valid for one hour
```

---

## Bucket Management

```csharp
// Create
await storage.CreateBucketAsync("my-new-bucket");

// Exists (returns bool)
bool exists = await storage.BucketExistsAsync("my-new-bucket");

// List
var buckets = await storage.ListBucketsAsync();
foreach (var b in buckets.Value!)
    Console.WriteLine($"{b.Name} (created {b.CreationDate:yyyy-MM-dd})");

// Delete (bucket must be empty)
await storage.DeleteBucketAsync("my-old-bucket");
```

---

## Events

Successful operations publish events to the CodeLogic event bus:

- `ObjectUploadedEvent(ConnectionId, BucketName, Key, Size, UploadedAt)`
- `ObjectDeletedEvent(ConnectionId, BucketName, Key, DeletedAt)`
- `BucketCreatedEvent(ConnectionId, BucketName, CreatedAt)`

Subscribe through the framework event bus to react to storage activity.

---

## Low-level Client Access

For operations not exposed by `S3StorageService`, reach the underlying
`AmazonS3Client` through the connection manager:

```csharp
var s3 = context.GetLibrary<CL.StorageS3.StorageS3Library>();
AmazonS3Client client = s3.ConnectionManager.GetClient("Default");
```

---

## Health Check

The library's health check tests every configured connection with a lightweight
`ListBuckets` call:

- **Healthy** — all connections operational (or the library is disabled).
- **Degraded** — some connections unreachable.
- **Unhealthy** — not initialized, no connections registered, or all connections failed.
