using System.Collections.Concurrent;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Xunit;
using static Storage.Tests.NeedsReviewTransferTests;

namespace Storage.Tests;

/// <summary>Transfer findings of the round-4 review (needs-review.md, R4-A and R4-B "Transfers").</summary>
public sealed class NeedsReviewRound4TransferTests
{
    private static readonly StorageTransferOptions Rename = new() { ConflictPolicy = StorageConflictPolicy.Rename };
    private static readonly StorageTransferOptions Resume = new() { ConflictPolicy = StorageConflictPolicy.Resume };

    private static InterceptBackend CommitsThenReportsALeftover(LocalStorageBackend local, string leftover = ".cl-storage-backup-provider.tmp") => new(local)
    {
        Move = async (from, to, options, token) =>
        {
            var moved = await local.MoveAsync(from, to, options, token);
            if (moved.IsFailure || !from.Contains(".cl-storage-", StringComparison.Ordinal)) return moved;
            // The provider committed, then could not remove its own backup.
            return Result.Failure(StorageErrors.PartialFailure("The provider's backup could not be removed.",
                $"{StorageErrorInfo.DestinationStateKey}=complete;{StorageErrorInfo.LeftBehindKey}={leftover}"));
        }
    };

    private static string Marker(string machine, long pid, long started) =>
        $"cl-storage-part-lock/1\n{Guid.NewGuid():N}\n{machine}\n{pid}\n{started}";

    // ---------------------------------------------------------------- R4-A1

