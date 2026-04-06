# CodeLogic.Libs

Official CL.* library integrations for the CodeLogic 3 framework.
Each library implements `ILibrary` and plugs into the CodeLogic lifecycle.

## Libraries

| Library | Description |
|---------|-------------|
| CL.Core | Shared utilities — hashing, caching, imaging, networking, generators |
| CL.SQLite | SQLite with LINQ query builder, table sync, migrations |
| CL.MySQL2 | MySQL with full ORM-like features, connection pooling, health checks |
| CL.PostgreSQL | PostgreSQL integration |
| CL.Mail | SMTP/IMAP email with template engine |
| CL.SystemStats | Cross-platform CPU, memory, process statistics |
| CL.GitHelper | Git repository management |
| CL.NetUtils | DNS, IP geolocation, DNSBL checking |
| CL.SocialConnect | Discord webhooks, Steam API |
| CL.StorageS3 | Amazon S3 storage |
| CL.TwoFactorAuth | TOTP 2FA + QR code generation |

## Dependency

All libs reference `CodeLogic` (the framework). No other shared dependency.

## License

MIT — see [LICENSE](LICENSE)
