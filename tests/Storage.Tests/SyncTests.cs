using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Sync;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers directory comparison and update, mirror, and two-way sync.</summary>
public sealed class SyncTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Compare_classifies_every_path()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "same.txt", "x", Old);
        await Write(b, "same.txt", "x", Old);
        await Write(a, "bigger.txt", "long content", Old);
        await Write(b, "bigger.txt", "short", Old);
        await Write(a, "newer.txt", "abc", New);
        await Write(b, "newer.txt", "abc", Old);
        await Write(a, "only-a/file.txt", "a", Old);
        await Write(b, "only-b.txt", "b", Old);

        var diff = (await a.CompareAsync("", b, "")).Value!;

        StorageDiffEntry Entry(string path) => diff.Entries.Single(entry => entry.RelativePath == path);
        Assert.Equal(StorageDiffKind.Same, Entry("same.txt").Kind);
        Assert.Equal(StorageDiffReason.Size, Entry("bigger.txt").Reasons);
        Assert.Equal(StorageDiffReason.SourceNewer, Entry("newer.txt").Reasons);
        Assert.Equal(StorageDiffKind.OnlyInSource, Entry("only-a").Kind);
        Assert.True(Entry("only-a").IsDirectory);
        Assert.Equal(StorageDiffKind.OnlyInSource, Entry("only-a/file.txt").Kind);
        Assert.Equal(StorageDiffKind.OnlyInDestination, Entry("only-b.txt").Kind);
        Assert.False(diff.Identical);
    }

    [Fact]
    public async Task Update_copies_new_and_changed_files_keeps_extra_ones_and_is_idempotent()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "docs/new.txt", "new", Old);
        await Write(a, "changed.txt", "version 2", New);
        await Write(b, "changed.txt", "version 1", Old);
        await Write(b, "extra.txt", "keep me", Old);

        var first = (await a.SyncAsync("", b, "")).Value!;
        var second = (await a.SyncAsync("", b, "")).Value!;

        Assert.Equal(2, first.Copied);
        Assert.Empty(first.Failed);
        Assert.Equal("version 2", await Read(b, "changed.txt"));
        Assert.Equal("new", await Read(b, "docs/new.txt"));
        Assert.Equal("keep me", await Read(b, "extra.txt"));
        Assert.Equal(New, (await b.GetInfoAsync("changed.txt")).Value!.LastModified);
        Assert.Equal(0, second.Copied);
        Assert.Empty(second.Actions);
    }

    [Fact]
    public async Task Update_never_replaces_a_newer_destination_of_the_same_size()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "f.txt", "aaa", Old);
        await Write(b, "f.txt", "bbb", New);

        var report = (await a.SyncAsync("", b, "")).Value!;

        Assert.Equal(0, report.Copied);
        Assert.Equal("bbb", await Read(b, "f.txt"));
    }

    [Fact]
    public async Task Mirror_with_deletes_makes_the_destination_match()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "keep.txt", "k", Old);
        await Write(b, "stale.txt", "s", Old);
        await Write(b, "stale-dir/deep/x.txt", "x", Old);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true })).Value!;

        // Each file is checked and deleted on its own, then the emptied folders (deepest first).
        Assert.Equal(4, report.Deleted);
        Assert.Equal(["stale-dir/deep/x.txt", "stale-dir/deep", "stale-dir", "stale.txt"],
            report.Results.Where(result => result.Action.Kind == StorageSyncActionKind.DeleteFromDestination).Select(result => result.Action.RelativePath));
        Assert.True((await a.CompareAsync("", b, "")).Value!.Identical);
    }

    [Fact]
    public async Task Two_way_sync_with_newer_wins_copies_newer_files_in_both_directions()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "a-only.txt", "from a", Old);
        await Write(b, "b-only.txt", "from b", Old);
        await Write(a, "shared.txt", "old", Old);
        await Write(b, "shared.txt", "newest", New);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, ConflictPolicy = StorageSyncConflictPolicy.NewerWins })).Value!;

        Assert.Equal(3, report.Copied);
        Assert.Equal("from b", await Read(a, "b-only.txt"));
        Assert.Equal("from a", await Read(b, "a-only.txt"));
        Assert.Equal("newest", await Read(a, "shared.txt"));
    }

    [Fact]
    public async Task Dry_run_plans_without_changing_anything()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "f.txt", "x", Old);
        await Write(b, "gone.txt", "y", Old);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true, DryRun = true })).Value!;

        Assert.True(report.DryRun);
        Assert.Contains(report.Actions, action => action.Kind == StorageSyncActionKind.CopyToDestination && action.RelativePath == "f.txt");
        Assert.Contains(report.Actions, action => action.Kind == StorageSyncActionKind.DeleteFromDestination && action.RelativePath == "gone.txt");
        Assert.False((await b.ExistsAsync("f.txt")).Value);
        Assert.True((await b.ExistsAsync("gone.txt")).Value);
    }

    [Fact]
    public async Task Checksum_comparison_treats_identical_content_with_different_times_as_same()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "f.txt", "identical", New);
        await Write(b, "f.txt", "identical", Old);

        var diff = (await a.CompareAsync("", b, "", new StorageCompareOptions { CompareBy = StorageCompareBy.Size | StorageCompareBy.Time | StorageCompareBy.Checksum })).Value!;

        Assert.Equal(StorageDiffKind.Same, Assert.Single(diff.Entries).Kind);
    }

    [Fact]
    public async Task Missing_destination_compares_as_empty_and_is_created()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "sub/f.txt", "x", Old);

        var report = (await a.SyncAsync("sub", b, "brand/new")).Value!;

        Assert.Equal(1, report.Copied);
        Assert.Equal("x", await Read(b, "brand/new/f.txt"));
    }

    private static (LocalStorageBackend A, LocalStorageBackend B) Pair(TestDirectory directory) =>
        (new("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") }),
         new("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") }));

    private static async Task Write(LocalStorageBackend storage, string path, string content, DateTimeOffset modified)
    {
        await storage.UploadBytesAsync(path, Encoding.UTF8.GetBytes(content));
        await storage.SetTimestampsAsync(path, modified);
    }

    private static async Task<string> Read(LocalStorageBackend storage, string path) =>
        Encoding.UTF8.GetString((await storage.DownloadBytesAsync(path)).Value!);
}

