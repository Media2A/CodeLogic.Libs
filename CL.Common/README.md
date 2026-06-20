# CodeLogic.Common

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Common)](https://www.nuget.org/packages/CodeLogic.Common)

General-purpose utility toolkit for [CodeLogic](https://github.com/Media2A/CodeLogic) applications.
A broad collection of stateless, thread-safe helpers covering security, ID/password
generation, caching, compression, imaging, data (JSON), file handling, type conversion,
cron parsing, date/time, strings, web, networking, and reflection.

Operations that can fail return `CodeLogic.Core.Results.Result` / `Result<T>` rather than
throwing, so callers rarely need `try`/`catch`.

## Install

```bash
dotnet add package CodeLogic.Common
```

## Features

| Area | Type(s) | What it does |
| --- | --- | --- |
| **Security — hashing** | `CL.Common.Security.Hashing` | SHA-256/512, MD5 (obsolete), PBKDF2 password hash/verify, salt generation, HMAC-SHA256/512 |
| **Security — encryption** | `CL.Common.Security.Encryption` | AES-256-GCM authenticated encrypt/decrypt (string & bytes) with PBKDF2 key derivation, random key generation |
| **Generators — IDs** | `CL.Common.Generators.IdGenerator` | GUIDs, sequential, timestamp, random alphanumeric/hex/Base64, URL-safe, NanoID, sortable IDs |
| **Generators — passwords** | `CL.Common.Generators.PasswordGenerator` | Random passwords, strong passwords, passphrases, PINs, strength estimation |
| **Caching** | `CL.Common.Caching.ICache`, `MemoryCache` | Async typed in-process cache with per-entry TTL, prefix removal, count, background eviction |
| **Compression** | `CL.Common.Compression.CompressionHelper` | GZip, Brotli, and LZ4 (byte arrays) plus GZip-to-Base64 string helpers |
| **Imaging** | `CL.Common.Imaging.CLU_Imaging` | Resize, crop, format conversion (JPEG/PNG/WebP), thumbnails, dimensions, Base64 — via SkiaSharp |
| **Data — JSON** | `CL.Common.Data.JsonHelper` | Serialize/deserialize, file I/O, validation, merge, property extraction (System.Text.Json) |
| **Conversion** | `CL.Common.Conversion.TypeConverter` | Safe `Result`-based conversion to int/long/double/bool/decimal/DateTime/Guid/enum |
| **File handling** | `CL.Common.FileHandling.FileSystem` | Async read/write/copy/move/delete, directory create, listing, size, existence |
| **Cron** | `CL.Common.Parser.CronParser`, `CronExpression` | Parse/validate 5-field cron and compute the next UTC occurrence |
| **Date/time** | `CL.Common.Time.DateTimeHelper` | Unix timestamps, ISO 8601, age, business days, boundary helpers, relative strings |
| **Strings** | `CL.Common.Strings.StringHelper`, `StringValidator` | Truncate, slug, case conversion, HTML strip; email/URL/phone/IP/GUID validation |
| **Web** | `CL.Common.Web.UrlHelper`, `HtmlHelper`, `HttpClientHelper`, `HttpHeaderHelper` | URL build/parse/encode, HTML sanitize/encode, typed HTTP requests, header/IP/UA helpers |
| **Networking** | `CL.Common.Networking.NetworkPing`, `NetworkDns`, `SubnetCalculator`, `TraceRoute` | ICMP ping, DNS lookup, IPv4 subnet math, traceroute |
| **Reflection** | `CL.Common.Assemblies.AssemblyHelper`, `ReflectionHelper` | Assembly metadata/loading, type discovery, embedded resources, dynamic property/method access |

## Quick Start

```csharp
using CL.Common.Imaging;
using CL.Common.Security;

// Convert any image to WebP
using var input = File.OpenRead("photo.jpg");
var result = CLU_Imaging.ConvertImage(input, ImageFormat.Webp);
if (result.IsSuccess)
    await File.WriteAllBytesAsync("photo.webp", result.Value!.ToArray());

// Hash a string (lowercase hex)
string digest = Hashing.Sha256("my-secret-data");
```

## Security

```csharp
using CL.Common.Security;

// PBKDF2 password hashing (salt is embedded in the returned string)
string stored = Hashing.HashPassword("hunter2");
bool ok = Hashing.VerifyPassword("hunter2", stored);   // true

// AES-256-GCM authenticated encryption — tampering is detected on decrypt
string cipher = Encryption.EncryptAes("top secret", "passphrase");
string plain  = Encryption.DecryptAes(cipher, "passphrase");
```

## Generators

```csharp
using CL.Common.Generators;

string id   = IdGenerator.NanoId();              // e.g. "V1StGXR8_Z5jdHi6B-myT"
string sort = IdGenerator.Sortable();            // lexicographically sortable
string pw   = PasswordGenerator.GenerateStrong(20);
string pass = PasswordGenerator.GeneratePassphrase(wordCount: 4);  // "Apple-Bridge-..."
PasswordStrength strength = PasswordGenerator.CalculateStrength(pw);
```

## Caching

```csharp
using CL.Common.Caching;

ICache cache = new MemoryCache();                       // background eviction loop runs internally
await cache.SetAsync("user:1", userObj, TimeSpan.FromMinutes(5));
var cached = await cache.GetAsync<User>("user:1");      // null if missing/expired
await cache.RemoveByPrefixAsync("user:");
```

## Compression

```csharp
using CL.Common.Compression;

var packed = CompressionHelper.CompressLz4(payloadBytes);
if (packed.IsSuccess)
    byte[] original = CompressionHelper.DecompressLz4(packed.Value!).Value!;

// Text → Base64 (GZip)
string blob = CompressionHelper.CompressString("a lot of repeated text...").Value!;
```

## JSON & Conversion

```csharp
using CL.Common.Data;
using CL.Common.Conversion;

string json = JsonHelper.Serialize(new { name = "Ada" }).Value!;   // {"name":"Ada"}
var person  = JsonHelper.Deserialize<Person>(json);

var n = TypeConverter.ToInt("42");          // Result<int>
if (TypeConverter.TryConvert<double>("3.14", out var d)) { /* ... */ }
```

## Cron, Date/Time & Strings

```csharp
using CL.Common.Parser;
using CL.Common.Time;
using CL.Common.Strings;

var next = CronParser.GetNextOccurrence("0 */6 * * *");   // Result<DateTime> (UTC)
long unix = DateTimeHelper.ToUnixTimestamp(DateTime.UtcNow);
string rel = DateTimeHelper.ToRelativeString(DateTime.UtcNow.AddMinutes(-3)); // "3 minutes ago"

string slug = StringHelper.ToSlug("Hello, World!");       // "hello-world"
bool valid  = StringValidator.IsEmail("a@b.com");
```

## Web & Networking

```csharp
using CL.Common.Web;
using CL.Common.Networking;

string url = UrlHelper.AppendQuery("https://api.example.com/search",
    new() { ["q"] = "cats", ["page"] = "2" });

using var http = new HttpClient();
var data = await HttpClientHelper.GetJsonAsync<MyDto>(http, url);

var ping = await NetworkPing.PingAsync("example.com");    // Result<PingResult>
var ips  = await NetworkDns.LookupAsync("example.com");   // Result<string[]>
```

## Documentation

- [API Reference](../docs-output/api/CL.Common.html)

## Requirements

- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
