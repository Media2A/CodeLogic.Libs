# CL.GitHelper

[![NuGet](https://img.shields.io/nuget/v/CodeLogic.GitHelper)](https://www.nuget.org/packages/CodeLogic.GitHelper)

Git repository management library for CodeLogic 3 applications, powered by LibGit2Sharp.

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

- **Clone, fetch, pull, push** — full remote workflow with HTTPS and SSH authentication
- **Branch management** — list, checkout, and inspect branches (local and remote)
- **Commit and status** — stage changes, commit with metadata, and query working-tree status
- **Batch operations** — `FetchAllAsync` and `GetAllStatusAsync` across all configured repositories
- **Repository caching** — configurable in-memory cache with automatic eviction

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
- CodeLogic 3.0.0+
- LibGit2Sharp 0.30+

## License

MIT -- see [LICENSE](../LICENSE) for details.
