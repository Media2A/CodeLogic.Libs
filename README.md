# CodeLogic Libraries

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Production-ready .NET 10 library integrations for [CodeLogic 3](https://github.com/Media2A/CodeLogic). Each library is a self-contained `ILibrary` implementation with auto-generated configuration, health checks, and lifecycle management.

## Libraries

| Package | Version | Description |
|---------|---------|-------------|
| [CodeLogic.Common](CL.Common/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.Common?label=)](https://www.nuget.org/packages/CodeLogic.Common) | Hashing, caching, imaging (SkiaSharp), compression, file utilities |
| [CodeLogic.MySQL2](CL.MySQL2/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.MySQL2?label=)](https://www.nuget.org/packages/CodeLogic.MySQL2) | MySQL ORM with LINQ query builder, table sync, migrations, result caching |
| [CodeLogic.SQLite](CL.SQLite/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.SQLite?label=)](https://www.nuget.org/packages/CodeLogic.SQLite) | SQLite with connection pooling, LINQ queries, table sync |
| [CodeLogic.PostgreSQL](CL.PostgreSQL/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.PostgreSQL?label=)](https://www.nuget.org/packages/CodeLogic.PostgreSQL) | PostgreSQL with multi-database support and LINQ query builder |
| [CodeLogic.Mail](CL.Mail/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.Mail?label=)](https://www.nuget.org/packages/CodeLogic.Mail) | SMTP/IMAP email with HTML template engine |
| [CodeLogic.StorageS3](CL.StorageS3/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.StorageS3?label=)](https://www.nuget.org/packages/CodeLogic.StorageS3) | Amazon S3 / Cloudflare R2 / MinIO object storage |
| [CodeLogic.SocialConnect](CL.SocialConnect/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.SocialConnect?label=)](https://www.nuget.org/packages/CodeLogic.SocialConnect) | Discord webhooks + Steam Web API (profiles, bans, games) |
| [CodeLogic.NetUtils](CL.NetUtils/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.NetUtils?label=)](https://www.nuget.org/packages/CodeLogic.NetUtils) | DNS lookups, DNSBL blacklist checking, IP geolocation (MaxMind) |
| [CodeLogic.GameNetQuery](CL.GameNetQuery/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.GameNetQuery?label=)](https://www.nuget.org/packages/CodeLogic.GameNetQuery) | Game server queries — Valve RCON, Source UDP, Minecraft |
| [CodeLogic.SystemStats](CL.SystemStats/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.SystemStats?label=)](https://www.nuget.org/packages/CodeLogic.SystemStats) | Cross-platform CPU, memory, process monitoring |
| [CodeLogic.GitHelper](CL.GitHelper/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.GitHelper?label=)](https://www.nuget.org/packages/CodeLogic.GitHelper) | Git repository management via libgit2 |
| [CodeLogic.TwoFactorAuth](CL.TwoFactorAuth/) | [![NuGet](https://img.shields.io/nuget/v/CodeLogic.TwoFactorAuth?label=)](https://www.nuget.org/packages/CodeLogic.TwoFactorAuth) | TOTP 2FA with QR code generation |

All packages target **.NET 10** and depend on **CodeLogic 3.x**.

## Quick Start

### 1. Install

```bash
# Install the core framework
dotnet add package CodeLogic

# Install the libraries you need
dotnet add package CodeLogic.MySQL2
dotnet add package CodeLogic.Mail
```

### 2. Load in your application

```csharp
var result = await CodeLogic.InitializeAsync(opts =>
{
    opts.FrameworkRootPath = "data/codelogic";
    opts.AppVersion = "1.0.0";
});

// Load the libraries you installed
await Libraries.LoadAsync<MySQL2Library>();
await Libraries.LoadAsync<MailLibrary>();

CodeLogic.RegisterApplication(new MyApplication());
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

### 3. Configure

On first run, each library auto-generates its config file under `data/codelogic/Libraries/`:

```
data/codelogic/Libraries/
├── CL.MySQL2/config.mysql2.json        ← database connection
├── CL.Mail/config.mail.json            ← SMTP settings
└── ...
```

Edit the JSON files to configure. Validation runs on startup — invalid config stops the boot with a clear error.

### 4. Use

```csharp
// MySQL2 — LINQ query builder with optional result caching
var users = await db.Query<UserRecord>()
    .Where(u => u.IsActive && u.Role == "admin")
    .OrderBy(u => u.Name)
    .WithCache(TimeSpan.FromMinutes(5))
    .ToPagedListAsync(page: 1, pageSize: 20);

// Mail — send templated email
await mailService.SendAsync("user@example.com", "Welcome!", "templates/welcome.html", model);

// Storage — upload to S3/R2
var url = await storage.UploadAsync("avatars", "user-123.webp", stream, "image/webp");

// Health — check all loaded libraries
var report = await CodeLogic.HealthCheckAsync();
```

## How Libraries Work

Every CodeLogic library follows the same pattern:

1. **`ILibrary` implementation** — defines the lifecycle hooks
2. **Config model** (`ConfigModelBase`) — auto-generated JSON, validated on startup
3. **Service classes** — the actual functionality, accessed via the library instance
4. **Health check** — reports Healthy/Degraded/Unhealthy via `HealthCheckAsync()`

Libraries are loaded before your application starts and stopped after it stops. This guarantees your application code can always rely on database connections, mail services, etc. being available.

```csharp
// Access a loaded library anywhere in your code
var mysql = Libraries.Get<MySQL2Library>();
var repo = mysql.GetRepository<UserRecord>();
```

## Documentation

- [Getting Started](docs/articles/getting-started.md)
- [Database Libraries (MySQL, PostgreSQL, SQLite)](docs/articles/database-libraries.md)
- [Mail & Templates](docs/articles/mail.md)
- [Object Storage (S3/R2)](docs/articles/storage.md)
- [Social Integrations (Discord, Steam)](docs/articles/social.md)
- [System Monitoring](docs/articles/system-monitoring.md)
- [Game Server Queries](docs/articles/game-queries.md)
- [Security & 2FA](docs/articles/security.md)
- [API Reference](docs-output/index.html)

## Requirements

- [CodeLogic 3.x](https://github.com/Media2A/CodeLogic)
- .NET 10 SDK or later

## License

MIT — see [LICENSE](LICENSE)
