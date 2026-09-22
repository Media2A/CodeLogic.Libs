# Migrating from CodeLogic.StorageS3

`CodeLogic.Storage` supersedes the S3-only `CodeLogic.StorageS3` package for mounted object and file
storage. The new package deliberately changes configuration and APIs so S3, local filesystems, FTP,
SFTP, WebDAV, Azure Blob, GCS, and Swift share one safe contract.

## Package and library

```diff
- dotnet add package CodeLogic.StorageS3
+ dotnet add package CodeLogic.Storage
```

```diff
- await Libraries.LoadAsync<CL.StorageS3.StorageS3Library>();
- var s3 = Libraries.Get<CL.StorageS3.StorageS3Library>().DefaultService;
+ await Libraries.LoadAsync<CL.Storage.StorageLibrary>();
+ var storage = Libraries.Get<CL.Storage.StorageLibrary>();
+ IStorageService s3 = storage.GetStorage("media");
```

## Connection scope changed

The old service accepted a bucket on each operation. A new S3 connection mounts one bucket and an
optional prefix, so common operations accept only a path below that mount. Create one connection per
bucket/prefix boundary your application needs.

Old shape:

```csharp
await s3.PutObjectAsync("company-media", "production/photo.jpg", source);
```

New shape:

```json
{
  "Connections": {
    "media": {
      "Bucket": "company-media",
      "Prefix": "production",
      "Region": "eu-north-1",
      "AuthenticationMode": "DefaultCredentialChain"
    }
  }
}
```

```csharp
await storage.GetStorage("media").UploadAsync("photo.jpg", source);
```

Configuration is now in `storage` and `storage.s3` sections rather than `storages3`. Credentials can
use the AWS default credential chain or explicit static credentials. A clear-text custom endpoint is
rejected unless `AllowInsecureHttp` is deliberately enabled.

## Operation mapping

| StorageS3 | Storage |
|---|---|
| `GetService(id)` / `DefaultService` | `GetStorage(id)` / `DefaultStorage` |
| `PutObjectAsync(bucket, key, stream)` | `UploadAsync(path, stream)` |
| `PutObjectAsync(bucket, key, bytes)` | `UploadBytesAsync(path, bytes)` |
| `GetObjectStreamAsync` | `DownloadAsync` |
| `GetObjectAsync` | `DownloadBytesAsync` |
| `GetObjectInfoAsync` | `GetInfoAsync` |
| `ObjectExistsAsync` | `ExistsAsync` returning `Result<bool>` |
| `ListObjectsAsync` | `ListAsync`, `EnumeratePagesAsync`, or `EnumerateItemsAsync` |
| `CopyObjectAsync` | service `CopyAsync` or cross-connection `StorageLibrary.CopyAsync` |
| no common move | service or cross-connection `MoveAsync` |
| `DeleteObjectAsync` | `DeleteAsync` |
| `GeneratePresignedUrlAsync` | `CreateSignedUrlAsync` for reads or writes |
| `GetObjectTaggingAsync` / `PutObjectTaggingAsync` | `GetTagsAsync` / `SetTagsAsync` |
| version download | `StorageDownloadOptions.VersionId` |
| no version listing | `ListVersionsAsync` / `EnumerateVersionPagesAsync` |

Existence checks now use `Result<bool>` so authorization, timeout, and provider failure are not
silently confused with a missing item.

## Upload options

Portable upload options include overwrite/create-only behavior, parent creation, content type, user
metadata, and atomic ETag/version conditions. Provider-specific storage class, cache-control,
content-disposition, canned ACL, and public-URL behavior are intentionally not guessed across
providers. Use `GetNativeClient<IAmazonS3>` for those S3-specific operations.

The old `MakePublic` shortcut has no common replacement. Public ACL changes are security-sensitive and
should be performed explicitly with the native client or bucket policy.

Large S3 uploads are multipart automatically and keep one bounded part buffer. Upload streams remain
caller-owned.

## Buckets and provider administration

The common package is rooted inside an already provisioned bucket/container/directory. Bucket create,
delete, account-wide listing, lifecycle policy, IAM, and similar administration remain provider-specific:

```csharp
IAmazonS3 client = storage.GetNativeClient<IAmazonS3>("media");
```

This separation also allows bucket-scoped credentials to pass normal connection health checks.

## Events and errors

The old S3-only object events become provider-neutral `StorageItemWrittenEvent`,
`StorageItemDeletedEvent`, `StorageItemCopiedEvent`, and `StorageItemMovedEvent`. Cross-connection and
local-directory completion events include sanitized counts but never signed URLs or credentials.

Expected failures now use stable `storage.*` codes. Cancellation is an `OperationCanceledException`,
not a failed result. A move whose destination completed but whose source deletion failed returns
`storage.partial_failure`; callers should reconcile that state rather than retrying blindly.

### Finer error codes

Provider failures that previously surfaced as `storage.unauthorized`, `storage.unavailable`, or
`storage.provider_error` now use more specific codes. The Core error kind is unchanged for the
authorization and availability groups, so code that branches on the kind keeps working; code that
compares exact codes should switch to the table below or to `StorageErrorInfo.IsTransient`.

| Situation | Before | Now |
|---|---|---|
| Wrong password, key, or token (FTP 530, SSH auth, HTTP 401) | `unauthorized` / `provider_error` | `authentication_failed` |
| Logged in but not allowed (FTP 550 permission, SFTP, HTTP 403, local ACL) | `unauthorized` / `provider_error` | `permission_denied` |
| TLS handshake or certificate pin failure | `unauthorized` / `unavailable` | `tls_failure` |
| SSH host key not trusted | `unavailable` | `host_key_rejected` |
| DNS failure, connection refused, proxy failure | `unavailable` / `provider_error` | `connection_failed` |
| Connection dropped mid-operation | `unavailable` / `provider_error` | `connection_lost` |
| Rate limited or too many sessions (HTTP 429/503, FTP 421, SSH) | `unavailable` / `provider_error` | `server_busy` |
| Disk full or quota (FTP 452/552, HTTP 507, local ENOSPC) | `unavailable` / `provider_error` | `quota_exceeded` |

`StorageErrorInfo.IsTransient` returns true for `timeout`, `unavailable`, `connection_failed`,
`connection_lost`, and `server_busy`. Provider codes are available through
`StorageErrorInfo.TryGetDetail(error, StorageErrorInfo.FtpReplyKey, out var reply)` and similar keys.

## Recommended rollout

1. Add `CodeLogic.Storage` beside the legacy package.
2. Create mounted `storage.s3` connections for the buckets/prefixes used by the application.
3. Migrate object data paths and result handling one call site at a time.
4. Move bucket administration and non-portable object options to typed native-client calls.
5. Verify capability flags against every S3-compatible endpoint used in production.
6. Remove `CodeLogic.StorageS3` after its configuration and events are no longer consumed.
