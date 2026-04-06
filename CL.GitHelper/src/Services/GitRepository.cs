using CL.GitHelper.Models;
using CodeLogic.Core.Logging;
using LibGit2Sharp;

// Alias our model types that clash with LibGit2Sharp names.
using GitCloneOptions = CL.GitHelper.Models.CloneOptions;
using GitFetchOptions = CL.GitHelper.Models.FetchOptions;
using GitPullOptions = CL.GitHelper.Models.PullOptions;
using GitPushOptions = CL.GitHelper.Models.PushOptions;
using GitCommitOptions = CL.GitHelper.Models.CommitOptions;
using GitRepoStatus = CL.GitHelper.Models.RepositoryStatus;

namespace CL.GitHelper.Services;

/// <summary>
/// Wraps a LibGit2Sharp <see cref="Repository"/> and exposes typed Git operations.
/// Instances are created and cached by <see cref="GitManager"/>.
/// All write operations are guarded by an async semaphore to prevent concurrent access.
/// </summary>
public sealed class GitRepository : IDisposable
{
    private readonly RepositoryConfiguration _config;
    private readonly string _resolvedPath;
    private readonly ILogger? _logger;
    private Repository? _repo;
    private bool _disposed;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Initialises the repository service.</summary>
    /// <param name="config">Repository configuration entry.</param>
    /// <param name="resolvedPath">Absolute local path (pre-resolved by <see cref="GitManager"/>).</param>
    /// <param name="logger">Optional scoped logger.</param>
    internal GitRepository(RepositoryConfiguration config, string resolvedPath, ILogger? logger = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _resolvedPath = resolvedPath;
        _logger = logger;
    }

    // ── Clone ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clones the configured remote repository into the local path.
    /// Fails if the local directory already exists and is non-empty.
    /// </summary>
    /// <param name="options">Optional clone settings.</param>
    public async Task<GitResult<RepositoryInfo>> CloneAsync(GitCloneOptions? options = null)
    {
        options ??= new GitCloneOptions();
        var diag = new GitDiagnostics();
        diag.AddMessage("Starting clone");

        try
        {
            if (Directory.Exists(_resolvedPath))
            {
                if (Directory.GetFileSystemEntries(_resolvedPath).Length > 0)
                    return GitResult<RepositoryInfo>.Fail(
                        $"Directory '{_resolvedPath}' already exists and is not empty.", null, diag);
            }
            else
            {
                Directory.CreateDirectory(_resolvedPath);
                diag.AddMessage($"Created directory: {_resolvedPath}");
            }

            _logger?.Info($"Cloning {_config.RepositoryUrl} → {_resolvedPath}");

            await Task.Run(() =>
                Repository.Clone(_config.RepositoryUrl, _resolvedPath),
                options.CancellationToken);

            diag.Complete();
            _logger?.Info($"Clone complete in {diag.Duration?.TotalSeconds:F2}s");

            _repo = new Repository(_resolvedPath);
            var info = BuildRepositoryInfo();

            return GitResult<RepositoryInfo>.Ok(info, diag);
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Clone failed: {ex.Message}", ex);
            return GitResult<RepositoryInfo>.Fail($"Clone failed: {ex.Message}", ex, diag);
        }
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches changes from the configured remote without merging.
    /// </summary>
    /// <param name="options">Optional fetch settings.</param>
    public async Task<GitResult<bool>> FetchAsync(GitFetchOptions? options = null)
    {
        options ??= new GitFetchOptions();
        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                diag.AddMessage($"Fetching from {options.RemoteName}");
                _logger?.Info($"Fetching '{_config.Id}' from {options.RemoteName}");

                var remote = _repo!.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitResult<bool>.Fail($"Remote '{options.RemoteName}' not found", null, diag);

                var refSpecs = remote.FetchRefSpecs.Select(s => s.Specification);

                await Task.Run(() =>
                    Commands.Fetch(_repo, options.RemoteName, refSpecs, null, null),
                    options.CancellationToken);

                diag.Complete();
                _logger?.Info($"Fetch complete in {diag.Duration?.TotalSeconds:F2}s");
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Fetch failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Fetch failed: {ex.Message}", ex, diag);
        }
    }

    // ── Pull ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches and integrates remote changes.
    /// </summary>
    /// <param name="options">Optional pull settings.</param>
    public async Task<GitResult<bool>> PullAsync(GitPullOptions? options = null)
    {
        options ??= new GitPullOptions();
        var diag = new GitDiagnostics();

        try
        {
            var fetchResult = await FetchAsync(options.FetchOptions);
            if (!fetchResult.IsSuccess)
                return GitResult<bool>.Fail(fetchResult.ErrorMessage!, fetchResult.Exception, diag);

            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                diag.AddMessage("Merging fetched changes");
                _logger?.Info($"Pulling '{_config.Id}'");

                var sig = GetSignature();
                await Task.Run(() => Commands.Pull(_repo!, sig, null), options.CancellationToken);

                diag.Complete();
                _logger?.Info($"Pull complete in {diag.Duration?.TotalSeconds:F2}s");
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Pull failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Pull failed: {ex.Message}", ex, diag);
        }
    }

