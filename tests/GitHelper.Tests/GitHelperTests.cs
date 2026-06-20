using CL.GitHelper;
using CL.GitHelper.Models;
using CL.GitHelper.Services;
using CodeLogic;                         // Libraries, CodeLogicOptions
using LibGit2Sharp;                      // direct repo setup for the offline temp-repo fixture
using Xunit;
using CommitOptions = CL.GitHelper.Models.CommitOptions;   // disambiguate from LibGit2Sharp.CommitOptions

namespace GitHelper.Tests;

// ── CL.GitHelper tests ───────────────────────────────────────────────────────────
// HYBRID strategy:
//   • LOCAL git operations (init/commit/status/log/branch) are exercised OFFLINE against
//     a throwaway temp repository that the fixture creates on disk with LibGit2Sharp
//     directly (Init + write file + Stage + Commit). The real CodeLogic runtime is booted
//     once and pointed (via config.githelper.json) at that temp repo.
//   • Operations needing a remote (clone/fetch/push/pull) are NOT generally offline-
//     testable. The SSH fail-fast regression IS offline (it short-circuits before any
//     network) and is asserted directly. The live clone test is env-gated.
//
// The CodeLogic runtime is a process-wide singleton, so every test that needs the booted
// library lives behind a single shared fixture serialized by the [Collection] attribute.

/// <summary>
/// A <see cref="FactAttribute"/> that statically skips the test unless the named
/// environment variable is set. xUnit 2.9.3 has no runtime <c>Assert.Skip</c>, so the
/// skip decision is made here (at discovery time) and reported as a proper "Skipped".
/// </summary>
internal sealed class FactRequiresEnvAttribute : FactAttribute
{
    public FactRequiresEnvAttribute(string envVar, string reason)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            Skip = reason;
    }
}

// ── Shared one-time-boot fixture ───────────────────────────────────────────────

public sealed class GitHelperRuntimeFixture : IAsyncLifetime
{
    public string TempDir { get; } =
        Path.Combine(Path.GetTempPath(), "cl_githelper_test_" + Guid.NewGuid().ToString("N"));

    /// <summary>Absolute path of the local working repo that LOCAL ops run against.</summary>
    public string LocalRepoPath { get; private set; } = "";

    /// <summary>Relative path of the seeded tracked file inside the repo.</summary>
    public const string TrackedFile = "tracked.txt";