    [Fact] // needs-review R4-A1
    public async Task A_same_server_rename_move_uses_the_servers_rename_on_a_single_session_and_takes_the_next_name_when_raced()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("s"));
        await local.UploadBytesAsync("f.txt", [1]);
        await local.UploadBytesAsync("g.txt", [2]);
        var uploads = 0;
        var moves = new ConcurrentQueue<string>();
        var raced = false;
        var server = new InterceptBackend(local)
        {
            Upload = (path, source, options, token) =>
            {
                Interlocked.Increment(ref uploads);
                return local.UploadAsync(path, source, options, token);
            },
            Move = async (from, to, options, token) =>
            {
                moves.Enqueue(from);
                if (to == "g (1).txt" && !raced)
                {
                    // Someone takes the chosen name between the choice and the server's rename.
                    raced = true;
                    await local.UploadBytesAsync("g (1).txt", [5]);
                }
                return await local.MoveAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("S", server.Named("S")).IsSuccess);
        // Like SFTP or FTP with Session.MaxSessions = 1: a relay through the client cannot run at all.
        StorageTransferPipeline.SetSessionLimit(server, 1);

        var report = await library.MoveAsync("S", "f.txt", "S", "g.txt", Rename);

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal("g (2).txt", report.WrittenPath);
        Assert.True(report.SourceDeleted);
        Assert.Equal(0, uploads);
        Assert.All(moves, from => Assert.Equal("f.txt", from));
        Assert.Equal([1], (await local.DownloadBytesAsync("g (2).txt")).Value!);
        Assert.Equal([5], (await local.DownloadBytesAsync("g (1).txt")).Value!);
        Assert.Equal([2], (await local.DownloadBytesAsync("g.txt")).Value!);
        Assert.Equal(["g (1).txt", "g (2).txt", "g.txt"], await ListAllAsync(local));
    }

    /// <summary>Like WebDAV: its MOVE is not an atomic rename for collections, and it refuses to pin a move to a version.</summary>
    private static InterceptBackend WebDavLike(LocalStorageBackend local, Action? upload = null, Func<string, Task>? beforeGetInfo = null) => new(local)
    {
        CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features & ~StorageFeature.AtomicMove),
        Upload = (path, source, options, token) =>
        {
            upload?.Invoke();
            return local.UploadAsync(path, source, options, token);
        },
        GetInfo = async (path, token) =>
        {
            if (beforeGetInfo is not null) await beforeGetInfo(path);
            return await local.GetInfoAsync(path, token);
        },
        Move = (from, to, options, token) => options?.ExpectedSourceETag is not null || options?.SourceVersionId is not null
            ? Task.FromResult(Result.Failure(StorageErrors.Unsupported("This connection cannot pin a move to a version.")))
            : local.MoveAsync(from, to, options, token)
    };

    [Fact] // needs-review R4-A1
    public async Task A_same_server_move_that_cannot_be_pinned_checks_the_source_just_before_the_servers_move_instead_of_relaying()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("dav"));
        await local.UploadBytesAsync("f.bin", [1, 2, 3]);
        var uploads = 0;
        var dav = WebDavLike(local, upload: () => Interlocked.Increment(ref uploads));
        Assert.True(library.RegisterBackend("Dav", dav.Named("Dav")).IsSuccess);
        StorageTransferPipeline.SetSessionLimit(dav, 1);

        var report = await library.MoveAsync("Dav", "f.bin", "Dav", "g.bin");

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(0, uploads);
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, report.ConditionEnforcement);
        Assert.True(report.SourceDeleted);
        Assert.Equal([1, 2, 3], (await local.DownloadBytesAsync("g.bin")).Value!);
        Assert.Equal(["g.bin"], await ListAllAsync(local));
    }

    [Fact] // needs-review R4-A1
    public async Task A_source_changed_before_an_unpinned_server_move_is_not_moved()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("dav"));
        await local.UploadBytesAsync("f.bin", [1, 2, 3]);
        var reads = 0;
        var dav = WebDavLike(local, beforeGetInfo: async path =>
        {
            // A writer replaces the source after the transfer first read it.
            if (path == "f.bin" && Interlocked.Increment(ref reads) == 2)
                await local.UploadBytesAsync("f.bin", [9, 9, 9, 9]);
        });
        Assert.True(library.RegisterBackend("Dav", dav.Named("Dav")).IsSuccess);

        var report = await library.MoveAsync("Dav", "f.bin", "Dav", "g.bin");

        Assert.Equal(StorageTransferOutcome.Failed, report.Outcome);
        Assert.Equal(StorageErrors.ConflictCode, report.Error?.Code);
        Assert.Equal([9, 9, 9, 9], (await local.DownloadBytesAsync("f.bin")).Value!);
        Assert.False((await local.ExistsAsync("g.bin")).Value);
    }

    // ---------------------------------------------------------------- R4-A3

    [Fact] // needs-review R4-A3
    public async Task A_conditional_move_the_provider_refuses_does_not_bring_back_a_destination_deleted_meanwhile()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b, "H");
        await local.UploadBytesAsync("target.bin", [1]);
        var seen = (await local.GetInfoAsync("target.bin")).Value!;
        var conditional = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                if (to == "target.bin" && options?.DestinationCondition is not null)
                {
                    // Someone deletes the destination; the provider enforces the condition in its move (412) and
                    // moves nothing.
                    await local.DeleteAsync("target.bin");
                    return Result.Failure(StorageErrors.Conflict("The destination changed.", $"{StorageErrorInfo.HttpStatusKey}=412"));
                }
                return await local.MoveAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("H", conditional.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin",
            new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = seen.ETag } });

        Assert.Equal(StorageErrors.ConflictCode, report.Error?.Code);
        Assert.False(report.DestinationCommitted);
        Assert.Null(report.BackupRestored);
        Assert.Empty(await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- R4-A4

    [Fact] // needs-review R4-A4
    public async Task A_confirm_that_fails_after_a_broken_promote_keeps_the_backup_and_reports_it()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("target.bin", [1]);
        var broken = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var moved = await local.MoveAsync(from, to, options, token);
                // A promote that "succeeds" but leaves the wrong content in place.
                if (to == "target.bin" && moved.IsSuccess) await local.UploadBytesAsync("target.bin", [9]);
                return moved;
            }
        };
        Assert.True(library.RegisterBackend("H", broken.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.True(report.DestinationCommitted);
        Assert.NotNull(report.BackupLeftBehind);
        Assert.Equal([1], (await local.DownloadBytesAsync(report.BackupLeftBehind!)).Value!);
    }

    // ---------------------------------------------------------------- R4-A9

    [Fact] // needs-review R4-A9
    public async Task A_part_file_another_process_holds_is_neither_appended_to_nor_touched()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(100_000);
        Assert.True(library.RegisterBackend("Src", Source(content)).IsSuccess);
        var local = Local(b, "D");
        Assert.True(library.RegisterBackend("D", local).IsSuccess);
        var part = StagedWriter.ResumableStagingPath("big.bin", StagedWriter.SourceKey("big.bin", content.Length, null, "\"v1\"", null));
        // A worker on another machine is appending to this part file right now; its bytes are not this transfer's.
        var theirs = Enumerable.Repeat((byte)0xEE, 40_000).ToArray();
        await local.UploadBytesAsync(part, theirs);
        await local.UploadBytesAsync(part + ".lock", Encoding.UTF8.GetBytes(Marker("another-host", 4242, 0)));

        var report = await library.CopyAsync("Src", "big.bin", "D", "big.bin", Resume);

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(content, (await local.DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(theirs, (await local.DownloadBytesAsync(part)).Value!);
        Assert.True((await local.ExistsAsync(part + ".lock")).Value);
    }

    [Fact] // needs-review R4-A9
    public async Task The_part_file_stays_held_until_after_it_is_promoted()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(50_000);
        Assert.True(library.RegisterBackend("Src", Source(content)).IsSuccess);
        var local = Local(b);
        bool? heldAtPromote = null;
        var destination = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                if (from.Contains(StagedWriter.ResumablePrefix, StringComparison.Ordinal) && to == "big.bin")
                    heldAtPromote = (await local.ExistsAsync(from + ".lock")).Value;
                return await local.MoveAsync(from, to, options, token);
            }
        };
        Assert.True(library.RegisterBackend("D", destination.Named("D")).IsSuccess);

        var report = await library.CopyAsync("Src", "big.bin", "D", "big.bin", Resume);

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.True(heldAtPromote);
        Assert.Equal(["big.bin"], await ListAllAsync(local));
    }

    [Fact] // needs-review R4-A9 (partner: a hold whose process is gone does not block a resume)
    public async Task A_part_file_whose_holder_process_is_gone_is_taken_over_and_resumed()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Content(100_000);
        Assert.True(library.RegisterBackend("Src", Source(content)).IsSuccess);
        var local = Local(b, "D");
        Assert.True(library.RegisterBackend("D", local).IsSuccess);
        var part = StagedWriter.ResumableStagingPath("big.bin", StagedWriter.SourceKey("big.bin", content.Length, null, "\"v1\"", null));
        await local.UploadBytesAsync(part, content[..60_000]);
        // Left by a worker on this machine that crashed: no process has that id now.
        await local.UploadBytesAsync(part + ".lock", Encoding.UTF8.GetBytes(Marker(Environment.MachineName, int.MaxValue - 7, 1)));

        var report = await library.CopyAsync("Src", "big.bin", "D", "big.bin", Resume);

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(60_000, report.BytesResumed);
        Assert.Equal(40_000, report.Bytes);
        Assert.Equal(content, (await local.DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(["big.bin"], await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- R4-A11

    [Fact] // needs-review R4-A11
    public async Task An_upload_resume_identified_only_by_a_path_is_refused()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);

        var uploaded = await local.UploadAsync("u.bin", new MemoryStream(Content(1000)),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "C:/data/u.bin" });

        Assert.Equal(StorageErrors.InvalidContentCode, uploaded.Error?.Code);
        Assert.False((await local.ExistsAsync("u.bin")).Value);
    }

    [Fact] // needs-review R4-A11
    public async Task An_upload_resume_with_a_time_or_a_content_version_identity_is_accepted()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);

        var byTime = await local.UploadAsync("t.bin", new MemoryStream(Content(1000)),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "C:/data/t.bin", SourceLastModified = DateTimeOffset.UnixEpoch });
        var byVersion = await local.UploadAsync("v.bin", new MemoryStream(Content(1000)),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "sha256:abc", SourceIdentityIsContentVersion = true });

        Assert.True(byTime.IsSuccess, byTime.Error?.ToString());
        Assert.True(byVersion.IsSuccess, byVersion.Error?.ToString());
    }

    // ---------------------------------------------------------------- R4-B1

    [Fact] // needs-review R4-B1
    public async Task A_directory_transfer_reports_what_a_provider_left_behind_for_one_of_its_files()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        Assert.True(library.RegisterBackend("H", CommitsThenReportsALeftover(local).Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/b.txt", [2]);

        var report = await library.CopyAsync("Default", "tree", "H", "out");

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(".cl-storage-backup-provider.tmp", report.BackupLeftBehind);
    }

    [Fact] // needs-review R4-B1
    public async Task Staged_uploads_and_streamed_writes_report_a_leftover_the_provider_could_not_remove()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        // The provider's own backup really is still there.
        await local.UploadBytesAsync(".cl-storage-backup-provider.tmp", [0]);
        var storage = CommitsThenReportsALeftover(local);

        var uploaded = await StorageTransferPipeline.StagedUploadAsync(storage, "u.bin", new MemoryStream([1, 2]), new StorageUploadOptions { Verify = true }, CancellationToken.None);
        var writer = (await storage.OpenWriteAsync("w.bin")).Value!;
        await writer.WriteAsync(new byte[] { 3, 4, 5 });
        var committed = await writer.CommitAsync();

        foreach (var result in new[] { uploaded, committed })
        {
            Assert.Equal(StorageErrors.PartialFailureCode, result.Error?.Code);
            Assert.True(StorageErrorInfo.DestinationCommitted(result.Error));
            Assert.Contains($"{StorageErrorInfo.LeftBehindKey}=.cl-storage-backup-provider.tmp", result.Error!.Details, StringComparison.Ordinal);
        }
        Assert.Equal([1, 2], (await local.DownloadBytesAsync("u.bin")).Value!);
        Assert.Equal([3, 4, 5], (await local.DownloadBytesAsync("w.bin")).Value!);
    }

    // ---------------------------------------------------------------- R4-B2

    [Fact] // needs-review R4-B2
    public async Task A_directory_rollback_deletes_a_committed_file_only_under_its_committed_version()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        var conditions = new ConcurrentQueue<StorageMutationCondition?>();
        var promotes = 0;
        var destination = new InterceptBackend(local)
        {
            CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features | StorageFeature.ConditionalDelete),
            Move = (from, to, options, token) => to.StartsWith("out/", StringComparison.Ordinal) && Interlocked.Increment(ref promotes) == 2
                ? Task.FromResult(Result.Failure(StorageErrors.Unavailable("The server is briefly unavailable.")))
                : local.MoveAsync(from, to, options, token),
            Delete = (path, options, token) =>
            {
                if (path.StartsWith("out/", StringComparison.Ordinal) && !path.Contains(".cl-storage-", StringComparison.Ordinal))
                    conditions.Enqueue(options?.Condition);
                return local.DeleteAsync(path, options is null ? null : options with { Condition = null }, token);
            }
        };
        Assert.True(library.RegisterBackend("H", destination.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/b.txt", [2]);

        var report = await library.CopyAsync("Default", "tree", "H", "out");

        Assert.Equal(StorageTransferOutcome.Failed, report.Outcome);
        var condition = Assert.Single(conditions);
        Assert.NotNull(condition?.ExpectedETag);
        Assert.Empty(await ListAllAsync(local));
    }

    [Fact] // needs-review R4-B2
    public async Task A_directory_rollback_leaves_a_committed_file_whose_version_is_unknown()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        var promotes = 0;
        var confirmReads = new ConcurrentDictionary<string, int>();
        var destination = new InterceptBackend(local)
        {
            Move = (from, to, options, token) => to.StartsWith("out/", StringComparison.Ordinal) && Interlocked.Increment(ref promotes) == 2
                ? Task.FromResult(Result.Failure(StorageErrors.Unavailable("The server is briefly unavailable.")))
                : local.MoveAsync(from, to, options, token),
            // The read right after the first commit fails, so what was committed is not known.
            GetInfo = (path, token) => path.StartsWith("out/", StringComparison.Ordinal) && !path.Contains(".cl-storage-", StringComparison.Ordinal) &&
                confirmReads.AddOrUpdate(path, 1, (_, n) => n + 1) == 1 && promotes == 1
                ? Task.FromResult(Result<StorageItem>.Failure(StorageErrors.Unavailable("The server is briefly unavailable.")))
                : local.GetInfoAsync(path, token)
        };
        Assert.True(library.RegisterBackend("H", destination.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/b.txt", [2]);

        var report = await library.CopyAsync("Default", "tree", "H", "out");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.Contains("out/a.txt", await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- R4-B5

    [Fact] // needs-review R4-B5
    public async Task A_directory_move_cancelled_while_deleting_its_sources_is_announced_and_says_what_was_deleted()
    {
        var events = new RecordingEventBus();
        var (library, directory, _, _) = await TwoConnectionsAsync(events);
        using var _l = library; using var _d = directory;
        var local = Local(directory.CreateDirectory("s"));
        await local.UploadBytesAsync("tree/a.txt", [1]);
        await local.UploadBytesAsync("tree/b.txt", [2, 2]);
        using var cancel = new CancellationTokenSource();
        var deletes = 0;
        var source = new InterceptBackend(local)
        {
            Delete = async (path, options, token) =>
            {
                if (Interlocked.Increment(ref deletes) == 2)
                {
                    await cancel.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }
                return await local.DeleteAsync(path, options, token);
            }
        };
        Assert.True(library.RegisterBackend("S", source.Named("S")).IsSuccess);

        var report = await library.MoveAsync("S", "tree", "B", "tree", cancellationToken: cancel.Token);

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.True(report.DestinationCommitted);
        Assert.False(report.SourceDeleted);
        Assert.Contains("sourceItemsDeleted=1", report.Error?.Details, StringComparison.Ordinal);
        var copied = Assert.Single(events.Published.OfType<StorageCrossConnectionCopyCompletedEvent>());
        Assert.Equal(2, copied.Files);
    }

    // ---------------------------------------------------------------- R4-B6

    [Fact] // needs-review R4-B6
    public async Task A_destination_without_server_side_copy_or_a_restoring_replace_keeps_the_previous_file_when_its_promote_fails()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("target.bin", [1]);
        var plain = new InterceptBackend(local)
        {
            CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features & ~StorageFeature.ServerSideCopy),
            Move = async (from, to, options, token) =>
            {
                if (to != "target.bin" || !from.Contains(".cl-storage-transfer-", StringComparison.Ordinal) || from.Contains("backup", StringComparison.Ordinal))
                    return await local.MoveAsync(from, to, options, token);
                // Its replace removes the old file and then fails: it keeps nothing of its own.
                await local.DeleteAsync("target.bin", new StorageDeleteOptions { IgnoreMissing = true });
                return Result.Failure(StorageErrors.ConnectionLost("The connection dropped."));
            }
        };
        Assert.True(library.RegisterBackend("H", plain.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin");

        Assert.Equal(StorageTransferOutcome.Failed, report.Outcome);
        Assert.True(report.BackupRestored);
        Assert.Equal([1], (await local.DownloadBytesAsync("target.bin")).Value!);
        Assert.Equal(["target.bin"], await ListAllAsync(local));
    }

    // ---------------------------------------------------------------- R4-B7

    [Fact] // needs-review R4-B7
    public async Task Directory_transfers_report_their_condition_enforcement_and_a_native_directory_move_its_files()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/sub/b.txt", [2, 2]);

        var copied = await library.CopyAsync("Default", "tree", "B", "out", new StorageTransferOptions { Overwrite = false });
        var moved = await library.MoveAsync("B", "out", "B", "moved");

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.NotEqual(StorageConditionEnforcement.None, copied.ConditionEnforcement);
        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.Equal(2, moved.Files);
        Assert.Equal(3, moved.Bytes);
    }

    [Fact] // needs-review R4-B7
    public async Task A_cancelled_directory_transfer_reports_the_restore_it_made()
    {
        var (library, directory, _, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = Local(b);
        await local.UploadBytesAsync("out/a.txt", [0]);
        using var cancel = new CancellationTokenSource();
        var hooked = new InterceptBackend(local)
        {
            Move = async (from, to, options, token) =>
            {
                var moved = await local.MoveAsync(from, to, options, token);
                if (to == "out/a.txt") await cancel.CancelAsync();
                return moved;
            }
        };
        Assert.True(library.RegisterBackend("H", hooked.Named("H")).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("tree/a.txt", [1]);
        await library.GetStorage("Default").UploadBytesAsync("tree/b.txt", [2]);

        var report = await library.CopyAsync("Default", "tree", "H", "out", cancellationToken: cancel.Token);

        Assert.Equal(StorageTransferOutcome.Cancelled, report.Outcome);
        Assert.True(report.BackupRestored);
        Assert.Equal([0], (await local.DownloadBytesAsync("out/a.txt")).Value!);
    }

    // ---------------------------------------------------------------- R4-B8

    [Fact] // needs-review R4-B8
    public async Task A_failed_streamed_commit_names_the_staging_object_it_could_not_remove()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.Path);
        var storage = new InterceptBackend(local)
        {
            Move = (from, to, options, token) => Task.FromResult(Result.Failure(StorageErrors.Unavailable("The server is briefly unavailable."))),
            Delete = (path, options, token) => Task.FromResult(Result.Failure(StorageErrors.PermissionDenied("Deleting is not allowed.")))
        };
        var writer = (await storage.OpenWriteAsync("w.bin")).Value!;
        await writer.WriteAsync(new byte[] { 3, 4, 5 });

        var committed = await writer.CommitAsync();

        Assert.Equal(StorageErrors.UnavailableCode, committed.Error?.Code);
        Assert.False(StorageErrorInfo.DestinationCommitted(committed.Error));
        var left = Assert.Single(StagedWriter.LeftBehind(committed.Error));
        Assert.True((await local.ExistsAsync(left)).Value);
        Assert.False((await local.ExistsAsync("w.bin")).Value);
    }
}