    // ── Push ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes local commits to the remote.
    /// </summary>
    /// <param name="options">Optional push settings.</param>
    public async Task<GitResult<bool>> PushAsync(GitPushOptions? options = null)
    {
        options ??= new GitPushOptions();
        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(options.CancellationToken);
            try
            {
                var branch = options.BranchName ?? _repo!.Head.FriendlyName;
                diag.AddMessage($"Pushing {branch} to {options.RemoteName}");
                _logger?.Info($"Pushing '{_config.Id}':{branch} to {options.RemoteName}");

                var remote = _repo!.Network.Remotes[options.RemoteName];
                if (remote is null)
                    return GitResult<bool>.Fail($"Remote '{options.RemoteName}' not found", null, diag);

                var refSpec = options.Force
                    ? $"+refs/heads/{branch}:refs/heads/{branch}"
                    : $"refs/heads/{branch}:refs/heads/{branch}";

                await Task.Run(() =>
                    _repo.Network.Push(remote, refSpec, (LibGit2Sharp.PushOptions?)null),
                    options.CancellationToken);

                diag.Complete();
                _logger?.Info($"Push complete in {diag.Duration?.TotalSeconds:F2}s");
                return GitResult<bool>.Ok(true, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Push failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Push failed: {ex.Message}", ex, diag);
        }
    }

    // ── Commit ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a commit from the current index.
    /// Optionally stages specific files before committing.
    /// </summary>
    /// <param name="options">Commit options including message and optional files to stage.</param>
    public async Task<GitResult<CommitInfo>> CommitAsync(GitCommitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Message))
            return GitResult<CommitInfo>.Fail("Commit message must not be empty.");

