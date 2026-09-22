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

        Assert.Equal(2, report.Deleted); // the directory once, not each file in it
        Assert.True((await a.CompareAsync("", b, "")).Value!.Identical);
    }

    [Fact]
    public async Task Two_way_sync_copies_newer_files_in_both_directions()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "a-only.txt", "from a", Old);
        await Write(b, "b-only.txt", "from b", Old);
        await Write(a, "shared.txt", "old", Old);
        await Write(b, "shared.txt", "newest", New);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay })).Value!;

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