public sealed class WatchTests
{
    [Fact]
    public async Task Native_local_watching_reports_creates_and_renames()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var seen = new List<StorageChange>();
        var watching = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in storage.WatchAsync("", cancellationToken: stop.Token))
                {
                    lock (seen) seen.Add(change);
                    if (change.Kind == StorageChangeKind.Renamed) break;
                }
            }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(300);

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "a.txt"), "x");
        File.Move(Path.Combine(directory.Path, "a.txt"), Path.Combine(directory.Path, "b.txt"));
        await watching.WaitAsync(TimeSpan.FromSeconds(15));

        lock (seen)
        {
            Assert.Contains(seen, change => change.Kind == StorageChangeKind.Created && change.Path == "a.txt");
            var renamed = Assert.Single(seen, change => change.Kind == StorageChangeKind.Renamed);
            Assert.Equal("b.txt", renamed.Path);
            Assert.Equal("a.txt", renamed.OldPath);
        }
    }

    [Fact]
    public async Task Polling_detects_creates_changes_and_deletes()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        await storage.UploadBytesAsync("existing.txt", [1]);
        await storage.UploadBytesAsync("doomed.txt", [1]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var seen = new List<StorageChange>();
        var options = new StorageWatchOptions { ForcePolling = true, PollInterval = TimeSpan.FromMilliseconds(100) };
        var watching = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in storage.WatchAsync("", options, stop.Token))
                {
                    lock (seen)
                    {
                        seen.Add(change);
                        if (AllSeen(seen)) break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(300);

        await storage.UploadBytesAsync("new.txt", [1]);
        await storage.UploadBytesAsync("existing.txt", [1, 2, 3], new StorageUploadOptions { Overwrite = true });
        await storage.DeleteAsync("doomed.txt");
        await watching.WaitAsync(TimeSpan.FromSeconds(15));

        lock (seen)
            Assert.True(AllSeen(seen), string.Join(", ", seen.Select(change => $"{change.Kind} {change.Path}")));
    }

    /// <summary>
    /// An overwrite shows up as a change, or, when a poll lands mid-replace (Windows replaces are not
    /// atomic for directory listings), as a delete followed by a create.
    /// </summary>
    private static bool AllSeen(List<StorageChange> seen) =>
        seen.Any(change => change is { Kind: StorageChangeKind.Created, Path: "new.txt" }) &&
        seen.Any(change => change is { Kind: StorageChangeKind.Deleted, Path: "doomed.txt" }) &&
        (seen.Any(change => change is { Kind: StorageChangeKind.Changed, Path: "existing.txt" }) ||
         seen.Any(change => change is { Kind: StorageChangeKind.Created, Path: "existing.txt" }));
}