        var diag = new GitDiagnostics();

        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync();
            try
            {
                if (options.FilesToStage?.Count > 0)
                {
                    foreach (var file in options.FilesToStage)
                    {
                        Commands.Stage(_repo!, file);
                        diag.AddMessage($"Staged: {file}");
                    }
                }

                var sig = GetSignature(options.AuthorName, options.AuthorEmail);

                var commit = await Task.Run(() =>
                    _repo!.Commit(options.Message, sig, sig));

                var info = ConvertCommit(commit);
                diag.CommitsProcessed = 1;
                diag.Complete();

                _logger?.Info($"Committed {info.ShortSha}: {info.ShortMessage}");
                return GitResult<CommitInfo>.Ok(info, diag);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            diag.Complete();
            _logger?.Error($"Commit failed: {ex.Message}", ex);
            return GitResult<CommitInfo>.Fail($"Commit failed: {ex.Message}", ex, diag);
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current working-tree status of the repository.
    /// </summary>
    public async Task<GitResult<GitRepoStatus>> GetStatusAsync()
    {
        var diag = new GitDiagnostics();
        try
        {
            await EnsureOpenAsync();

            var raw = await Task.Run(() => _repo!.RetrieveStatus());
            var status = new GitRepoStatus { IsDirty = raw.IsDirty };

            foreach (var item in raw)
            {
                var entry = new FileStatusEntry
                {
                    FilePath = item.FilePath,
                    Status = item.State.ToString(),
                    IsStaged = item.State.HasFlag(FileStatus.ModifiedInIndex) ||
                               item.State.HasFlag(FileStatus.NewInIndex)
                };

                if (item.State.HasFlag(FileStatus.Conflicted))
                    status.ConflictedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.Ignored))
                    status.IgnoredFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.NewInIndex))
                    status.StagedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.ModifiedInIndex))
                    status.StagedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.NewInWorkdir))
                    status.UntrackedFiles.Add(entry);
                else if (item.State.HasFlag(FileStatus.ModifiedInWorkdir))
                    status.ModifiedFiles.Add(entry);
            }

            diag.FilesChanged = status.TotalChangedFiles;
            diag.Complete();
            return GitResult<GitRepoStatus>.Ok(status, diag);
        }
        catch (Exception ex)
        {
            diag.Complete();
            return GitResult<GitRepoStatus>.Fail($"Failed to get status: {ex.Message}", ex, diag);
        }
    }

    // ── Repository info ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns summary metadata about the repository.
    /// </summary>
    public async Task<GitResult<RepositoryInfo>> GetRepositoryInfoAsync()
    {
        try
        {
            await EnsureOpenAsync();
            var info = BuildRepositoryInfo();
            return GitResult<RepositoryInfo>.Ok(info);
        }
        catch (Exception ex)
        {
            return GitResult<RepositoryInfo>.Fail($"Failed to get repository info: {ex.Message}", ex);
        }
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all branches in the repository.
    /// </summary>
    /// <param name="includeRemote">When true, remote-tracking branches are included.</param>
    public async Task<GitResult<List<BranchInfo>>> ListBranchesAsync(bool includeRemote = true)
    {
        try
        {
            await EnsureOpenAsync();

            var branches = await Task.Run(() =>
            {
                var list = new List<BranchInfo>();
                foreach (var branch in _repo!.Branches)
                {
                    if (!includeRemote && branch.IsRemote) continue;
                    list.Add(new BranchInfo
                    {
                        Name = branch.CanonicalName,
                        FriendlyName = branch.FriendlyName,
                        IsCurrent = branch.IsCurrentRepositoryHead,
                        IsRemote = branch.IsRemote,
                        IsTracking = branch.IsTracking,
                        TrackedBranchName = branch.TrackedBranch?.FriendlyName,
                        TipSha = branch.Tip?.Sha ?? "",
                        AheadBy = branch.TrackingDetails.AheadBy,
                        BehindBy = branch.TrackingDetails.BehindBy
                    });
                }
                return list;
            });

            return GitResult<List<BranchInfo>>.Ok(branches);
        }
        catch (Exception ex)
        {
            return GitResult<List<BranchInfo>>.Fail($"Failed to list branches: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Checks out the named branch.
    /// </summary>
    /// <param name="branchName">Branch to check out.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GitResult<bool>> CheckoutBranchAsync(
        string branchName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureOpenAsync();
            await _lock.WaitAsync(cancellationToken);
            try
            {
                await Task.Run(() => Commands.Checkout(_repo!, branchName), cancellationToken);
                _logger?.Info($"Checked out branch '{branchName}'");
                return GitResult<bool>.Ok(true);
            }
            finally
            {
                _lock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error($"Checkout failed: {ex.Message}", ex);
            return GitResult<bool>.Fail($"Checkout failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Retrieves the commit log for the current branch.
    /// </summary>
    /// <param name="maxCount">Maximum number of commits to return. 0 returns all.</param>
    public async Task<GitResult<List<CommitInfo>>> GetCommitLogAsync(int maxCount = 20)
    {
        try
        {
            await EnsureOpenAsync();

            var commits = await Task.Run(() =>
            {
                var filter = new CommitFilter
                {
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                };
                var commits = _repo!.Commits.QueryBy(filter).AsEnumerable();
                if (maxCount > 0) commits = commits.Take(maxCount);
                return commits.Select(ConvertCommit).ToList();
            });

            return GitResult<List<CommitInfo>>.Ok(commits);
        }
        catch (Exception ex)
        {
            return GitResult<List<CommitInfo>>.Fail($"Failed to get commit log: {ex.Message}", ex);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task EnsureOpenAsync()
    {
        if (_repo is not null) return;

        if (!Directory.Exists(_resolvedPath) ||
            !Directory.Exists(Path.Combine(_resolvedPath, ".git")))
            throw new InvalidOperationException(
                $"Repository not found at '{_resolvedPath}'. Clone it first.");

        await Task.Run(() => _repo = new Repository(_resolvedPath));
    }

    private Signature GetSignature(string? name = null, string? email = null)
    {
        name ??= _repo?.Config.Get<string>("user.name")?.Value ?? "CodeLogic";
        email ??= _repo?.Config.Get<string>("user.email")?.Value ?? "codelogic@localhost";
        return new Signature(name, email, DateTimeOffset.Now);
    }

    private RepositoryInfo BuildRepositoryInfo()
    {
        var status = _repo!.RetrieveStatus();
        return new RepositoryInfo
        {
            Id = _config.Id,
            Name = _config.Name,
            LocalPath = _resolvedPath,
            RemoteUrl = _repo!.Network.Remotes.FirstOrDefault()?.Url ?? "",
            CurrentBranch = _repo.Head.FriendlyName,
            HeadCommitSha = _repo.Head.Tip?.Sha ?? "",
            IsBare = _repo.Info.IsBare,
            State = _repo.Info.CurrentOperation.ToString(),
            LocalBranchCount = _repo.Branches.Count(b => !b.IsRemote),
            RemoteBranchCount = _repo.Branches.Count(b => b.IsRemote),
            TagCount = _repo.Tags.Count(),
            StashCount = _repo.Stashes.Count(),
            IsDirty = status.IsDirty,
            ModifiedFiles = status.Modified.Count(),
            StagedFiles = status.Staged.Count(),
            UntrackedFiles = status.Untracked.Count()
        };
    }

    private static CommitInfo ConvertCommit(Commit c) => new()
    {
        Sha = c.Sha,
        Message = c.Message,
        AuthorName = c.Author.Name,
        AuthorEmail = c.Author.Email,
        AuthorDate = c.Author.When.DateTime,
        CommitterName = c.Committer.Name,
        CommitterEmail = c.Committer.Email,
        CommitDate = c.Committer.When.DateTime,
        ParentShas = c.Parents.Select(p => p.Sha).ToList()
    };

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _repo?.Dispose();
        _lock.Dispose();
        _disposed = true;
    }
}
