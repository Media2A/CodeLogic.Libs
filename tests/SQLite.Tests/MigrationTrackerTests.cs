using System.Text.Json;
using CL.SQLite.Services;
using CodeLogic.Core.Logging;
using Xunit;

namespace SQLite.Tests;

/// <summary>
/// <see cref="MigrationTracker"/> is a public class backed by a JSON file under
/// <c>{dataDirectory}/migrations/migration_history.json</c>; it takes no connection and
/// needs no runtime, so each test gets its own throwaway directory.
/// </summary>
public sealed class MigrationTrackerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cl_sqlite_mt_" + Guid.NewGuid().ToString("N"));

    private readonly MigrationTracker _tracker;

    public MigrationTrackerTests()
    {
        Directory.CreateDirectory(_dir);
        _tracker = new MigrationTracker(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string HistoryFile => Path.Combine(_dir, "migrations", "migration_history.json");

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void The_constructor_creates_the_migrations_directory_eagerly()
        => Assert.True(Directory.Exists(Path.Combine(_dir, "migrations")));

    [Fact]
    public void The_constructor_does_not_write_a_history_file_until_something_is_recorded()
        => Assert.False(File.Exists(HistoryFile));

    [Fact]
    public void A_nested_data_directory_that_does_not_exist_yet_is_created()
    {
        var nested = Path.Combine(_dir, "deep", "deeper");
        _ = new MigrationTracker(nested);
        Assert.True(Directory.Exists(Path.Combine(nested, "migrations")));
    }

    // ── Empty state ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAppliedMigrationsAsync_is_empty_before_anything_is_recorded()
        => Assert.Empty(await _tracker.GetAppliedMigrationsAsync());

    [Fact]
    public async Task HasMigrationBeenAppliedAsync_is_false_for_an_unknown_id()
        => Assert.False(await _tracker.HasMigrationBeenAppliedAsync("never_ran"));

    [Fact]
    public async Task RemoveMigrationRecordAsync_returns_false_when_there_is_nothing_to_remove()
        => Assert.False(await _tracker.RemoveMigrationRecordAsync("never_ran"));

    // ── Record / query / remove round trip ───────────────────────────────────

    [Fact]
    public async Task RecordMigrationAsync_makes_the_migration_visible_to_both_readers()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(await _tracker.RecordMigrationAsync("m1", "first migration"));

        Assert.True(await _tracker.HasMigrationBeenAppliedAsync("m1"));

        var record = Assert.Single(await _tracker.GetAppliedMigrationsAsync());
        Assert.Equal("m1", record.MigrationId);
        Assert.Equal("first migration", record.Description);
        Assert.InRange(record.AppliedAt, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task A_description_is_optional_and_round_trips_as_null()
    {
        Assert.True(await _tracker.RecordMigrationAsync("no_desc"));

        var record = Assert.Single(await _tracker.GetAppliedMigrationsAsync());
        Assert.Null(record.Description);
    }

    [Fact]
    public async Task Recording_the_same_id_twice_succeeds_but_does_not_duplicate_the_entry()
    {
        Assert.True(await _tracker.RecordMigrationAsync("dup", "one"));
        Assert.True(await _tracker.RecordMigrationAsync("dup", "two"));

        var record = Assert.Single(await _tracker.GetAppliedMigrationsAsync());
        Assert.Equal("one", record.Description); // the first write wins
    }

    [Fact]
    public async Task Migration_ids_are_compared_case_sensitively()
    {
        await _tracker.RecordMigrationAsync("Case");

        Assert.True(await _tracker.HasMigrationBeenAppliedAsync("Case"));
        Assert.False(await _tracker.HasMigrationBeenAppliedAsync("case"));
    }

    [Fact]
    public async Task Records_are_returned_in_the_order_they_were_applied()
    {
        foreach (var id in new[] { "a", "b", "c" })
            await _tracker.RecordMigrationAsync(id);

        Assert.Equal(["a", "b", "c"], (await _tracker.GetAppliedMigrationsAsync()).Select(m => m.MigrationId));
    }

    [Fact]
    public async Task RemoveMigrationRecordAsync_removes_only_the_named_record()
    {
        await _tracker.RecordMigrationAsync("keep");
        await _tracker.RecordMigrationAsync("drop");

        Assert.True(await _tracker.RemoveMigrationRecordAsync("drop"));
        Assert.False(await _tracker.HasMigrationBeenAppliedAsync("drop"));
        Assert.True(await _tracker.HasMigrationBeenAppliedAsync("keep"));
        Assert.Single(await _tracker.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task A_removed_migration_can_be_recorded_again()
    {
        await _tracker.RecordMigrationAsync("redo", "first");
        await _tracker.RemoveMigrationRecordAsync("redo");
        await _tracker.RecordMigrationAsync("redo", "second");

        var record = Assert.Single(await _tracker.GetAppliedMigrationsAsync());
        Assert.Equal("second", record.Description);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task History_is_persisted_as_indented_JSON_and_reread_by_a_fresh_tracker()
    {
        await _tracker.RecordMigrationAsync("persisted", "on disk");

        Assert.True(File.Exists(HistoryFile));
        var json = await File.ReadAllTextAsync(HistoryFile);
        Assert.Contains("\n", json); // WriteIndented
        Assert.Contains("persisted", json);

        var reopened = new MigrationTracker(_dir);
        Assert.True(await reopened.HasMigrationBeenAppliedAsync("persisted"));
    }

    [Fact]
    public async Task A_MigrationRecord_written_by_hand_is_read_back_faithfully()
    {
        var applied = new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        await File.WriteAllTextAsync(HistoryFile, JsonSerializer.Serialize(
            new List<MigrationRecord> { new("hand", "written", applied) }));

        var record = Assert.Single(await new MigrationTracker(_dir).GetAppliedMigrationsAsync());
        Assert.Equal("hand", record.MigrationId);
        Assert.Equal("written", record.Description);
        Assert.Equal(applied, record.AppliedAt);
    }

    [Fact]
    public async Task A_corrupt_history_file_is_treated_as_empty_rather_than_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        await File.WriteAllTextAsync(HistoryFile, "{ this is not json ]");

        Assert.Empty(await _tracker.GetAppliedMigrationsAsync());
        Assert.False(await _tracker.HasMigrationBeenAppliedAsync("anything"));
    }

    /// <summary>
    /// A corrupt history file is discarded on read, so the next
    /// <see cref="MigrationTracker.RecordMigrationAsync"/> overwrites it with a one-entry list —
    /// any previously recorded migrations are lost. The recovery is deliberate, but it is no
    /// longer silent: see <see cref="A_corrupt_history_file_is_reported_before_it_is_discarded"/>.
    /// </summary>
    [Fact]
    public async Task Recording_over_a_corrupt_history_file_discards_the_old_contents()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        await File.WriteAllTextAsync(HistoryFile, "not json at all");

        Assert.True(await _tracker.RecordMigrationAsync("after_corruption"));

        var record = Assert.Single(await _tracker.GetAppliedMigrationsAsync());
        Assert.Equal("after_corruption", record.MigrationId);
    }

    /// <summary>
    /// The discard is announced with a warning naming the file, so the loss is traceable.
    /// </summary>
    [Fact]
    public async Task A_corrupt_history_file_is_reported_before_it_is_discarded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        await File.WriteAllTextAsync(HistoryFile, "{ this is not json ]");

        var logger = new WarningCapturingLogger();
        var tracker = new MigrationTracker(_dir, logger);

        Assert.Empty(await tracker.GetAppliedMigrationsAsync());

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(HistoryFile, warning, StringComparison.Ordinal);
    }

    // ── Record value semantics ────────────────────────────────────────────────

    [Fact]
    public void MigrationRecord_is_a_value_record_with_positional_members()
    {
        var at = DateTime.UtcNow;
        var a = new MigrationRecord("id", "desc", at);
        var b = new MigrationRecord("id", "desc", at);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { MigrationId = "other" });
        Assert.Contains("id", a.ToString());
    }

    // ── Concurrency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_records_through_one_tracker_are_all_retained()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _tracker.RecordMigrationAsync($"m{i:D2}", $"desc {i}"));
        Assert.All(await Task.WhenAll(tasks), ok => Assert.True(ok));

        var applied = await _tracker.GetAppliedMigrationsAsync();
        Assert.Equal(20, applied.Count);
        Assert.Equal(20, applied.Select(m => m.MigrationId).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_records_of_the_same_id_still_produce_exactly_one_entry()
    {
        var tasks = Enumerable.Range(0, 20).Select(_ => _tracker.RecordMigrationAsync("same"));
        await Task.WhenAll(tasks);

        Assert.Single(await _tracker.GetAppliedMigrationsAsync());
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_already_cancelled_token_cancels_a_read()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tracker.GetAppliedMigrationsAsync(cts.Token));
    }

    /// <summary>
    /// The write methods swallow every exception into a <c>false</c> return, but their
    /// <c>_lock.WaitAsync(ct)</c> sits *outside* that try block, so an already-cancelled token
    /// throws out of the method rather than returning <c>false</c>. Pinned because the return
    /// type invites the opposite assumption.
    /// </summary>
    [Fact]
    public async Task An_already_cancelled_token_throws_out_of_a_write_rather_than_returning_false()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tracker.RecordMigrationAsync("cancelled", null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tracker.RemoveMigrationRecordAsync("cancelled", cts.Token));

        // The lock is not left held: a subsequent uncancelled write still works.
        Assert.True(await _tracker.RecordMigrationAsync("after_cancel"));
    }
}

/// <summary>Collects warnings so the corrupt-history advisory can be asserted.</summary>
internal sealed class WarningCapturingLogger : ILogger
{
    public readonly List<string> Warnings = [];

    public void Trace(string message) { }
    public void Debug(string message) { }
    public void Debug(string message, params object?[] args) { }
    public void Info(string message) { }
    public void Info(string message, params object?[] args) { }
    public void Warning(string message) { lock (Warnings) Warnings.Add(message); }
    public void Warning(string message, params object?[] args) { lock (Warnings) Warnings.Add(message); }
    public void Error(string message, Exception? ex = null) { }
    public void Error(string message, params object?[] args) { }
    public void Critical(string message, Exception? ex = null) { }
}
