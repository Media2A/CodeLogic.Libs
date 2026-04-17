# CodeLogic.Common

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.Common)](https://www.nuget.org/packages/CodeLogic.Common)

General-purpose utility toolkit for [CodeLogic 3](https://github.com/Media2A/CodeLogic) applications. Provides imaging (SkiaSharp), hashing, caching, file handling, compression, and networking utilities.

## Install

```bash
dotnet add package CodeLogic.Common
```

## Features

- **Imaging** — resize, crop, convert between JPEG/PNG/WebP (via SkiaSharp), thumbnail generation, dimension validation, Base64 encoding
- **Hashing** — SHA256, MD5, HMAC helpers with string and stream inputs
- **Caching** — in-memory cache with TTL expiry
- **File Handling** — async read/write/copy/delete, directory management, path helpers
- **Compression** — LZ4 stream compression/decompression
- **Networking** — HTTP client helpers, download utilities

## Quick Start

```csharp
using CL.Common.Imaging;
using CL.Common.Hashing;

// Convert any image to WebP
using var input = File.OpenRead("photo.jpg");
var result = CLU_Imaging.ConvertImage(input, ImageFormat.Webp);
if (result.IsSuccess)
    await File.WriteAllBytesAsync("photo.webp", result.Value.ToArray());

// Resize with crop
var resized = CLU_Imaging.ResizeImage(input, 200, 200, allowCrop: true);

// Hash a string
var hash = CLU_Hashing.SHA256("my-secret-data");
```

## Documentation

- [API Reference](../docs-output/api/CL.Common.html)

## Requirements

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic) | .NET 10

## License

MIT — see [LICENSE](../LICENSE)
