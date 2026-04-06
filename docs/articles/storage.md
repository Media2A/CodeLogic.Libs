# Object Storage — CL.StorageS3

CL.StorageS3 provides unified access to Amazon S3 and MinIO-compatible object storage. It supports upload, download, delete, listing, presigned URLs, and bucket management.

---

## Registration

```csharp
await Libraries.LoadAsync<CL.StorageS3.StorageS3Library>();
```

---

## Configuration (`config.storage.json`)

### Amazon S3

```json
{
  "Provider": "S3",
  "Region": "eu-west-1",
  "AccessKey": "AKIAIOSFODNN7EXAMPLE",
  "SecretKey": "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
  "DefaultBucket": "my-app-storage"
}
```

### MinIO

```json
{
  "Provider": "MinIO",
  "Endpoint": "minio.internal:9000",
  "AccessKey": "minioadmin",
  "SecretKey": "minioadmin",
  "UseSSL": false,
  "DefaultBucket": "my-app-storage"
}
```

---

## Uploading Objects

```csharp
var storage = context.GetLibrary<CL.StorageS3.StorageS3Library>();

// Upload from bytes
await storage.PutAsync("uploads/avatar-123.jpg", imageBytes, "image/jpeg");

// Upload from stream
await using var stream = File.OpenRead("report.pdf");
await storage.PutAsync("reports/2026-04.pdf", stream, "application/pdf");

// Upload to a specific bucket
await storage.PutAsync(
    key:         "backups/db-2026-04-01.sql.gz",
    data:        backupBytes,
    contentType: "application/gzip",
    bucket:      "my-app-backups"
);
```

---

## Downloading Objects

```csharp
// Download to bytes
byte[] data = await storage.GetAsync("uploads/avatar-123.jpg");

// Download to stream
await using var outStream = File.Create("local-copy.pdf");
await storage.GetToStreamAsync("reports/2026-04.pdf", outStream);

// Check existence
bool exists = await storage.ExistsAsync("uploads/avatar-123.jpg");
```

---

## Deleting Objects

```csharp
// Delete a single object
await storage.DeleteAsync("uploads/old-avatar.jpg");

// Delete multiple objects
await storage.DeleteManyAsync([
    "uploads/temp-1.jpg",
    "uploads/temp-2.jpg",
    "cache/stale-data.json"
]);
```

---

## Listing Objects

```csharp
// List all objects with prefix
var objects = await storage.ListAsync("uploads/");

foreach (var obj in objects)
{
    Console.WriteLine($"{obj.Key}  {obj.Size:N0} bytes  {obj.LastModified:yyyy-MM-dd}");
}

// List with pagination
var page = await storage.ListAsync("reports/", maxKeys: 100, continuationToken: nextToken);
```

---

## Presigned URLs

Generate temporary URLs for direct client access:

```csharp
// Download URL (valid for 1 hour)
var downloadUrl = await storage.GetPresignedUrlAsync(
    key:     "uploads/avatar-123.jpg",
    expires: TimeSpan.FromHours(1),
    method:  PresignedMethod.Get
);

// Upload URL (client uploads directly to S3/MinIO)
var uploadUrl = await storage.GetPresignedUrlAsync(
    key:     "uploads/new-avatar.jpg",
    expires: TimeSpan.FromMinutes(15),
    method:  PresignedMethod.Put
);
```

Return `uploadUrl` to the client — they POST/PUT directly to S3 without going through your server.

---

## Bucket Management

```csharp
// Create bucket
await storage.CreateBucketAsync("my-new-bucket");

// Check if bucket exists
bool exists = await storage.BucketExistsAsync("my-new-bucket");

// List all buckets
var buckets = await storage.ListBucketsAsync();

// Delete bucket (must be empty)
await storage.DeleteBucketAsync("my-old-bucket");
```

---

## Object Metadata

```csharp
// Attach metadata on upload
await storage.PutAsync("uploads/doc.pdf", pdfBytes, "application/pdf",
    metadata: new Dictionary<string, string>
    {
        ["uploaded-by"] = "user-123",
        ["original-name"] = "Q1 Report.pdf"
    });

// Read metadata
var info = await storage.GetMetadataAsync("uploads/doc.pdf");
Console.WriteLine(info.ContentType);   // application/pdf
Console.WriteLine(info.Size);          // bytes
Console.WriteLine(info.Metadata["uploaded-by"]);   // user-123
```

---

## Health Check

```csharp
// Returns Healthy if the default bucket is accessible
// Returns Unhealthy if credentials are invalid or endpoint is unreachable
var status = await storage.HealthCheckAsync();
```
