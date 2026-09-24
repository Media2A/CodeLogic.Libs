using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Sync;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Sync defects found in review: baselines, case spelling, directories, stopping, and conflict names.</summary>
public sealed class ReviewSyncTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (LocalStorageBackend A, LocalStorageBackend B) Pair(TestDirectory directory) =>
        (new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") }),
         new LocalStorageBackend("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") }));

    private static async Task Write(IStorageService storage, string path, string content, DateTimeOffset modified)
    {
        Assert.True((await storage.UploadBytesAsync(path, Encoding.UTF8.GetBytes(content))).IsSuccess);
        Assert.True((await storage.SetTimestampsAsync(path, modified)).IsSuccess);
    }

    private static async Task<string> Read(IStorageService storage, string path) =>
        Encoding.UTF8.GetString((await storage.DownloadBytesAsync(path)).Value!);

    private static StorageSyncOptions TwoWay(InMemoryStorageSyncStateStore store) =>
        new() { Direction = StorageSyncDirection.TwoWay, StateStore = store, SyncId = "s" };

    [Fact]
    public async Task An_edit_made_after_planning_is_still_carried_over_by_the_next_run()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "f.txt", "one", Old);
        Assert.Equal(1, (await a.SyncAsync("", b, "", TwoWay(store))).Value!.Copied);

        await Write(a, "g.txt", "other", Old);
        var plan = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;
        // f.txt is edited after the plan was made; the run has no step for it.
        await Write(a, "f.txt", "two!!", New);
        var applied = (await a.ApplySyncAsync("", b, "", plan, plan.Digest, TwoWay(store))).Value!;
        Assert.True(applied.BaselineSaved);

        var next = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;

        Assert.Contains(next.Results, result => result.Action.RelativePath == "f.txt" && result.Action.Kind == StorageSyncActionKind.CopyToDestination && result.Outcome == StorageSyncActionOutcome.Applied);
        Assert.Equal("two!!", await Read(b, "f.txt"));
    }

    [Fact]
    public async Task Unchanged_sides_that_differ_in_content_are_a_conflict_not_in_sync()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "f.txt", "same", Old);
        await Write(b, "f.txt", "same", Old);
        Assert.True((await a.SyncAsync("", b, "", TwoWay(store))).IsSuccess);
        // A baseline that claims both sides are at versions whose content differs cannot be trusted.
        var saved = (await store.LoadAsync("s", default))!;
        var skewed = saved.Entries.ToDictionary(pair => pair.Key, pair => pair.Value);
        await Write(b, "f.txt", "diff!", Old);
        skewed["f.txt"] = skewed["f.txt"] with { Destination = StorageSyncIdentity.Of((await b.GetInfoAsync("f.txt")).Value!) };
        Assert.True(await store.SaveAsync("s", new StorageSyncBaseline(saved.Generation + 1, skewed), saved.Generation, default));

        var plan = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;

        Assert.Equal(StorageSyncConflictKind.BothModified, Assert.Single(plan.Conflicts).Conflict);
    }

    [Fact]
    public async Task A_name_spelled_differently_by_case_is_updated_and_deleted_under_its_own_spelling()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "Readme.TXT", "version two", New);
        await Write(b, "readme.txt", "v1", Old);
        await Write(a, "anchor.txt", "keeps neither side empty", Old);
        await Write(b, "anchor.txt", "keeps neither side empty", Old);
        var options = TwoWay(store) with { ConflictPolicy = StorageSyncConflictPolicy.NewerWins, Compare = new StorageCompareOptions { CaseInsensitive = true } };

        var first = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Empty(first.Stale);
        Assert.Empty(first.Failed);
        Assert.Equal("version two", await Read(b, "readme.txt"));
        Assert.Contains("readme.txt", (await b.ListAsync("")).Value!.Items.Select(item => item.Name));
        Assert.Contains("Readme.TXT", (await store.LoadAsync("s", default))!.Entries.Keys);

        // A later deletion at the source reaches the destination's spelling instead of resurrecting the file.
        Assert.True((await a.DeleteAsync("Readme.TXT")).IsSuccess);
        var second = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Equal(1, second.Deleted);
        Assert.False((await b.ExistsAsync("readme.txt")).Value);
        Assert.False((await a.ExistsAsync("Readme.TXT")).Value);
    }

    [Fact]
    public async Task Two_way_sync_creates_and_deletes_directories()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        Assert.True((await a.CreateDirectoryAsync("empty")).IsSuccess);
        Assert.True((await b.CreateDirectoryAsync("from-b/inner")).IsSuccess);
        await Write(a, "anchor.txt", "keeps neither side empty", Old);
        await Write(b, "anchor.txt", "keeps neither side empty", Old);

        var first = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;
        Assert.Empty(first.Failed);
        Assert.True((await b.ExistsAsync("empty")).Value);
        Assert.True((await a.ExistsAsync("from-b/inner")).Value);

        // The destination removes one tree; the source adds a file inside another the destination removed.
        Assert.True((await b.DeleteAsync("from-b", new StorageDeleteOptions { Recursive = true })).IsSuccess);
        Assert.True((await b.DeleteAsync("empty")).IsSuccess);
        await Write(a, "empty/new.txt", "kept", Old);
        var second = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;

        Assert.Empty(second.Failed);
        Assert.False((await a.ExistsAsync("from-b")).Value);
        Assert.Equal("kept", await Read(b, "empty/new.txt"));
        Assert.True((await a.ExistsAsync("empty/new.txt")).Value);
    }

    [Fact]
    public async Task A_path_that_is_a_file_on_one_side_and_a_directory_on_the_other_leaves_everything_below_it_alone()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "x/child.txt", "c", Old);
        await Write(a, "other.txt", "o", Old);
        await Write(b, "x", "a file", Old);
        await Write(b, "gone.txt", "g", Old);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true })).Value!;

        Assert.Empty(report.Failed);
        Assert.DoesNotContain(report.Actions, action => action.RelativePath.StartsWith("x", StringComparison.Ordinal));
        Assert.Contains(report.Plan.Warnings, warning => warning.Contains("'x'", StringComparison.Ordinal));
        Assert.Equal(1, report.Deleted);
        Assert.Equal("o", await Read(b, "other.txt"));
    }

    [Fact]
    public async Task Stopping_at_the_first_error_returns_the_report()
    {
        using var directory = new TestDirectory();
        var (a, local) = Pair(directory);
        for (var i = 0; i < 4; i++)
        {
            await Write(a, $"transient/t{i}.txt", "t", Old);
            await Write(a, $"permanent/p{i}.txt", "p", Old);
        }
        var b = new HookedBackend(local, (_, _) => Task.CompletedTask, path =>
            path.StartsWith("transient/", StringComparison.Ordinal) ? Result<StorageItem>.Failure(StorageErrors.Unavailable("try later"))
            : path.StartsWith("permanent/", StringComparison.Ordinal) ? Result<StorageItem>.Failure(StorageErrors.PermissionDenied("no"))
            : (Result<StorageItem>?)null);

        var report = await a.SyncAsync("", b, "", new StorageSyncOptions { ContinueOnError = false, ItemRetries = 5, MaxConcurrency = 4 });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.NotEmpty(report.Value!.Failed);
        Assert.False(report.Value.Cancelled);
        Assert.Contains(report.Value.Results, result => result.Outcome == StorageSyncActionOutcome.NotRun);
    }

    [Fact]
    public async Task A_kept_conflict_copy_takes_a_numbered_name_when_the_first_is_taken()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "f.txt", "ours", New);
        await Write(b, "f.txt", "theirs", Old);
        var theirs = StorageSyncIdentity.Of((await b.GetInfoAsync("f.txt")).Value!)!;
        // An old conflict copy already has the name this run would pick.
        await Write(b, StorageSync.ConflictPath("f.txt", theirs), "older conflict copy", Old);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, ConflictPolicy = StorageSyncConflictPolicy.KeepBoth })).Value!;

        Assert.Empty(report.Failed);
        Assert.Empty(report.Stale);
        var numbered = StorageSync.ConflictPath("f.txt", theirs, 2);
        Assert.Equal("theirs", await Read(b, numbered));
        Assert.Equal("theirs", await Read(a, numbered));
        Assert.Equal("ours", await Read(b, "f.txt"));
        Assert.Equal("older conflict copy", await Read(b, StorageSync.ConflictPath("f.txt", theirs)));
    }

    [Fact]
    public async Task Update_never_replaces_a_newer_destination_even_when_the_size_differs()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "f.txt", "old but longer", Old);
        await Write(b, "f.txt", "newer", New);

        var report = (await a.SyncAsync("", b, "")).Value!;

        Assert.Equal(0, report.Copied);
        Assert.Equal("newer", await Read(b, "f.txt"));
        Assert.Contains(report.Plan.Warnings, warning => warning.Contains("f.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mirror_keeps_a_folder_that_gained_a_file_after_the_plan()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "keep.txt", "k", Old);
        await Write(b, "extra/old.txt", "o", Old);
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true };
        var plan = (await a.PlanSyncAsync("", b, "", options)).Value!;
        await Write(b, "extra/arrived-later.txt", "new", Old);

        var report = (await a.ApplySyncAsync("", b, "", plan, plan.Digest, options)).Value!;

        Assert.False((await b.ExistsAsync("extra/old.txt")).Value);
        Assert.Equal("new", await Read(b, "extra/arrived-later.txt"));
        Assert.Equal(StorageSyncActionOutcome.Stale, report.Results.Single(result => result.Action.RelativePath == "extra").Outcome);
    }

    [Fact]
    public async Task Sync_refuses_link_recreation_and_a_plan_for_another_sync_id()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "f.txt", "x", Old);

        var recreate = await a.PlanSyncAsync("", b, "", new StorageSyncOptions { Compare = new StorageCompareOptions { LinkHandling = StorageLinkHandling.Recreate } });
        Assert.Equal(StorageErrors.InvalidContentCode, recreate.Error?.Code);

        var plan = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;
        var other = await a.ApplySyncAsync("", b, "", plan, plan.Digest, TwoWay(store) with { SyncId = "other" });
        Assert.Equal(StorageErrors.InvalidContentCode, other.Error?.Code);
    }
}
