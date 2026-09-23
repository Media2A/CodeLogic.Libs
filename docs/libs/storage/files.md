# Files & Attributes

> Everything about a file beyond its bytes: permissions, ownership, timestamps, links, checksums,
> metadata, tags, versions, and signed URLs — plus raw server commands and free space.

Every method here is an extension on `IStorageService` and returns `storage.unsupported` when the
connection cannot do it. Check `Capabilities` first when you need to know in advance.

## What a listing tells you

`StorageItem` carries, wherever the provider reports them:

| Property | Source |
|---|---|
| `UnixMode`, `Permissions` (`rwxr-xr-x`) | FTP listings, SFTP, local (Unix) |
| `Owner`, `Group` | FTP listings |
| `OwnerId`, `GroupId` | SFTP |
| `LinkTarget` | FTP listings, local |
| `Created`, `LastAccessed` | local, SFTP (`LastAccessed`) |
| `IsHidden` | dot-files; local hidden attribute |
| `ETag`, `VersionId`, `ContentType`, metadata | object stores, WebDAV; local files get an ETag from their time and size |

## Permissions, ownership, timestamps, and links

```csharp
await files.SetPermissionsAsync("reports/q3.csv", "640");
await files.SetPermissionsRecursiveAsync("public", fileMode: 0x1A4, directoryMode: 0x1ED); // 0644 / 0755
await files.SetOwnerAsync("reports/q3.csv", ownerId: 1001, groupId: 1001);
await files.SetTimestampsAsync("reports/q3.csv", lastModified: sourceTime);
await files.CreateLinkAsync("current", "releases/v42");
var link = await files.ReadLinkAsync("current");
```

| | Local | FTP | SFTP |
|---|---|---|---|
| Permissions (`Permissions`) | Unix only | `SITE CHMOD` | yes, incl. setuid/setgid/sticky |
| Owner/group (`Ownership`) | no | no | numeric IDs |
| Timestamps (`SetTimestamps`) | modified + accessed | modified (`MFMT`) | modified + accessed |
| Create link (`CreateLinks`) | yes (relative) | no | yes |
| Read link (`ReadLinks`) | yes | from listings | no (SSH.NET lacks `readlink`) |

Link targets must stay inside the mounted root. On Windows, creating local links needs Developer Mode
or the symbolic-link privilege.

## Server-side checksums

`ComputeChecksumAsync` and `VerifyChecksumAsync` ask the server for a stored digest first and only
download the content when there is none; `StorageChecksum.Source` says which happened.
`GetServerChecksumAsync` returns only the server's value, and `StorageChecksumMode.ComputeOnly`
forces a download.

```csharp
Result<StorageChecksumVerification> verified = await files.VerifyChecksumAsync(
    "large.bin", expectedSha256Hex);
```

| Provider | Server digest |
|---|---|
| S3 | MD5 from single-part, non-KMS ETags; SHA-256 when stored with the object |
| Azure Blob | MD5 (`Content-MD5`) |
| Google Cloud Storage | MD5 of non-composite objects |
| Swift | MD5 ETag, except segmented large objects |
| FTP | `HASH`/`XMD5`/`XSHA256`/`XSHA512` when the server offers them |
| SFTP, WebDAV, Local | none (always computed) |

Computed checksums stream through a pooled buffer using MD5, SHA-256, SHA-384, or SHA-512. MD5 is
there for interoperability; prefer SHA-256 or stronger for security-sensitive checks.

## Metadata, tags, versions, and signed URLs

| Provider | Metadata | Tags | Conditional create/update/delete | Versions | Signed URLs |
|---|---|---|---|---|---|
| Local / UNC | no | no | create | no | no |
| S3-compatible | read/write | read/write | yes/yes/yes | read/list/delete | read/write |
| FTP / FTPS | no | no | no | no | no |
| SFTP | no | no | no | no | no |
| WebDAV | discovered properties, read-only | no | create | no | no |
| Azure Blob | read/write | read/write | yes/yes/yes | read/list/delete | SAS when credentials permit |
| Google Cloud Storage | read/write | no | yes/yes/yes | read/list/delete | when signing credentials permit |
| OpenStack Swift | read/write | no | yes/yes/yes | native only | no |

```csharp
var metadata = await files.GetMetadataAsync("asset.bin");

await files.SetTagsAsync(
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

Treat signed URLs as credentials and never log them.

`StorageMutationCondition` applies atomic ETag/version guards to uploads and deletes. Providers that
cannot enforce the condition atomically reject it instead of running a racy check-then-write:

```csharp
await files.UploadAsync("settings.json", replacement, new StorageUploadOptions
{
    Condition = new StorageMutationCondition { ExpectedETag = current.Value!.ETag }
});
```

## Raw commands

With `AllowRawCommands: true` on an FTP or SFTP connection, `ExecuteCommandAsync` sends a raw FTP
command (`SITE ...`, `SYST`) or runs an SSH shell command as the connection's account:

```csharp
var reply = await files.ExecuteCommandAsync("SITE CHMOD 640 reports/q3.csv");
if (reply.IsSuccess && !reply.Value!.Succeeded)
    Console.WriteLine($"Server said {reply.Value.Code}: {reply.Value.Output}");
```

It is off by default because commands are not confined to the connection's `Root`. A rejected command
or non-zero exit comes back as a result with `Succeeded = false`, not as an error. Servers that allow
only SFTP (`ForceCommand internal-sftp`) refuse shell commands.

## Free space

`GetSpaceAsync` reports free and used space: SFTP through `statvfs@openssh.com`, FTP through `AVBL`
where the server implements it, and local connections from the volume. WebDAV and object stores
return `storage.unsupported`.

```csharp
var space = await files.GetSpaceAsync();
if (space.IsSuccess && space.Value!.AvailableBytes < 10L * 1024 * 1024 * 1024)
    Console.WriteLine("Less than 10 GiB left.");
```
