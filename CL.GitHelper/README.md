# CodeLogic.GitHelper

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GitHelper)](https://www.nuget.org/packages/CodeLogic.GitHelper)

Git repository management library for [CodeLogic](https://github.com/Media2A/CodeLogic) applications, powered by LibGit2Sharp.

## Install

```
dotnet add package CodeLogic.GitHelper
```

## Quick Start

```csharp
var gitLib = new GitHelperLibrary();
// After library initialization via CodeLogic framework:

var repo = await gitLib.GetRepositoryAsync("Default");
var info = await repo.GetRepositoryInfoAsync();
Console.WriteLine($"Branch: {info.Value!.CurrentBranch}, Dirty: {info.Value.IsDirty}");

var status = await repo.GetStatusAsync();
await repo.CommitAsync(new CommitOptions { Message = "Update config" });
```

## Features

- **Clone, fetch, pull, push** — full remote workflow with HTTPS authentication
- **Branch management** — list, checkout, and inspect branches (local and remote)
- **Commit and status** — stage changes, commit with metadata, and query working-tree status
- **Reset / sync helpers** — `ResetHardAsync` and idempotent `EnsureUpToDateAsync`
- **Commit log** — read commit history with `GetCommitLogAsync`
- **Batch operations** — `FetchAllAsync`, `GetAllStatusAsync`, and `ExecuteOnAllAsync` across all configured repositories
- **Repository caching** — configurable in-memory cache with automatic eviction

## Result handling

Every Git operation returns a `GitResult<T>` rather than throwing. Inspect it before
using the payload:

```csharp
var result = await repo.PullAsync();
if (result.IsSuccess)
    Console.WriteLine($"Pulled in {result.Diagnostics.Duration?.TotalSeconds:F2}s");
else
    Console.WriteLine($"Pull failed: {result.ErrorMessage}");
```

- `IsSuccess` — whether the operation succeeded.
- `Value` — the typed payload (non-null only on success).
- `ErrorMessage` / `Exception` — failure details.
- `Diagnostics` — timing (`Duration`), counters, and timestamped `Messages`.

## Repository operations

A `GitRepository` exposes the full single-repo workflow. All write operations are
serialized by an internal async lock, and most accept an `*Options` object (with a
`CancellationToken`) that can be omitted for defaults.

```csharp
var repo = await gitLib.GetRepositoryAsync("Default");

// Clone (fails if the local directory exists and is non-empty)
await repo.CloneAsync(new CloneOptions { BranchName = "main" });

// Fetch / pull / push
await repo.FetchAsync(new FetchOptions { Prune = true });
await repo.PullAsync();
await repo.PushAsync(new PushOptions { Force = false });

// Branches
var branches = await repo.ListBranchesAsync(includeRemote: true);
await repo.CheckoutBranchAsync("develop");

// Commit specific files
await repo.CommitAsync(new CommitOptions
{
    Message = "Update config",
    AuthorName = "Bot",
    AuthorEmail = "bot@example.com",
    FilesToStage = ["config.json"]
});

// Commit history (0 = all)
var log = await repo.GetCommitLogAsync(maxCount: 10);
foreach (var c in log.Value!)
    Console.WriteLine($"{c.ShortSha} {c.ShortMessage}");
```

### Reset and one-call sync

```csharp
// Hard-reset the working tree to origin/<branch> (defaults to the tracked upstream)
await repo.ResetHardAsync("main");

// Idempotent: clones if missing, otherwise fetches + hard-resets to the remote tip.
// Branch defaults to the repository's configured DefaultBranch.
var info = await repo.EnsureUpToDateAsync();
```

## Manager and batch operations

`GetManager()` returns the `GitManager` that owns the repository pool, runtime
registration, the cache, and cross-repository batch calls. Convenience wrappers for the
most common batch calls are also exposed directly on the library.

```csharp
// Run any operation across every configured repository (bounded concurrency)
var manager = gitLib.GetManager();
var infos = await manager.ExecuteOnAllAsync(
    (repo, id) => repo.GetRepositoryInfoAsync());

// Built-in batch helpers (also available on the manager)
Dictionary<string, GitResult<bool>> fetched = await gitLib.FetchAllAsync();
Dictionary<string, GitResult<RepositoryStatus>> statuses = await gitLib.GetAllStatusAsync();

// Per-repository health check (id → healthy)
Dictionary<string, bool> health = await manager.HealthCheckAsync();
```

### Runtime registration and cache control

```csharp
// Add a repository discovered after startup
gitLib.RegisterRepository(new RepositoryConfiguration
{
    Id = "Docs",
    RepositoryUrl = "https://github.com/org/docs.git",
    LocalPath = "docs",
    DefaultBranch = "main"
});
await manager.UnregisterRepositoryAsync("Docs");

// Inspect and clear the in-memory cache
CacheStats? stats = gitLib.GetCacheStats();
await gitLib.ClearCacheAsync();
```

## Authentication

HTTPS authentication is driven by the `Username` / `Password` fields on each repository
configuration. When only `Password` is set (a Personal Access Token), the username is
sent as `x-access-token` — the placeholder GitHub accepts for PAT-only auth. Leave both
blank for anonymous access.

The `SshKeyPath` / `SshPassphrase` configuration fields are reserved for future use but
are **not currently wired** — the shipped LibGit2Sharp 0.30 managed binary has no native
SSH transport, so use HTTPS URLs.

## Configuration

Config file: `config.githelper.json`

```json
{
  "Enabled": true,
  "BaseDirectory": "",
  "DefaultTimeoutSeconds": 300,
  "MaxConcurrentOperations": 3,
  "EnableRepositoryCaching": true,
  "CacheTimeoutMinutes": 30,
  "Repositories": [
    {
      "Id": "Default",
      "Name": "My Repository",
      "RepositoryUrl": "https://github.com/username/repository.git",
      "LocalPath": "my-repo",
      "DefaultBranch": "main",
      "Username": null,
      "Password": null,
      "AutoFetch": false,
      "AutoFetchIntervalMinutes": 0,
      "TimeoutSeconds": 300
    }
  ]
}
```

## Documentation

Full API docs: [https://github.com/Media2A/CodeLogic.Libs](https://github.com/Media2A/CodeLogic.Libs)

## Requirements

- .NET 10.0+
- [CodeLogic 3.x or 4.x](https://github.com/Media2A/CodeLogic)
- LibGit2Sharp 0.30+

## License

MIT — see [LICENSE](https://github.com/Media2A/CodeLogic.Libs/blob/main/LICENSE)
