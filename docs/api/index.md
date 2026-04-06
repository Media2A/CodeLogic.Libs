# API Reference

This section contains the complete API reference for all CodeLogic Libraries (`CL.*`), generated from XML documentation comments in the source code.

## Libraries

| Library | Root Namespace | Description |
|---------|---------------|-------------|
| CL.Common | `CL.Common` | Utilities: hashing, ID generation, extensions |
| CL.GitHelper | `CL.GitHelper` | Git operations via LibGit2Sharp |
| CL.Mail | `CL.Mail` | SMTP/IMAP email with templates |
| CL.MySQL2 | `CL.MySQL2` | MySQL with pooling and Repository pattern |
| CL.NetUtils | `CL.NetUtils` | DNSBL and GeoIP2 lookups |
| CL.PostgreSQL | `CL.PostgreSQL` | PostgreSQL with multi-database support |
| CL.SQLite | `CL.SQLite` | SQLite with custom pool and migrations |
| CL.SocialConnect | `CL.SocialConnect` | Discord and Steam integrations |
| CL.StorageS3 | `CL.StorageS3` | Amazon S3 and MinIO storage |
| CL.SystemStats | `CL.SystemStats` | CPU and memory monitoring |
| CL.TwoFactorAuth | `CL.TwoFactorAuth` | TOTP 2FA with QR codes |

## Common Patterns

### Repository Pattern

The database libraries (CL.MySQL2, CL.PostgreSQL, CL.SQLite) share a consistent `Repository<T>` pattern:

```csharp
// All three database libraries expose:
IRepository<T> GetRepository<T>() where T : class, new();

// Repository methods:
Task<T?> FindAsync(Expression<Func<T, bool>> predicate);
Task<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>> predicate);
Task<T> InsertAsync(T entity);
Task<T> UpdateAsync(T entity);
Task<bool> DeleteAsync(Expression<Func<T, bool>> predicate);
Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null);
```

### QueryBuilder Pattern

```csharp
// MySQL2 / PostgreSQL
var results = await library.Query<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.CreatedAt)
    .Limit(20)
    .ToListAsync();

// SQLite
var sqliteResults = await sqlite.GetQueryBuilder<User>()
    .Where(u => u.IsActive)
    .OrderBy(u => u.CreatedAt)
    .Limit(20)
    .ToListAsync();
```

### Health Check Pattern

All libraries implement `HealthCheckAsync()` returning `HealthStatus`:

```csharp
var status = await library.HealthCheckAsync();
// status.Status: Healthy | Degraded | Unhealthy
// status.Message: human-readable description
// status.Data: optional structured metrics
```
