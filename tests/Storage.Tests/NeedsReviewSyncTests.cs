using System.Security.Cryptography;
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

/// <summary>Sync findings from the round-3 review (needs-review.md, sections A, B, and E).</summary>
public sealed class NeedsReviewSyncTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StorageSyncOptions TwoWay(IStorageSyncStateStore store) =>
        new() { Direction = StorageSyncDirection.TwoWay, StateStore = store, SyncId = "s" };

    private static StorageSyncOptions CaseInsensitive(StorageSyncOptions options) =>
        options with { Compare = options.Compare with { CaseInsensitive = true } };

    private static async Task<StorageSyncBaselineEntry?> Entry(InMemoryStorageSyncStateStore store, string path) =>
        (await store.LoadAsync("s", default))?.Entries.GetValueOrDefault(path);

    private static StorageSyncActionResult Step(StorageSyncReport report, string path, StorageSyncActionKind? kind = null) =>
        report.Results.Single(result => result.Action.RelativePath == path && (kind is null || result.Action.Kind == kind));

    // ------------------------------------------------------------ A19: folders spelled differently

    [Fact] // needs-review A19
    public async Task A_new_file_under_a_folder_spelled_differently_lands_in_the_destinations_folder()
    {
        var local = new MemoryStorage("local", caseInsensitive: true);
        var remote = new MemoryStorage("remote");
        local.Put("Docs/a.txt", "same", Old);
        remote.Put("docs/a.txt", "same", Old);
        var store = new InMemoryStorageSyncStateStore();
        var options = CaseInsensitive(TwoWay(store)) with { Compare = new StorageCompareOptions { CaseInsensitive = true, CompareBy = StorageCompareBy.Size } };

        Assert.True((await local.SyncAsync("", remote, "", options)).IsSuccess);
        local.Put("Docs/new.txt", "new", Old);
        var second = await local.SyncAsync("", remote, "", options);

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Empty(second.Value!.Failed);
        Assert.Equal(["docs", "docs/a.txt", "docs/new.txt"], remote.Paths());
        Assert.True((await local.CompareAsync("", remote, "", options.Compare)).IsSuccess);
    }

    [Fact] // needs-review A19
    public async Task A_two_way_copy_into_a_case_sensitive_source_uses_the_sources_folder()
    {
        var source = new MemoryStorage("source");
        var destination = new MemoryStorage("destination", caseInsensitive: true);
        source.Put("Docs/a.txt", "a", Old);
        destination.Put("docs/b.txt", "b", Old);

        var report = await source.SyncAsync("", destination, "", new StorageSyncOptions
        {
            Direction = StorageSyncDirection.TwoWay,
            Compare = new StorageCompareOptions { CaseInsensitive = true }
        });

        Assert.True(report.IsSuccess, report.Error?.Message);
        Assert.Empty(report.Value!.Failed);
        Assert.Equal(["Docs", "Docs/a.txt", "Docs/b.txt"], source.Paths());
        Assert.Equal(["docs", "docs/a.txt", "docs/b.txt"], destination.Paths());
    }

    [Fact] // needs-review A19
    public async Task A_kept_conflict_copy_goes_into_each_sides_own_folder()
    {
        var source = new MemoryStorage("source", caseInsensitive: true);
        var destination = new MemoryStorage("destination");
        source.Put("Docs/f.txt", "ours!", New);
        destination.Put("docs/f.txt", "theirs", Old);

        var report = (await source.SyncAsync("", destination, "", new StorageSyncOptions
        {
            Direction = StorageSyncDirection.TwoWay,
            ConflictPolicy = StorageSyncConflictPolicy.KeepBoth,
            Compare = new StorageCompareOptions { CaseInsensitive = true }
        })).Value!;

        Assert.Empty(report.Failed);
        Assert.Empty(report.Stale);
        Assert.All(destination.Paths(), path => Assert.StartsWith("docs", path, StringComparison.Ordinal));
        Assert.Equal("ours!", destination.Text("docs/f.txt"));
        var copy = Assert.Single(destination.Paths(), path => path.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("theirs", destination.Text(copy));
        Assert.Equal("theirs", source.Text("Docs/" + copy["docs/".Length..]));
    }

    [Fact] // needs-review A19
    public async Task Entries_use_the_sources_folder_spelling_and_a_folder_comes_right_before_its_contents()
    {
        var source = new MemoryStorage("source", caseInsensitive: true);
        var destination = new MemoryStorage("destination");
        source.Put("Docs/a.txt", "a", Old);
        source.Put("a/c.txt", "c", Old);
        source.Put("a-b.txt", "b", Old);
        destination.Put("docs/x.txt", "x", Old);

        var diff = (await source.CompareAsync("", destination, "", new StorageCompareOptions { CaseInsensitive = true })).Value!;

        var onlyThere = diff.Entries.Single(entry => entry.Kind == StorageDiffKind.OnlyInDestination);
        Assert.Equal("Docs/x.txt", onlyThere.RelativePath);
        Assert.Equal("docs/x.txt", onlyThere.DestinationRelativePath);
        Assert.Equal("docs/a.txt", diff.Entries.Single(entry => entry.RelativePath == "Docs/a.txt").DestinationRelativePath);
        Assert.Equal(["Docs", "Docs/a.txt", "Docs/x.txt", "a", "a/c.txt", "a-b.txt"], diff.Entries.Select(entry => entry.RelativePath));
    }

    [Fact] // needs-review E: the case-spelling test, on a destination that really keeps case on every platform
    public async Task A_name_spelled_differently_by_case_is_updated_and_deleted_under_its_own_spelling_on_a_case_sensitive_destination()
    {
        var a = new MemoryStorage("a", caseInsensitive: true);
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("Readme.TXT", "version two", New);
        b.Put("readme.txt", "v1", Old);
        a.Put("anchor.txt", "keeps neither side empty", Old);
        b.Put("anchor.txt", "keeps neither side empty", Old);
        var options = CaseInsensitive(TwoWay(store)) with { ConflictPolicy = StorageSyncConflictPolicy.NewerWins };

        var first = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Empty(first.Stale);
        Assert.Empty(first.Failed);
        Assert.Equal(["anchor.txt", "readme.txt"], b.Paths());
        Assert.Equal("version two", b.Text("readme.txt"));
        Assert.Contains("Readme.TXT", (await store.LoadAsync("s", default))!.Entries.Keys);

        await a.DeleteAsync("Readme.TXT");
        var second = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Equal(1, second.Deleted);
        Assert.Equal(["anchor.txt"], b.Paths());
        Assert.Equal(["anchor.txt"], a.Paths());
    }

    // ------------------------------------------------------------ A20: only NotFound is absent

    [Fact] // needs-review A20
    public async Task A_delete_that_could_not_read_the_file_fails_and_is_not_undone_later()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "x", Old);
        a.Put("keep.txt", "k", Old);
        var options = TwoWay(store) with { ItemRetries = 0 };
        Assert.True((await a.SyncAsync("", b, "", options)).IsSuccess);
        await a.DeleteAsync("f.txt");
        b.FailGetInfo = path => path == "f.txt" ? StorageErrors.Unavailable("busy") : null;

        var second = (await a.SyncAsync("", b, "", options)).Value!;

        Assert.Equal(StorageSyncActionOutcome.Failed, Step(second, "f.txt").Outcome);
        Assert.True(b.Has("f.txt"));
        b.FailGetInfo = null;
        await a.SyncAsync("", b, "", options);
        Assert.False(b.Has("f.txt"));
        Assert.False(a.Has("f.txt"));
    }

    [Fact] // needs-review A20
    public async Task A_transient_failure_to_read_the_source_is_retried_not_reported_stale()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "x", Old);
        var calls = 0;
        a.FailGetInfo = path => path == "f.txt" && Interlocked.Increment(ref calls) == 1 ? StorageErrors.Unavailable("busy") : null;

        var report = (await a.SyncAsync("", b, "")).Value!;

        var step = Step(report, "f.txt");
        Assert.Equal(StorageSyncActionOutcome.Applied, step.Outcome);
        Assert.Equal(2, step.Attempts);
        Assert.Equal("x", b.Text("f.txt"));
    }

    // ------------------------------------------------------------ A21: baseline keys where case is ignored

    private static async Task<(MemoryStorage A, MemoryStorage B, InMemoryStorageSyncStateStore Store, StorageSyncOptions Options)> SpelledPairAsync()
    {
        var a = new MemoryStorage("a", caseInsensitive: true);
        var b = new MemoryStorage("b");
        a.Put("Readme.TXT", "same", Old);
        b.Put("readme.txt", "same", Old);
        a.Put("anchor.txt", "k", Old);
        b.Put("anchor.txt", "k", Old);
        var store = new InMemoryStorageSyncStateStore();
        var options = CaseInsensitive(TwoWay(store));
        Assert.True((await a.SyncAsync("", b, "", options)).IsSuccess);
        return (a, b, store, options);
    }

    [Fact] // needs-review A21
    public async Task A_withheld_delete_keeps_its_baseline_entry_where_case_is_ignored()
    {
        var (a, b, store, options) = await SpelledPairAsync();
        await a.DeleteAsync("Readme.TXT");

        var withheld = (await a.SyncAsync("", b, "", options with { MaxDeletes = 0 })).Value!;

        Assert.Single(withheld.Withheld);
        Assert.Contains((await store.LoadAsync("s", default))!.Entries.Keys, key => key.Equals("readme.txt", StringComparison.OrdinalIgnoreCase));
        await a.SyncAsync("", b, "", options);
        Assert.False(b.Has("readme.txt"));
        Assert.False(a.Has("Readme.TXT"));
    }

    [Fact] // needs-review A21
    public async Task A_blocked_delete_versus_modify_conflict_stays_a_conflict_where_case_is_ignored()
    {
        var (a, b, _, options) = await SpelledPairAsync();
        await a.DeleteAsync("Readme.TXT");
        b.Put("readme.txt", "edited at the destination", New);

        var run = (await a.SyncAsync("", b, "", options with { ApplyWithConflicts = true })).Value!;
        Assert.Equal(StorageSyncConflictKind.DeleteVersusModify, Assert.Single(run.Conflicts).Conflict);

        var again = (await a.PlanSyncAsync("", b, "", options)).Value!;
        Assert.Equal(StorageSyncConflictKind.DeleteVersusModify, Assert.Single(again.Conflicts).Conflict);
        Assert.False(a.Has("Readme.TXT"));
    }

    // ------------------------------------------------------------ A22, A23, A24: what is left out stays out

    private static (LocalStorageBackend A, LocalStorageBackend B, string DirA, string DirB) LocalPair(TestDirectory directory)
    {
        var dirA = directory.CreateDirectory("a");
        var dirB = directory.CreateDirectory("b");
        return (new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = dirA }),
                new LocalStorageBackend("b", new LocalConnectionConfig { RootPath = dirB }), dirA, dirB);
    }

    [Fact] // needs-review A22 (acceptance R3-5)
    public async Task Excluding_a_folder_excludes_everything_inside_it()
    {
        using var directory = new TestDirectory();
        var (a, b, dirA, dirB) = LocalPair(directory);
        Directory.CreateDirectory(Path.Combine(dirA, "app", "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(dirA, "app", "main.js"), "main");
        File.WriteAllText(Path.Combine(dirA, "app", "node_modules", "pkg", "index.js"), "dep");
        Directory.CreateDirectory(Path.Combine(dirB, "app", "node_modules", "other"));
        File.WriteAllText(Path.Combine(dirB, "app", "node_modules", "other", "kept.js"), "theirs");
        var compare = new StorageCompareOptions { Exclude = ["**/node_modules"] };

        var update = (await a.SyncAsync("", b, "", new StorageSyncOptions { Compare = compare })).Value!;
        var mirror = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true, Compare = compare })).Value!;

        Assert.True(File.Exists(Path.Combine(dirB, "app", "main.js")));
        Assert.False(File.Exists(Path.Combine(dirB, "app", "node_modules", "pkg", "index.js")));
        Assert.True(File.Exists(Path.Combine(dirB, "app", "node_modules", "other", "kept.js")));
        Assert.Empty(update.Failed);
        Assert.Empty(mirror.Failed);
        Assert.Empty(mirror.Stale);
        Assert.DoesNotContain(mirror.Actions, action => action.RelativePath.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact] // needs-review A23 (acceptance R3-6)
    public async Task Leaving_hidden_items_out_leaves_out_the_contents_of_hidden_folders_and_keeps_folders_holding_them()
    {
        using var directory = new TestDirectory();
        var (a, b, dirA, dirB) = LocalPair(directory);
        Directory.CreateDirectory(Path.Combine(dirA, ".git"));
        File.WriteAllText(Path.Combine(dirA, ".git", "config"), "secret");
        File.WriteAllText(Path.Combine(dirA, "readme.txt"), "hi");
        Directory.CreateDirectory(Path.Combine(dirB, "old"));
        File.WriteAllText(Path.Combine(dirB, "old", ".keep"), "hidden");
        File.WriteAllText(Path.Combine(dirB, "old", "a.txt"), "extraneous");
        var compare = new StorageCompareOptions { IncludeHidden = false };

        var update = (await a.SyncAsync("", b, "", new StorageSyncOptions { Compare = compare })).Value!;
        var mirror = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true, Compare = compare })).Value!;

        Assert.False(File.Exists(Path.Combine(dirB, ".git", "config")));
        Assert.Empty(update.Failed);
        // The folder still holds a hidden file, so only the visible file goes and nothing fails or goes stale.
        Assert.Empty(mirror.Failed);
        Assert.Empty(mirror.Stale);
        Assert.False(File.Exists(Path.Combine(dirB, "old", "a.txt")));
        Assert.True(File.Exists(Path.Combine(dirB, "old", ".keep")));
    }

    [Fact] // needs-review A24
    public async Task Mirror_never_deletes_what_the_source_left_out_for_what_it_is()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("doc.txt", "d", Old);
        b.Put("doc.txt", "d", Old);
        // A link skipped at the source (a reparse point such as a cloud placeholder), and a file hidden only there.
        a.PutLink("placeholder.txt", "elsewhere");
        b.Put("placeholder.txt", "the real content", Old);
        a.Put("report.docx", "q", Old, hidden: true);
        b.Put("report.docx", "q", Old);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions
        {
            Direction = StorageSyncDirection.Mirror,
            DeleteExtraneous = true,
            Compare = new StorageCompareOptions { IncludeHidden = false }
        })).Value!;

        Assert.Equal(0, report.Deleted);
        Assert.Equal("the real content", b.Text("placeholder.txt"));
        Assert.True(b.Has("report.docx"));
    }

    [WindowsFact] // needs-review A24 (acceptance R3-7, with a second file so the empty-side rule does not hide it)
    public async Task A_file_hidden_by_attribute_at_the_source_keeps_its_copy_under_mirror()
    {
        using var directory = new TestDirectory();
        var (a, _, dirA, _) = LocalPair(directory);
        var remote = new MemoryStorage("remote");
        File.WriteAllText(Path.Combine(dirA, "report.docx"), "quarterly");
        File.WriteAllText(Path.Combine(dirA, "other.txt"), "visible");
        var mirror = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true, Compare = new StorageCompareOptions { IncludeHidden = false } };
        Assert.True((await a.SyncAsync("", remote, "", mirror)).IsSuccess);
        File.SetAttributes(Path.Combine(dirA, "report.docx"), File.GetAttributes(Path.Combine(dirA, "report.docx")) | FileAttributes.Hidden);

        var second = (await a.SyncAsync("", remote, "", mirror)).Value!;

        Assert.Equal(0, second.Deleted);
        Assert.True(remote.Has("report.docx"));
    }

    // ------------------------------------------------------------ A25: a committed copy keeps its baseline entry

    [Fact] // needs-review A25
    public async Task A_cancel_landing_during_the_promote_still_records_the_copy_and_sets_its_time()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "abc", Old);
        using var cancel = new CancellationTokenSource();
        b.BeforeMove = (_, to, _) =>
        {
            if (to == "f.txt") cancel.Cancel();
        };

        var report = (await a.SyncAsync("", b, "", TwoWay(store), cancel.Token)).Value!;

        Assert.True(report.Cancelled);
        Assert.Equal(StorageSyncActionOutcome.Applied, Step(report, "f.txt").Outcome);
        Assert.Equal(3, (await Entry(store, "f.txt"))!.Destination!.Size);
        Assert.Equal(Old, (await b.GetInfoAsync("f.txt")).Value!.LastModified);
    }

    [Fact] // needs-review A25
    public async Task A_copy_whose_read_back_fails_is_recorded_with_what_was_written()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "abc", Old);
        var moved = false;
        b.AfterMove = (_, to, result) =>
        {
            if (to == "f.txt") moved = true;
            return result;
        };
        b.FailGetInfo = path => moved && path == "f.txt" ? StorageErrors.Unavailable("busy") : null;

        var report = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;

        Assert.Equal(StorageSyncActionOutcome.Applied, Step(report, "f.txt").Outcome);
        var entry = (await Entry(store, "f.txt"))!;
        Assert.Equal(3, entry.Destination!.Size);
        Assert.Equal(Old, entry.Destination.Modified);
    }

    [Fact] // needs-review A25
    public async Task A_same_size_write_by_someone_else_right_after_the_copy_is_not_recorded_as_ours()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "abc", Old);
        a.Put("keep.txt", "k", Old);
        var once = 0;
        b.AfterSetTimestamps = path =>
        {
            if (path == "f.txt" && Interlocked.Exchange(ref once, 1) == 0) b.Put("f.txt", "xyz", New);
        };

        Assert.True((await a.SyncAsync("", b, "", TwoWay(store))).IsSuccess);
        var next = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;

        // Their edit is not mistaken for the synced version: the next run sees it.
        Assert.Contains(next.Actions, action => action.RelativePath == "f.txt");
    }

    // ------------------------------------------------------------ A29: cancellation

    [Fact] // needs-review A29: pins the 4.9 behaviour the docs describe
    public async Task A_sync_cancelled_while_applying_returns_success_with_cancelled_and_one_cancelled_while_planning_throws()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("a.txt", "1", Old);
        a.Put("b.txt", "2", Old);
        using var cancel = new CancellationTokenSource();
        b.BeforeMove = (_, _, _) => cancel.Cancel();

        var result = await a.SyncAsync("", b, "", new StorageSyncOptions { MaxConcurrency = 1 }, cancel.Token);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.Cancelled);
        Assert.Contains(result.Value.Results, step => step.Outcome == StorageSyncActionOutcome.NotRun);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.SyncAsync("", b, "", null, new CancellationToken(canceled: true)));
    }

    // ------------------------------------------------------------ A2 (sync side): a conflict rename moves only the planned version

    private static StorageSyncOptions KeepBoth => new() { Direction = StorageSyncDirection.TwoWay, ConflictPolicy = StorageSyncConflictPolicy.KeepBoth };

    [Fact] // needs-review A2
    public async Task A_conflict_rename_is_pinned_to_the_version_planned()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "ours", New);
        b.Put("f.txt", "theirs", Old);
        var theirs = (await b.GetInfoAsync("f.txt")).Value!.ETag;

        var report = (await a.SyncAsync("", b, "", KeepBoth)).Value!;

        Assert.Empty(report.Failed);
        var move = Assert.Single(b.Moves, move => move.From == "f.txt");
        Assert.Equal(theirs, move.Options!.ExpectedSourceETag);
    }

    [Fact] // needs-review A2
    public async Task A_version_written_after_the_rename_check_is_not_moved_aside()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "ours", New);
        b.Put("f.txt", "theirs", Old);
        var once = 0;
        b.BeforeMove = (from, _, _) =>
        {
            if (from == "f.txt" && Interlocked.Exchange(ref once, 1) == 0) b.Put("f.txt", "written meanwhile", New.AddDays(1));
        };

        var report = (await a.SyncAsync("", b, "", KeepBoth)).Value!;

        Assert.Equal(StorageSyncActionOutcome.Stale, Step(report, "f.txt", StorageSyncActionKind.RenameAtDestination).Outcome);
        Assert.Equal("written meanwhile", b.Text("f.txt"));
        Assert.Equal(["f.txt"], b.Paths());
    }

    [Fact] // needs-review A2
    public async Task Where_a_move_cannot_be_pinned_the_rename_is_a_pinned_copy_and_a_conditional_delete()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b") { PinnedMovesUnsupported = true };
        a.Put("f.txt", "ours", New);
        b.Put("f.txt", "theirs", Old);

        var report = (await a.SyncAsync("", b, "", KeepBoth)).Value!;

        Assert.Empty(report.Failed);
        Assert.Empty(report.Stale);
        Assert.DoesNotContain(b.Moves, move => move.From == "f.txt" && move.Options?.ExpectedSourceETag is null);
        Assert.Equal("ours", b.Text("f.txt"));
        var copy = Assert.Single(b.Paths(), path => path.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("theirs", b.Text(copy));
        Assert.Equal("theirs", a.Text(copy));
        Assert.Empty(b.Internal());
    }

    // ------------------------------------------------------------ A11 (sync side): a committed promote is a copy

    [Fact] // needs-review A11
    public async Task A_promote_that_committed_and_then_failed_is_an_applied_copy_with_its_baseline_entry()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "abc", Old);
        b.AfterMove = (_, to, result) => to == "f.txt" && result.IsSuccess
            ? Result.Failure(StorageErrors.PartialFailure("The provider's backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;{StorageErrorInfo.LeftBehindKey}=.cl-storage-backup-1"))
            : result;

        var report = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;

        var step = Step(report, "f.txt");
        Assert.Equal(StorageSyncActionOutcome.Applied, step.Outcome);
        Assert.True(StorageErrorInfo.TryGetDetail(step.Error, StorageErrorInfo.LeftBehindKey, out _));
        Assert.Equal("abc", b.Text("f.txt"));
        Assert.NotNull(await Entry(store, "f.txt"));
    }

    // ------------------------------------------------------------ B44: exceptions do not lose the report

    [Fact] // needs-review B44
    public async Task A_provider_that_throws_fails_its_step_and_the_run_keeps_its_report_and_baseline()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("a.txt", "1", Old);
        a.Put("b.txt", "2", Old);
        a.ThrowOnDownload = path => path == "b.txt" ? new IOException("The disk went away.") : null;

        var result = await a.SyncAsync("", b, "", TwoWay(store));

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(StorageSyncActionOutcome.Applied, Step(result.Value!, "a.txt").Outcome);
        Assert.Equal(StorageSyncActionOutcome.Failed, Step(result.Value!, "b.txt").Outcome);
        Assert.True(result.Value!.BaselineSaved);
        Assert.NotNull(await Entry(store, "a.txt"));
    }

    private sealed class ThrowingStore(bool onLoad) : IStorageSyncStateStore
    {
        public Task<StorageSyncBaseline?> LoadAsync(string syncId, CancellationToken cancellationToken) =>
            onLoad ? throw new InvalidOperationException("database down") : Task.FromResult<StorageSyncBaseline?>(null);

        public Task<bool> SaveAsync(string syncId, StorageSyncBaseline baseline, long expectedGeneration, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database down");
    }

    [Fact] // needs-review B44 (and C: BaselineSaved=false gives a reason)
    public async Task A_state_store_that_throws_gives_a_failure_or_a_report_with_the_reason_not_an_exception()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "1", Old);

        var saving = await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, StateStore = new ThrowingStore(onLoad: false), SyncId = "s" });
        var loading = await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, StateStore = new ThrowingStore(onLoad: true), SyncId = "s" });

        Assert.True(saving.IsSuccess);
        Assert.Equal(1, saving.Value!.Copied);
        Assert.False(saving.Value.BaselineSaved);
        Assert.NotNull(saving.Value.BaselineError);
        Assert.True(loading.IsFailure);
    }

    // ------------------------------------------------------------ B45: entries of paths left alone are carried over

    [Fact] // needs-review B45 and E (filter change between runs)
    public async Task A_deletion_made_while_a_path_was_excluded_is_carried_over_when_the_filter_is_lifted()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("a.txt", "1", Old);
        a.Put("keep.txt", "k", Old);
        Assert.True((await a.SyncAsync("", b, "", TwoWay(store))).IsSuccess);
        await b.DeleteAsync("a.txt");

        var excluded = (await a.SyncAsync("", b, "", TwoWay(store) with { Compare = new StorageCompareOptions { Exclude = ["a.txt"] } })).Value!;
        Assert.Empty(excluded.Actions);
        Assert.NotNull(await Entry(store, "a.txt"));

        await a.SyncAsync("", b, "", TwoWay(store));
        Assert.False(a.Has("a.txt"));
        Assert.False(b.Has("a.txt"));
    }

    [Fact] // needs-review B45 and E (two-way type clash)
    public async Task A_path_under_a_file_folder_clash_keeps_its_baseline_entry()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("x", "file", Old);
        a.Put("keep.txt", "k", Old);
        Assert.True((await a.SyncAsync("", b, "", TwoWay(store))).IsSuccess);
        await b.DeleteAsync("x");
        b.Put("x/child.txt", "c", Old);

        var clash = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;

        Assert.Contains(clash.Plan.Warnings, warning => warning.Contains("'x'", StringComparison.Ordinal));
        Assert.DoesNotContain(clash.Actions, action => action.RelativePath.StartsWith('x'));
        Assert.NotNull(await Entry(store, "x"));
        Assert.Equal("file", a.Text("x"));
    }

    // ------------------------------------------------------------ B46: the promote is conditional

    [Fact] // needs-review B46
    public async Task A_destination_changed_right_before_the_promote_is_not_overwritten()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "new content", New);
        b.Put("f.txt", "old", Old);
        var fired = 0;
        b.AfterGetInfo = (path, _) =>
        {
            if (path == "f.txt" && b.Internal().Length > 0 && Interlocked.Exchange(ref fired, 1) == 0)
                b.Put("f.txt", "concurrent", Old.AddHours(1));
        };

        var report = (await a.SyncAsync("", b, "")).Value!;

        Assert.Equal("concurrent", b.Text("f.txt"));
        Assert.Equal(StorageSyncActionOutcome.Stale, Step(report, "f.txt").Outcome);
        Assert.Empty(b.Internal());
    }

    // ------------------------------------------------------------ B47: a planned delete checks the type

    [Fact] // needs-review B47
    public async Task A_planned_delete_leaves_a_path_whose_type_changed_after_the_plan()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("keep.txt", "k", Old);
        b.Put("keep.txt", "k", Old);
        b.Put("x", "file", Old);
        b.PutDirectory("d");
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true };
        var plan = (await a.PlanSyncAsync("", b, "", options)).Value!;
        await b.DeleteAsync("x");
        b.PutDirectory("x");
        await b.DeleteAsync("d");
        b.Put("d", "now a file", Old);

        var report = (await a.ApplySyncAsync("", b, "", plan, plan.Digest, options)).Value!;

        Assert.Equal(StorageSyncActionOutcome.Stale, Step(report, "x").Outcome);
        Assert.Equal(StorageSyncActionOutcome.Stale, Step(report, "d").Outcome);
        Assert.True(b.Has("x"));
        Assert.Equal("now a file", b.Text("d"));
    }

    // ------------------------------------------------------------ B48: the plan binds what shapes the apply

    [Fact] // needs-review B48
    public async Task A_plan_applies_only_with_its_options_to_its_connections_and_in_its_format()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var c = new MemoryStorage("c");
        a.Put("f.txt", "1", Old);
        var options = new StorageSyncOptions();
        var plan = (await a.PlanSyncAsync("", b, "", options)).Value!;

        var widened = await a.ApplySyncAsync("", b, "", plan, plan.Digest, options with { Compare = new StorageCompareOptions { TimeTolerance = TimeSpan.FromHours(1) } });
        var otherPair = await a.ApplySyncAsync("", c, "", plan, plan.Digest, options);
        var older = plan with { SchemaVersion = 0 };
        older = older with { Digest = older.ComputeDigest() };
        var oldFormat = await a.ApplySyncAsync("", b, "", older, older.Digest, options);

        Assert.Equal(StorageErrors.InvalidContentCode, widened.Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, otherPair.Error?.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, oldFormat.Error?.Code);
        Assert.Contains("\"schemaVersion\":2", plan.ToJson(), StringComparison.Ordinal);
        Assert.False(b.Has("f.txt"));
        Assert.False(c.Has("f.txt"));
        Assert.True((await a.ApplySyncAsync("", b, "", plan, plan.Digest, options)).IsSuccess);
    }

    // ------------------------------------------------------------ B49: deletion safety

    [Fact] // needs-review B49
    public async Task Deletion_safety_rejects_NaN_counts_files_and_never_prints_a_share_equal_to_the_limit()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("keep1.txt", "1", Old);
        a.Put("keep2.txt", "2", Old);
        b.Put("keep1.txt", "1", Old);
        b.Put("keep2.txt", "2", Old);
        b.Put("old/extra.txt", "x", Old);
        var mirror = new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true };

        var nan = await a.PlanSyncAsync("", b, "", mirror with { MaxDeletePercent = double.NaN });
        var share = (await a.PlanSyncAsync("", b, "", mirror with { MaxDeletePercent = 33.3 })).Value!;
        var count = (await a.PlanSyncAsync("", b, "", mirror with { MaxDeletes = 0 })).Value!;

        Assert.Equal(StorageErrors.InvalidContentCode, nan.Error?.Code);
        Assert.Contains(share.Warnings, warning => warning.Contains("33.33%", StringComparison.Ordinal));
        Assert.Contains(count.Warnings, warning => warning.Contains("1 file deletion(s) and 1 folder deletion(s)", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ B50: a kept folder keeps its entry

    [Fact] // needs-review B50
    public async Task A_folder_kept_by_excluded_items_is_not_recreated_on_the_side_that_deleted_it()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        foreach (var side in new[] { a, b })
        {
            side.Put("d/a.txt", "a", Old);
            side.Put("d/x.log", "log", Old);
            side.Put("keep.txt", "k", Old);
        }
        var options = TwoWay(store) with { Compare = new StorageCompareOptions { Exclude = ["**/*.log"] } };
        Assert.True((await a.SyncAsync("", b, "", options)).IsSuccess);
        await b.DeleteAsync("d", new StorageDeleteOptions { Recursive = true });

        var second = (await a.SyncAsync("", b, "", options)).Value!;
        Assert.False(a.Has("d/a.txt"));
        Assert.True(a.Has("d/x.log"));

        var third = (await a.PlanSyncAsync("", b, "", options)).Value!;
        Assert.DoesNotContain(third.Actions, action => action.RelativePath == "d" && action.Kind == StorageSyncActionKind.CreateDirectory);
        Assert.Empty(second.Failed);
    }

    // ------------------------------------------------------------ B51: a missing destination root is created first

    [Fact] // needs-review B51
    public async Task A_missing_destination_folder_is_created_before_any_copy()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "1", Old);
        a.Put("g.txt", "2", Old);

        var report = (await a.SyncAsync("", b, "new/root")).Value!;

        var first = report.Actions[0];
        Assert.Equal(StorageSyncActionKind.CreateDirectory, first.Kind);
        Assert.Equal(string.Empty, first.RelativePath);
        Assert.Equal("1", b.Text("new/root/f.txt"));
        Assert.Empty(report.Failed);
    }

    // ------------------------------------------------------------ B52: conflicts are named; NewerWins ties do not block

    [Fact] // needs-review B52
    public async Task A_blocked_sync_names_its_conflicts_and_a_newer_wins_tie_does_not_block_the_rest()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "ours", New);
        b.Put("f.txt", "theirs", Old);
        var blocked = await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay });

        var c = new MemoryStorage("c");
        var d = new MemoryStorage("d");
        c.Put("same.txt", "aa", Old);
        d.Put("same.txt", "bbb", Old);
        c.Put("new.txt", "n", Old);
        var tie = await c.SyncAsync("", d, "", new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, ConflictPolicy = StorageSyncConflictPolicy.NewerWins });

        Assert.Equal(StorageErrors.ConflictCode, blocked.Error?.Code);
        Assert.Contains("'f.txt'", blocked.Error!.Message, StringComparison.Ordinal);
        Assert.True(StorageErrorInfo.TryGetDetail(blocked.Error, "conflicts", out var count) && count == "1");
        Assert.True(tie.IsSuccess, tie.Error?.Message);
        Assert.Equal("n", d.Text("new.txt"));
        Assert.Equal("bbb", d.Text("same.txt"));
        Assert.Equal(StorageSyncActionOutcome.NotRun, Step(tie.Value!, "same.txt").Outcome);
    }

    // ------------------------------------------------------------ B53: a source without ETag is re-checked

    [Fact] // needs-review B53
    public async Task A_source_without_an_ETag_that_changed_while_it_streamed_is_stale()
    {
        var a = new MemoryStorage("a") { NoETags = true };
        var b = new MemoryStorage("b");
        a.Put("f.txt", "abc", Old);
        var once = 0;
        a.AfterDownload = path =>
        {
            if (path == "f.txt" && Interlocked.Exchange(ref once, 1) == 0) a.Put("f.txt", "abcd", New);
        };

        var report = (await a.SyncAsync("", b, "")).Value!;

        Assert.Equal(StorageSyncActionOutcome.Stale, Step(report, "f.txt").Outcome);
        Assert.False(b.Has("f.txt"));
    }

    // ------------------------------------------------------------ B54: Verify confirms and records the digest

    [Fact] // needs-review B54
    public async Task Verify_records_the_content_digest_in_the_baseline()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "abc", Old);

        Assert.True((await a.SyncAsync("", b, "", TwoWay(store) with { Verify = true })).IsSuccess);

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abc")));
        var entry = (await Entry(store, "f.txt"))!;
        Assert.Equal(digest, entry.Source!.Sha256);
        Assert.Equal(digest, entry.Destination!.Sha256);
    }

    [Fact] // needs-review B54
    public async Task Verify_reports_a_copy_replaced_right_after_its_promote()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "abc", Old);
        b.AfterMove = (_, to, result) =>
        {
            if (to == "f.txt") b.Put("f.txt", "someone else's longer content", New);
            return result;
        };

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Verify = true })).Value!;

        Assert.Equal(StorageSyncActionOutcome.Failed, Step(report, "f.txt").Outcome);
        Assert.Equal("someone else's longer content", b.Text("f.txt"));
    }

    // ------------------------------------------------------------ B55: stopped steps, and the apply lock

    [Fact] // needs-review B55
    public async Task A_step_ended_by_the_stop_is_not_run_not_stale_and_the_apply_lock_is_released()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "abc", Old);
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        a.FailGetInfo = path =>
        {
            if (path != "f.txt" || Interlocked.Increment(ref calls) != 2) return null;
            cancel.Cancel();
            return StorageErrors.Cancelled("The request was cancelled.");
        };

        var report = (await a.SyncAsync("", b, "", TwoWay(store) with { SyncId = "b55" }, cancel.Token)).Value!;

        Assert.True(report.Cancelled);
        Assert.Equal(StorageSyncActionOutcome.NotRun, Step(report, "f.txt").Outcome);
        // This sync's own lock only: other tests may be applying in parallel.
        Assert.False(StorageSync.IsApplyGateHeld("b55"));
    }

    // ------------------------------------------------------------ B56: undecidable content, and the hashing budget

    [Fact] // needs-review B56
    public async Task Content_that_could_not_be_compared_is_left_alone_while_size_and_time_agree()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "aaa", Old);
        b.Put("f.txt", "bbb", Old);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions
        {
            Compare = new StorageCompareOptions { CompareBy = StorageCompareBy.Size | StorageCompareBy.Time | StorageCompareBy.Checksum, MaxHashedFiles = 0 }
        })).Value!;

        Assert.Equal(0, report.Copied);
        Assert.Contains(report.Plan.Warnings, warning => warning.Contains("f.txt", StringComparison.Ordinal));
        Assert.Equal("bbb", b.Text("f.txt"));
    }

    // ------------------------------------------------------------ E: the baseline after each outcome

    private static async Task<(MemoryStorage A, MemoryStorage B, InMemoryStorageSyncStateStore Store, StorageSyncBaselineEntry Before)> SyncedAsync()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        var store = new InMemoryStorageSyncStateStore();
        a.Put("f.txt", "v1", Old);
        a.Put("keep.txt", "k", Old);
        Assert.True((await a.SyncAsync("", b, "", TwoWay(store))).IsSuccess);
        return (a, b, store, (await Entry(store, "f.txt"))!);
    }

    [Fact] // needs-review E: baseline after Failed
    public async Task A_failed_step_keeps_the_previous_entry_and_the_next_run_catches_up()
    {
        var (a, b, store, before) = await SyncedAsync();
        a.Put("f.txt", "v2", New);
        a.FailDownload = path => path == "f.txt" ? StorageErrors.PermissionDenied("locked") : null;

        var failed = (await a.SyncAsync("", b, "", TwoWay(store))).Value!;
        Assert.Equal(StorageSyncActionOutcome.Failed, Step(failed, "f.txt").Outcome);
        Assert.Equal(before, await Entry(store, "f.txt"));

        a.FailDownload = null;
        await a.SyncAsync("", b, "", TwoWay(store));
        Assert.Equal("v2", b.Text("f.txt"));
    }

    [Fact] // needs-review E: baseline after Stale
    public async Task A_stale_step_keeps_the_previous_entry_and_the_next_run_catches_up()
    {
        var (a, b, store, before) = await SyncedAsync();
        a.Put("f.txt", "v2", New);
        var plan = (await a.PlanSyncAsync("", b, "", TwoWay(store))).Value!;
        a.Put("f.txt", "v3!", New.AddHours(1));

        var stale = (await a.ApplySyncAsync("", b, "", plan, plan.Digest, TwoWay(store))).Value!;
        Assert.Equal(StorageSyncActionOutcome.Stale, Step(stale, "f.txt").Outcome);
        Assert.Equal(before, await Entry(store, "f.txt"));

        await a.SyncAsync("", b, "", TwoWay(store));
        Assert.Equal("v3!", b.Text("f.txt"));
    }

    [Fact] // needs-review E: baseline after Withheld
    public async Task A_withheld_delete_keeps_the_previous_entry_and_the_next_run_catches_up()
    {
        var (a, b, store, before) = await SyncedAsync();
        await a.DeleteAsync("f.txt");

        var withheld = (await a.SyncAsync("", b, "", TwoWay(store) with { MaxDeletes = 0 })).Value!;
        Assert.Equal(StorageSyncActionOutcome.Withheld, Step(withheld, "f.txt").Outcome);
        Assert.Equal(before, await Entry(store, "f.txt"));

        await a.SyncAsync("", b, "", TwoWay(store));
        Assert.False(b.Has("f.txt"));
    }

    [Fact] // needs-review E: baseline after NotRun and after a cancel
    public async Task Steps_not_run_after_a_failure_or_a_cancel_keep_their_previous_entries()
    {
        var (a, b, store, before) = await SyncedAsync();
        a.Put("a1.txt", "new", Old);
        a.Put("f.txt", "v2", New);
        a.FailDownload = path => path == "a1.txt" ? StorageErrors.PermissionDenied("locked") : null;

        var stopped = (await a.SyncAsync("", b, "", TwoWay(store) with { ContinueOnError = false, MaxConcurrency = 1 })).Value!;
        Assert.Equal(StorageSyncActionOutcome.NotRun, Step(stopped, "f.txt").Outcome);
        Assert.Equal(before, await Entry(store, "f.txt"));

        a.FailDownload = null;
        using var cancel = new CancellationTokenSource();
        b.BeforeMove = (_, to, _) =>
        {
            if (to == "a1.txt") cancel.Cancel();
        };
        var cancelled = (await a.SyncAsync("", b, "", TwoWay(store) with { MaxConcurrency = 1 }, cancel.Token)).Value!;
        Assert.True(cancelled.Cancelled);
        Assert.Equal(StorageSyncActionOutcome.NotRun, Step(cancelled, "f.txt").Outcome);
        Assert.Equal(before, await Entry(store, "f.txt"));
        Assert.NotNull(await Entry(store, "a1.txt"));

        b.BeforeMove = null;
        await a.SyncAsync("", b, "", TwoWay(store));
        Assert.Equal("v2", b.Text("f.txt"));
    }

    [Fact] // needs-review E: KeepBoth with a stale rename
    public async Task A_keep_both_whose_rename_went_stale_loses_nothing()
    {
        var a = new MemoryStorage("a");
        var b = new MemoryStorage("b");
        a.Put("f.txt", "ours", New);
        b.Put("f.txt", "theirs", Old);
        var plan = (await a.PlanSyncAsync("", b, "", KeepBoth)).Value!;
        b.Put("f.txt", "theirs, edited again", New.AddHours(1));

        var report = (await a.ApplySyncAsync("", b, "", plan, plan.Digest, KeepBoth)).Value!;

        Assert.Equal(StorageSyncActionOutcome.Stale, Step(report, "f.txt", StorageSyncActionKind.RenameAtDestination).Outcome);
        Assert.Empty(report.Failed);
        Assert.Equal("theirs, edited again", b.Text("f.txt"));
        Assert.Equal("ours", a.Text("f.txt"));
        Assert.Equal(["f.txt"], a.Paths());
        Assert.Equal(["f.txt"], b.Paths());
    }

    // ------------------------------------------------------------ C: conflict names keep compound extensions

    [Fact] // needs-review C
    public void A_conflict_copy_of_a_tar_gz_keeps_its_extension_whole()
    {
        var name = StorageSync.ConflictPath("backups/a.tar.gz", new StorageSyncIdentity(1, Old, "\"x\"", null));

        Assert.StartsWith("backups/a (conflict ", name, StringComparison.Ordinal);
        Assert.EndsWith(").tar.gz", name, StringComparison.Ordinal);
    }
}