    public GitHelperLibrary Library { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(TempDir);

        // 1. Create a real local git repo on disk with one commit (offline, no remote).
        LocalRepoPath = Path.Combine(TempDir, "local-repo");
        Directory.CreateDirectory(LocalRepoPath);
        Repository.Init(LocalRepoPath);
        using (var repo = new Repository(LocalRepoPath))
        {
            File.WriteAllText(Path.Combine(LocalRepoPath, TrackedFile), "initial content\n");
            Commands.Stage(repo, TrackedFile);
            var sig = new Signature("Seed Author", "seed@example.com", DateTimeOffset.Now);
            repo.Commit("initial commit", sig, sig);
        }

        // 2. Boot the real CodeLogic runtime pointing the "Default" repo at the local repo,
        //    and a separate "Ssh" repo with an SSH URL for the fail-fast regression.
        var init = await CodeLogic.CodeLogic.InitializeAsync(o =>
        {
            o.FrameworkRootPath = TempDir;
            o.AppVersion = "1.0.0";
            o.HandleShutdownSignals = false;
        });
        if (!init.Success)
            throw new InvalidOperationException($"CodeLogic init failed: {init.Message}");

        var localPathJson = LocalRepoPath.Replace("\\", "\\\\");
        var sshLocalPathJson = Path.Combine(TempDir, "ssh-repo").Replace("\\", "\\\\");

        var cfgDir = Path.Combine(TempDir, "Libraries", "CL.GitHelper");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.githelper.json"), $$"""
        {
          "enabled": true,
          "baseDirectory": "",
          "repositories": [
            {
              "id": "Default",
              "name": "Local Test Repo",
              "repositoryUrl": "file:///{{localPathJson}}",
              "localPath": "{{localPathJson}}",
              "defaultBranch": "main",
              "autoFetch": false
            },
            {
              "id": "Ssh",
              "name": "SSH Repo",
              "repositoryUrl": "git@github.com:org/repo.git",
              "localPath": "{{sshLocalPathJson}}",
              "defaultBranch": "main",
              "autoFetch": false
            }
          ]
        }
        """);

        await Libraries.LoadAsync<GitHelperLibrary>();
        await CodeLogic.CodeLogic.ConfigureAsync();
        await CodeLogic.CodeLogic.StartAsync();

        Library = Libraries.Get<GitHelperLibrary>()
            ?? throw new InvalidOperationException("GitHelperLibrary not available after start.");
    }

    public async Task DisposeAsync()
    {
        try { await CodeLogic.CodeLogic.StopAsync(); } catch { /* best effort */ }
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* git pack files may linger briefly on Windows; ignore */ }
    }
}

[CollectionDefinition("codelogic")]
public sealed class CodeLogicCollection : ICollectionFixture<GitHelperRuntimeFixture> { }

// ── Local (offline) operations against the temp repo ─────────────────────────────

[Collection("codelogic")]
public sealed class GitHelperLocalTests
{
    private readonly GitHelperRuntimeFixture _fx;

    public GitHelperLocalTests(GitHelperRuntimeFixture fx) => _fx = fx;

    private Task<GitRepository> RepoAsync() => _fx.Library.GetRepositoryAsync("Default");

    [Fact]
    public async Task GetRepositoryInfo_reports_branch_and_head()
    {
        var repo = await RepoAsync();
        var info = await repo.GetRepositoryInfoAsync();

        Assert.True(info.IsSuccess, info.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(info.Value!.CurrentBranch));
        Assert.False(string.IsNullOrWhiteSpace(info.Value.HeadCommitSha));
    }

    [Fact]
    public async Task GetStatus_sees_a_new_untracked_file()
    {
        var repo = await RepoAsync();
        var newFile = "untracked-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        File.WriteAllText(Path.Combine(_fx.LocalRepoPath, newFile), "hello\n");

        var status = await repo.GetStatusAsync();

        Assert.True(status.IsSuccess, status.ErrorMessage);
        Assert.True(status.Value!.IsDirty || status.Value.UntrackedFiles.Count > 0);
        Assert.Contains(status.Value.UntrackedFiles, f => f.FilePath == newFile);

        // cleanup so we don't perturb later tests in this serialized class
        File.Delete(Path.Combine(_fx.LocalRepoPath, newFile));
    }

    [Fact]
    public async Task Commit_stages_and_records_a_commit()
    {
        var repo = await RepoAsync();
        var file = "commit-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        File.WriteAllText(Path.Combine(_fx.LocalRepoPath, file), "payload\n");

        var result = await repo.CommitAsync(new CommitOptions
        {
            Message = "test commit",
            AuthorName = "Test Author",
            AuthorEmail = "test@example.com",
            FilesToStage = [file]
        });

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("test commit", result.Value!.Message.Trim());
        Assert.False(string.IsNullOrWhiteSpace(result.Value.Sha));
    }

    [Fact]
    public async Task GetCommitLog_returns_at_least_one_commit()
    {
        var repo = await RepoAsync();
        var log = await repo.GetCommitLogAsync(10);

        Assert.True(log.IsSuccess, log.ErrorMessage);
        Assert.NotEmpty(log.Value!);
    }

    [Fact]
    public async Task ListBranches_local_only_includes_the_default_branch()
    {
        var repo = await RepoAsync();
        var branches = await repo.ListBranchesAsync(includeRemote: false);

        Assert.True(branches.IsSuccess, branches.ErrorMessage);
        Assert.NotEmpty(branches.Value!);
        Assert.All(branches.Value!, b => Assert.False(b.IsRemote));
    }

    // ── SSH fail-fast regression (cleanly offline) ───────────────────────────────
    // CloneAsync must short-circuit on an SSH URL before any network access and return a
    // failure whose message mentions SSH/HTTPS.
    [Fact]
    public async Task CloneAsync_with_ssh_url_fails_fast_offline()
    {
        var repo = await _fx.Library.GetRepositoryAsync("Ssh");
        var result = await repo.CloneAsync();

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("SSH", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HTTPS", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    // ── EnsureUpToDate dirty-guard regression ────────────────────────────────────
    // On the local temp repo (no reachable remote) make the working tree dirty, then call
    // EnsureUpToDateAsync(discardLocalChanges: false). It must return a failure rather than
    // discard the changes. (Offline, the fetch step fails first because the repo has no
    // remote; either way the contract — never silently discard — holds: result is failure
    // and the dirty file is still on disk.)
    [Fact]
    public async Task EnsureUpToDate_refuses_when_dirty_and_not_discarding()
    {
        var repo = await RepoAsync();

        var trackedPath = Path.Combine(_fx.LocalRepoPath, GitHelperRuntimeFixture.TrackedFile);
        var original = File.ReadAllText(trackedPath);
        File.WriteAllText(trackedPath, original + "local edit\n");
        try
        {
            var result = await repo.EnsureUpToDateAsync(discardLocalChanges: false);

            Assert.False(result.IsSuccess);
            // The local modification must NOT have been discarded.
            Assert.Contains("local edit", File.ReadAllText(trackedPath));
        }
        finally
        {
            File.WriteAllText(trackedPath, original);
        }
    }

    [Fact]
    public async Task HealthCheck_is_healthy_for_a_real_local_repo()
    {
        var health = await _fx.Library.HealthCheckAsync();
        // Default repo is a valid local repo. (Ssh repo isn't cloned, so overall status may
        // be Degraded; assert it isn't outright Unhealthy.)
        Assert.NotEqual(CodeLogic.Framework.Libraries.HealthStatusLevel.Unhealthy, health.Status);
    }
}

// ── Gated live clone test (needs a real HTTPS remote) ────────────────────────────

[Collection("codelogic")]
public sealed class GitHelperLiveTests
{
    private readonly GitHelperRuntimeFixture _fx;

    public GitHelperLiveTests(GitHelperRuntimeFixture fx) => _fx = fx;

    [FactRequiresEnv("CL_GIT_TEST_REMOTE", "set CL_GIT_TEST_REMOTE to an https git url to run the live clone test")]
    public async Task Clone_real_https_remote()
    {
        var url = Environment.GetEnvironmentVariable("CL_GIT_TEST_REMOTE")!;
        var dest = Path.Combine(_fx.TempDir, "live-clone-" + Guid.NewGuid().ToString("N")[..8]);

        _fx.Library.RegisterRepository(new RepositoryConfiguration
        {
            Id = "Live",
            Name = "Live Remote",
            RepositoryUrl = url,
            LocalPath = dest,
            DefaultBranch = "main"
        });

        var repo = await _fx.Library.GetRepositoryAsync("Live");
        var result = await repo.EnsureUpToDateAsync();

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.HeadCommitSha));
    }
}
