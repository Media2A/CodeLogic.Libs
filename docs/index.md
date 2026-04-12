---
_layout: landing
---

# CodeLogic Libraries

**CodeLogic Libraries** (`CL.*`) is a suite of 12 production-ready .NET 10 libraries designed to integrate seamlessly with the [CodeLogic 3 framework](https://media2a.github.io/CodeLogic). Each library is a self-contained CodeLogic library that manages its own configuration, lifecycle, and health checks.

---

## What are CL.* Libraries?

Each `CL.*` package is a `ILibrary` implementation that plugs directly into the CodeLogic 3 boot sequence. You register it with `Libraries.LoadAsync<T>()`, configure it via the auto-generated JSON config file, and the framework handles initialization, dependency ordering, and graceful shutdown.

```csharp
// Program.cs
await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();
await Libraries.LoadAsync<CL.Mail.MailLibrary>();
await Libraries.LoadAsync<CL.TwoFactorAuth.TwoFactorAuthLibrary>();

CodeLogic.RegisterApplication(new MyApp());
await CodeLogic.ConfigureAsync();
await CodeLogic.StartAsync();
```

---

## Library Reference

| Library | Description |
|---------|-------------|
| [CL.Common](articles/getting-started.md) | General-purpose utilities: hashing (SHA-256/SHA-512/MD5/bcrypt), ID generation (ULID, UUID), string extensions, date helpers |
| [CL.GitHelper](articles/getting-started.md) | Git repository operations and automation: clone, pull, commit, push, branch management via LibGit2Sharp |
| [CL.Mail](articles/mail.md) | SMTP/IMAP email with Handlebars-style template engine, HTML and plain-text, attachments |
| [CL.MySQL2](articles/database-libraries.md) | MySQL with connection pooling, `Repository<T>` CRUD, `QueryBuilder<T>` LINQ-style queries, migrations |
| [CL.NetUtils](articles/getting-started.md) | DNSBL spam checking and MaxMind GeoIP2 database lookup for IP intelligence |
| [CL.PostgreSQL](articles/database-libraries.md) | PostgreSQL with multi-database support, `Repository<T>`, `QueryBuilder<T>`, schema migrations |
| [CL.SQLite](articles/database-libraries.md) | SQLite with custom connection pool, `Repository<T>`, `QueryBuilder<T>`, migration runner |
| [CL.SocialConnect](articles/social.md) | Discord webhooks plus Steam Web API profile lookups and ticket-based authentication |
| [CL.StorageS3](articles/storage.md) | Amazon S3 and MinIO object storage: upload, download, delete, presigned URLs, bucket management |
| [CL.SystemStats](articles/system-monitoring.md) | Cross-platform CPU usage, memory (total/available/used), and per-process statistics |
| [CL.TwoFactorAuth](articles/security.md) | TOTP-based 2FA with QR code generation (compatible with Google Authenticator, Authy) |
| [CL.GameNetQuery](articles/game-queries.md) | Game server queries: Valve RCON, Source UDP (A2S), Minecraft UDP/RCON |

---

## Design Principles

- **CodeLogic-native** — each library implements `ILibrary` with the full 4-phase lifecycle
- **Zero shared state** — each library owns its config directory, log directory, and data directory
- **Self-configuring** — config files are auto-generated with defaults on first run
- **Health-aware** — every library implements `HealthCheckAsync()` for operational monitoring
- **Dependency-safe** — `LibraryManifest.Dependencies` declares ordering requirements

---

## Quick Integration Example

```csharp
// 1. Reference the library project
// <ProjectReference Include="path/to/CodeLogic.Libs/CL.MySQL2/CL.MySQL2.csproj" />

// 2. Load it
await Libraries.LoadAsync<CL.MySQL2.MySQL2Library>();

// 3. After StartAsync(), use it from your application or other libraries
public class MyApp : IApplication
{
    private IMySqlRepository<User> _users = null!;

    public async Task RunAsync(ApplicationContext context)
    {
        var mysql = context.GetLibrary<CL.MySQL2.MySQL2Library>();
        _users = mysql.GetRepository<User>();

        var user = await _users.FindAsync(u => u.Email == "alice@example.com");
    }
}
```

---

## Next Steps

- [Getting Started](articles/getting-started.md) — how to reference, register, and use any CL.* library
- [Database Libraries](articles/database-libraries.md) — MySQL, PostgreSQL, SQLite with Repository pattern
- [Mail](articles/mail.md) — sending email with templates
- [Storage](articles/storage.md) — S3 and MinIO object storage
- [Security](articles/security.md) — TOTP two-factor authentication
- [System Monitoring](articles/system-monitoring.md) — CPU and memory stats
- [Social](articles/social.md) — Discord webhooks and Steam auth
- [Game Server Queries](articles/game-queries.md) — Valve RCON, Source UDP, Minecraft
- [API Reference](api/index.md) — full API documentation
