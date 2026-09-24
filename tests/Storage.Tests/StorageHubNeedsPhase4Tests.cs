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

/// <summary>Three-way sync, plans and approval, deletion safety, filters, and robust runs.</summary>
public sealed class ThreeWaySyncTests
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (LocalStorageBackend A, LocalStorageBackend B) Pair(TestDirectory directory) =>
        (new("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") }),
         new("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") }));

    private static async Task Write(LocalStorageBackend storage, string path, string content, DateTimeOffset modified)
    {
        await storage.UploadBytesAsync(path, Encoding.UTF8.GetBytes(content));
        await storage.SetTimestampsAsync(path, modified);
    }

    private static async Task<string?> Read(IStorageService storage, string path)
    {
        var bytes = await storage.DownloadBytesAsync(path);
        return bytes.IsSuccess ? Encoding.UTF8.GetString(bytes.Value!) : null;
    }

    private static StorageSyncOptions TwoWay(IStorageSyncStateStore store, StorageSyncConflictPolicy policy = StorageSyncConflictPolicy.Block) =>
        new() { Direction = StorageSyncDirection.TwoWay, StateStore = store, SyncId = "pair", ConflictPolicy = policy };

    [Fact]
    public async Task A_deletion_after_a_two_way_sync_is_carried_over_not_undone()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "gone.txt", "x", T0);
        await Write(a, "stays.txt", "y", T0);
        Assert.Equal(2, (await a.SyncAsync("", b, "", TwoWay(store))).Value!.Copied);

        await b.DeleteAsync("gone.txt");
        var second = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;

        Assert.Equal(1, second.Deleted);
        Assert.False((await a.ExistsAsync("gone.txt")).Value);
        Assert.True((await a.ExistsAsync("stays.txt")).Value);
        Assert.Empty((await a.SyncAsync("", b, "", TwoWay(store))).Value!.Actions);
    }

    [Fact]
    public async Task Edits_on_both_sides_block_by_default()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "doc.txt", "base", T0);
        await a.SyncAsync("", b, "", TwoWay(store));
        await Write(a, "doc.txt", "edit from a", T0.AddHours(1));
        await Write(b, "doc.txt", "edit from b, longer", T0.AddHours(2));

        var plan = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;
        var applied = await a.ApplySyncAsync("", b, "", plan, plan.Digest, TwoWay(store));

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(StorageSyncConflictKind.BothModified, conflict.Conflict);
        Assert.False(plan.IsApprovable);
        Assert.Equal(StorageErrors.ConflictCode, applied.Error!.Code);
        Assert.Equal("edit from a", await Read(a, "doc.txt"));
        Assert.Equal("edit from b, longer", await Read(b, "doc.txt"));
    }

    [Fact]
    public async Task Keep_both_saves_the_other_version_under_a_conflict_name_on_both_sides()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "doc.txt", "base", T0);
        await a.SyncAsync("", b, "", TwoWay(store));
        await Write(a, "doc.txt", "edit from a", T0.AddHours(1));
        await Write(b, "doc.txt", "edit from b", T0.AddHours(2));

        var report = (await a.SyncAsync("", b, "", TwoWay(store, StorageSyncConflictPolicy.KeepBoth))).Value!;

        Assert.Empty(report.Failed);
        var copy = report.Actions.Single(action => action.Kind == StorageSyncActionKind.RenameAtDestination).TargetPath!;
        Assert.StartsWith("doc (conflict ", copy);
        Assert.Equal("edit from a", await Read(a, "doc.txt"));
        Assert.Equal("edit from a", await Read(b, "doc.txt"));
        Assert.Equal("edit from b", await Read(a, copy));
        Assert.Equal("edit from b", await Read(b, copy));
        Assert.Empty((await a.SyncAsync("", b, "", TwoWay(store, StorageSyncConflictPolicy.KeepBoth))).Value!.Actions);
    }

    [Fact]
    public async Task Newer_wins_only_when_asked_and_a_modify_beats_a_delete()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "doc.txt", "base", T0);
        await Write(a, "other.txt", "base", T0);
        await a.SyncAsync("", b, "", TwoWay(store));
        await Write(a, "doc.txt", "older edit", T0.AddHours(1));
        await Write(b, "doc.txt", "newer edit", T0.AddHours(2));
        await a.DeleteAsync("other.txt");
        await Write(b, "other.txt", "kept edit", T0.AddHours(3));

        var report = (await a.SyncAsync("", b, "", TwoWay(store, StorageSyncConflictPolicy.NewerWins))).Value!;

        Assert.Empty(report.Failed);
        Assert.Equal("newer edit", await Read(a, "doc.txt"));
        Assert.Equal("kept edit", await Read(a, "other.txt"));
    }

    [Fact]
    public async Task An_emptied_source_does_not_wipe_the_mirror()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        for (var i = 0; i < 5; i++) await Write(b, $"f{i}.txt", "x", T0);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true })).Value!;

        Assert.Equal(0, report.Deleted);
        Assert.Equal(5, report.Withheld.Count);
        Assert.Contains(report.Plan.Warnings, warning => warning.Contains("unexpectedly empty"));
        Assert.Equal(5, (await b.ListAsync("")).Value!.Items.Count);
    }

    [Fact]
    public async Task Deletion_limits_withhold_every_deletion()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "keep.txt", "k", T0);
        await Write(b, "keep.txt", "k", T0);
        for (var i = 0; i < 4; i++) await Write(b, $"extra{i}.txt", "x", T0);
        var mirror = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true };

        var byCount = (await a.SyncAsync("", b, "", mirror with { MaxDeletes = 3 })).Value!;
        var byShare = (await a.SyncAsync("", b, "", mirror with { MaxDeletePercent = 50 })).Value!;
        var allowed = (await a.SyncAsync("", b, "", mirror with { MaxDeletes = 4 })).Value!;

        Assert.Equal((0, 4), (byCount.Deleted, byCount.Withheld.Count));
        Assert.Equal((0, 4), (byShare.Deleted, byShare.Withheld.Count));
        Assert.Equal(4, allowed.Deleted);
    }

    [Fact]
    public async Task Same_size_different_content_is_found_by_checksum()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "f.txt", "aaaa", T0);
        await Write(b, "f.txt", "bbbb", T0.AddSeconds(1));

        var bySizeAndTime = (await a.CompareAsync("", b, "")).Value!;
        var byChecksum = (await a.CompareAsync("", b, "", new StorageCompareOptions { CompareBy = StorageCompareBy.Size | StorageCompareBy.Time | StorageCompareBy.Checksum })).Value!;
        var outOfBudget = (await a.CompareAsync("", b, "", new StorageCompareOptions { CompareBy = StorageCompareBy.Checksum, MaxHashedFiles = 0 })).Value!;

        Assert.Equal(StorageDiffKind.Same, Assert.Single(bySizeAndTime.Entries).Kind);
        Assert.True(Assert.Single(byChecksum.Entries).Reasons.HasFlag(StorageDiffReason.Checksum));
        Assert.True(Assert.Single(outOfBudget.Entries).Reasons.HasFlag(StorageDiffReason.Undecidable));
    }

    [Fact]
    public async Task A_plan_is_applied_only_as_approved_and_only_while_its_items_are_unchanged()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "one.txt", "1", T0);
        await Write(a, "two.txt", "2", T0);
        var plan = (await a.PlanSyncAsync("", b, "")).Value!;
        Assert.Equal(plan.Digest, StorageSyncPlan.FromJson(plan.ToJson()).Digest);

        var tampered = plan with { Actions = [.. plan.Actions.Take(1)] };
        Assert.Equal(StorageErrors.ConflictCode, (await a.ApplySyncAsync("", b, "", tampered, plan.Digest)).Error!.Code);
        Assert.Equal(StorageErrors.ConflictCode, (await a.ApplySyncAsync("", b, "", plan, new string('0', 64))).Error!.Code);

        await Write(a, "two.txt", "changed after planning", T0.AddHours(1));
        var report = (await a.ApplySyncAsync("", b, "", plan, plan.Digest)).Value!;

        Assert.Equal(1, report.Copied);
        Assert.Equal("two.txt", Assert.Single(report.Stale).Action.RelativePath);
        Assert.False((await b.ExistsAsync("two.txt")).Value);
    }

    [Fact]
    public async Task A_plan_made_before_another_run_saved_its_baseline_is_refused()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        var store = new InMemoryStorageSyncStateStore();
        await Write(a, "f.txt", "x", T0);
        var stale = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;
        await a.SyncAsync("", b, "", TwoWay(store));

        var applied = await a.ApplySyncAsync("", b, "", stale, stale.Digest, TwoWay(store));

        Assert.Equal(StorageErrors.ConflictCode, applied.Error!.Code);
    }

    [Fact]
    public async Task Filters_leave_excluded_items_alone_even_inside_deleted_folders()
    {
        using var directory = new TestDirectory();
        var (a, b) = Pair(directory);
        await Write(a, "keep.txt", "k", T0);
        await Write(a, "logs/app.log", "noise", T0);
        await Write(b, "keep.txt", "k", T0);
        await Write(b, "old/data.txt", "d", T0);
        await Write(b, "old/cache.tmp", "c", T0);
        var options = new StorageSyncOptions
        {
            Direction = StorageSyncDirection.Mirror,
            DeleteExtraneous = true,
            Compare = new StorageCompareOptions { Exclude = ["logs/**", "**/*.tmp"] }
        };

        var report = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Empty(report.Failed);
        Assert.False((await b.ExistsAsync("logs/app.log")).Value);
        Assert.False((await b.ExistsAsync("old/data.txt")).Value);
        Assert.True((await b.ExistsAsync("old/cache.tmp")).Value);
    }

    [Fact]
    public async Task Names_that_differ_only_by_case_are_refused_for_a_case_insensitive_side()
    {
        using var directory = new TestDirectory();
        var a = new CL.Storage.Providers.Local.LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") });
        var b = new CL.Storage.Providers.Local.LocalStorageBackend("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") });
        var names = new FakeStorageBackend(
            "cased",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.Directory })),
            list: (_, _, _) => Task.FromResult(Result<StoragePage>.Success(new StoragePage(
            [
                new StorageItem { Path = "Readme.md", Name = "Readme.md", ItemType = StorageItemType.File, Size = 1 },
                new StorageItem { Path = "README.md", Name = "README.md", ItemType = StorageItemType.File, Size = 2 }
            ], null))));

        var plan = await names.PlanSyncAsync("", b, "", new StorageSyncOptions { Compare = new StorageCompareOptions { CaseInsensitive = true } });

        Assert.Equal(StorageErrors.ConflictCode, plan.Error!.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(plan.Error, "caseCollision", out _));
        _ = a;
    }

    [Fact]
    public async Task A_stopping_run_keeps_the_report_and_continue_on_error_can_be_turned_off()
    {
        using var directory = new TestDirectory();
        var source = new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") });
        for (var i = 0; i < 6; i++) await Write(source, $"f{i}.txt", "x", T0);
        var uploads = 0;
        var failing = new FakeStorageBackend(
            "b",
            getInfo: (path, _) => Task.FromResult(path.Length == 0
                ? Result<StorageItem>.Success(new StorageItem { Path = "", Name = "", ItemType = StorageItemType.Directory })
                : Result<StorageItem>.Failure(StorageErrors.NotFound("missing"))),
            uploadStream: (_, _, _, _) =>
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.PermissionDenied("read-only")));
            });

        var report = (await source.SyncAsync("", failing, "", new StorageSyncOptions { ContinueOnError = false, MaxConcurrency = 1 })).Value!;

        Assert.Single(report.Failed);
        Assert.Equal(5, report.Results.Count(result => result.Outcome == StorageSyncActionOutcome.NotRun));
        Assert.Equal(1, uploads);
    }

    [Fact]
    public async Task Transient_failures_are_retried_per_item()
    {
        using var directory = new TestDirectory();
        var source = new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") });
        await Write(source, "f.txt", "x", T0);
        var target = new LocalStorageBackend("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") });
        var attempts = 0;
        var flaky = new FakeStorageBackend(
            "b",
            getInfo: (path, token) => target.GetInfoAsync(path, token),
            exists: (path, token) => target.ExistsAsync(path, token),
            createDirectory: (path, token) => target.CreateDirectoryAsync(path, token),
            uploadStream: (path, stream, options, token) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromResult(Result<StorageItem>.Failure(StorageErrors.ConnectionLost("dropped")))
                : target.UploadAsync(path, stream, options, token),
            move: (from, to, token) => target.MoveAsync(from, to, new StorageTransferOptions(), token),
            delete: (path, token) => target.DeleteAsync(path, new StorageDeleteOptions { IgnoreMissing = true }, token));

        var report = (await source.SyncAsync("", flaky, "", new StorageSyncOptions())).Value!;

        var result = Assert.Single(report.Results);
        Assert.Equal((StorageSyncActionOutcome.Applied, 2), (result.Outcome, result.Attempts));
    }
}
