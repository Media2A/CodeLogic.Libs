using CodeLogic.Core.Configuration;
using System.ComponentModel.DataAnnotations;

namespace CL.GitHelper.Models;

/// <summary>
/// Configuration for a single Git repository entry.
/// </summary>
public sealed class RepositoryConfiguration
{
    /// <summary>
    /// Unique identifier for this repository (e.g., "Default", "MyRepo").
    /// </summary>
    public string Id { get; set; } = "Default";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public string Name { get; set; } = "Default Repository";

    /// <summary>
    /// Remote repository URL — HTTPS or SSH (e.g., "https://github.com/org/repo.git").
    /// </summary>
    public string RepositoryUrl { get; set; } = "";

    /// <summary>
    /// Local directory path where the repository is (or will be) cloned.
    /// Relative paths are resolved from <see cref="GitHelperConfig.BaseDirectory"/>.
    /// </summary>
    public string LocalPath { get; set; } = "";

    /// <summary>
    /// Default branch name used when no branch is specified (e.g., "main", "master").
    /// </summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    /// Username for HTTPS authentication. Leave null for anonymous or SSH access.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password or Personal Access Token for HTTPS authentication.
    /// Leave null for anonymous or SSH access.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Path to an SSH private key file for SSH-URL repositories.
    /// </summary>
    public string? SshKeyPath { get; set; }

    /// <summary>
    /// Passphrase for the SSH private key, if encrypted.
    /// </summary>
    public string? SshPassphrase { get; set; }

    /// <summary>
    /// Whether to automatically fetch on library start-up.
    /// </summary>
    public bool AutoFetch { get; set; } = false;

    /// <summary>
    /// How often (in minutes) to auto-fetch in the background. 0 disables the timer.
    /// </summary>
    public int AutoFetchIntervalMinutes { get; set; } = 0;

    /// <summary>
    /// Per-repository operation timeout in seconds.
    /// </summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Validates that at minimum a URL and local path are set.
    /// </summary>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(RepositoryUrl) &&
        !string.IsNullOrWhiteSpace(LocalPath);
}

/// <summary>
/// Root configuration for <c>CL.GitHelper</c>.
/// Serialized to / from <c>config.githelper.json</c> in the library's config directory.
/// </summary>
[ConfigSection("githelper")]
public sealed class GitHelperConfig : ConfigModelBase
{
    /// <summary>
    /// Whether the library is active. When false, all operations are no-ops.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Base directory for resolving relative <see cref="RepositoryConfiguration.LocalPath"/> values.
    /// Defaults to a <c>repositories</c> sub-folder inside the library's data directory.
    /// Leave empty to resolve paths relative to the process working directory.
    /// </summary>
    public string BaseDirectory { get; set; } = "";

    /// <summary>
    /// List of configured repositories. At least one must be present.
    /// </summary>
    public List<RepositoryConfiguration> Repositories { get; set; } =
    [
        new RepositoryConfiguration
        {
            Id = "Default",
            Name = "My Repository",
            RepositoryUrl = "https://github.com/username/repository.git",
            LocalPath = "my-repo",
            DefaultBranch = "main"
        }
    ];

    /// <summary>
    /// Default operation timeout applied when a repository config does not specify one.
    /// </summary>
    [Range(1, 3600)]
    public int DefaultTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum number of repositories operated on concurrently during batch calls.
    /// </summary>
    [Range(1, 20)]
    public int MaxConcurrentOperations { get; set; } = 3;

    /// <summary>
    /// Whether to keep <c>GitRepository</c> instances in a memory cache between calls.
    /// </summary>
    public bool EnableRepositoryCaching { get; set; } = true;

    /// <summary>
    /// How long (minutes) a cached repository entry lives before being evicted. 0 = never expire.
    /// </summary>
    [Range(0, 1440)]
    public int CacheTimeoutMinutes { get; set; } = 30;

    /// <summary>Returns the <see cref="RepositoryConfiguration"/> with the given ID, or null.</summary>
    public RepositoryConfiguration? GetRepository(string id) =>
        Repositories.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override ConfigValidationResult Validate()
    {
        var errors = new List<string>();

        if (Repositories == null || Repositories.Count == 0)
            errors.Add("At least one repository must be configured.");

        if (DefaultTimeoutSeconds is < 1 or > 3600)
            errors.Add("DefaultTimeoutSeconds must be between 1 and 3600.");

        if (MaxConcurrentOperations is < 1 or > 20)
            errors.Add("MaxConcurrentOperations must be between 1 and 20.");

        if (CacheTimeoutMinutes is < 0 or > 1440)
            errors.Add("CacheTimeoutMinutes must be between 0 and 1440.");

        return errors.Count > 0
            ? ConfigValidationResult.Invalid(errors)
            : ConfigValidationResult.Valid();
    }
}
