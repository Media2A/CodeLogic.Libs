using System.Security.Cryptography;
using System.Text;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Guaranteed transfers: reports, conditions, pinning, verification, resume, and streamed writes.</summary>
public sealed class GuaranteedTransferTests
{
    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task<(global::CL.Storage.StorageLibrary Library, TestDirectory Directory)> TwoConnectionsAsync()
    {
        var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        var library = new global::CL.Storage.StorageLibrary();
        var a = directory.CreateDirectory("a");
        var b = directory.CreateDirectory("b");
        await StorageLibraryTestSupport.InitializeAsync(library, context, configureLocal: local =>
        {
            local.Connections["Default"] = new() { RootPath = a };
            local.Connections["B"] = new() { RootPath = b };
        });
        return (library, directory);
    }

    private static async Task<List<string>> ListAllAsync(IStorageService storage)
    {
        var page = await storage.ListAsync("", new StorageListOptions { Recursive = true, IncludeInternal = true });
        return [.. page.Value!.Items.Select(item => item.Path)];
    }

    [Fact]
    public async Task A_verified_copy_reports_the_digest_and_the_new_destination_identity()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var content = "verified content"u8.ToArray();
        await library.GetStorage("Default").UploadBytesAsync("f.bin", content);

        var report = await library.CopyAsync("Default", "f.bin", "B", "copy.bin", new StorageTransferOptions { Verify = true, ExpectedSha256 = Sha(content) });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(StorageTransferOutcome.Completed, report.Outcome);
        Assert.Equal(Sha(content), report.Sha256);
        Assert.NotNull(report.VerifiedBy);
        Assert.Equal("copy.bin", report.WrittenPath);
        Assert.True(report.DestinationCommitted);
        Assert.NotNull(report.DestinationETag);
        Assert.Equal(content.Length, report.Bytes);
        Assert.Equal(["copy.bin"], await ListAllAsync(library.GetStorage("B")));
    }

    [Fact]
    public async Task A_wrong_digest_or_length_commits_nothing_and_leaves_no_staging()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        await library.GetStorage("Default").UploadBytesAsync("f.bin", [1, 2, 3]);

        var digest = await library.CopyAsync("Default", "f.bin", "B", "x.bin", new StorageTransferOptions { ExpectedSha256 = new string('0', 64) });
        var length = await library.CopyAsync("Default", "f.bin", "B", "y.bin", new StorageTransferOptions { ExpectedSourceLength = 2 });

        Assert.Equal((StorageTransferOutcome.Failed, StorageErrors.ConflictCode), (digest.Outcome, digest.Error!.Code));
        Assert.Equal((StorageTransferOutcome.Failed, StorageErrors.ConflictCode), (length.Outcome, length.Error!.Code));
        Assert.False(digest.DestinationCommitted);
        Assert.Empty(await ListAllAsync(library.GetStorage("B")));
    }

    [Fact]
    public async Task The_destination_is_replaced_only_while_it_is_the_version_the_caller_saw()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var b = library.GetStorage("B");
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);
        await b.UploadBytesAsync("target.bin", [1]);
        var seen = (await b.GetInfoAsync("target.bin")).Value!;

        // Someone else rewrites the destination after the caller looked at it.
        await Task.Delay(20);
        await b.UploadBytesAsync("target.bin", [2, 2, 2]);
        var stale = await library.CopyAsync("Default", "new.bin", "B", "target.bin",
            new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = seen.ETag } });
        Assert.Equal(StorageErrors.ConflictCode, stale.Error!.Code);
        Assert.Equal([2, 2, 2], (await b.DownloadBytesAsync("target.bin")).Value!);

        var current = (await b.GetInfoAsync("target.bin")).Value!;
        var fresh = await library.CopyAsync("Default", "new.bin", "B", "target.bin",
            new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = current.ETag } });
        Assert.True(fresh.IsSuccess, fresh.Error?.ToString());
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, fresh.ConditionEnforcement);
        Assert.Equal([9, 9], (await b.DownloadBytesAsync("target.bin")).Value!);
    }

    [Fact]
    public async Task Creating_only_when_absent_is_atomic_on_local_storage()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        await library.GetStorage("Default").UploadBytesAsync("f.bin", [1]);

        var report = await library.CopyAsync("Default", "f.bin", "B", "g.bin", new StorageTransferOptions { Overwrite = false, Verify = true });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal(StorageConditionEnforcement.Atomic, report.ConditionEnforcement);
    }

    [Fact]
    public async Task Skips_and_renames_are_reported()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        await library.GetStorage("Default").UploadBytesAsync("f.bin", [1]);
        await library.GetStorage("B").UploadBytesAsync("f.bin", [2]);

        var skipped = await library.CopyAsync("Default", "f.bin", "B", "f.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Skip });
        var renamed = await library.CopyAsync("Default", "f.bin", "B", "f.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Rename });

        Assert.Equal((StorageTransferOutcome.Skipped, StorageSkipReason.DestinationExists), (skipped.Outcome, skipped.SkipReason));
        Assert.Equal("f (1).bin", renamed.WrittenPath);
    }

    [Fact]
    public async Task A_changed_source_etag_fails_without_committing()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1, ETag = "\"current\"" })),
            downloadWithOptions: (_, _, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1]))));
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);

        var report = await library.CopyAsync("Src", "f.bin", "B", "f.bin", new StorageTransferOptions { ExpectedSourceETag = "\"planned\"" });

        Assert.Equal(StorageErrors.ConflictCode, report.Error!.Code);
        Assert.Empty(await ListAllAsync(library.GetStorage("B")));
    }

    [Fact]
    public async Task An_interrupted_copy_resumes_from_its_staged_bytes()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var attempts = 0;
        var offsets = new List<long>();
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = content.Length, ETag = "\"v1\"" })),
            downloadWithOptions: (_, options, _) =>
            {
                var offset = options?.Offset ?? 0;
                offsets.Add(offset);
                var rest = content[(int)offset..];
                Stream stream = Interlocked.Increment(ref attempts) == 1 ? new FailingStream(rest, failAfter: 120_000) : new MemoryStream(rest);
                return Task.FromResult(Result<Stream>.Success(stream));
            });
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume, Verify = true };

        var first = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options);
        Assert.Equal(StorageTransferOutcome.Failed, first.Outcome);
        Assert.NotNull(first.ResumeToken);
        Assert.True(first.ResumeToken!.BytesStaged > 0);
        Assert.False((await library.GetStorage("B").ExistsAsync("big.bin")).Value); // never half-written

        var second = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options with { ResumeToken = first.ResumeToken });

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(first.ResumeToken.BytesStaged, second.BytesResumed);
        // Verify reads the staged part back from the source to check it; only its upload is saved.
        Assert.Equal(0, offsets[^1]);
        Assert.Equal(Sha(content), second.Sha256);
        Assert.Equal(content, (await library.GetStorage("B").DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(["big.bin"], await ListAllAsync(library.GetStorage("B")));
    }

    [Fact]
    public async Task A_move_keeps_a_source_that_changed_after_it_was_copied()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var calls = 0;
        var deleted = false;
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path, Name = path, ItemType = StorageItemType.File, Size = 1,
                ETag = Interlocked.Increment(ref calls) <= 1 ? "\"v1\"" : "\"v2\""
            })),
            downloadWithOptions: (_, _, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1]))),
            delete: (_, _) => { deleted = true; return Task.FromResult(Result.Success()); });
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);

        var report = await library.MoveAsync("Src", "f.bin", "B", "f.bin");

        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, report.Outcome);
        Assert.True(report.DestinationCommitted);
        Assert.False(report.SourceDeleted);
        Assert.False(deleted);
    }

    [Fact]
    public async Task Directory_progress_carries_totals_after_a_prescan()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var a = library.GetStorage("Default");
        await a.UploadBytesAsync("dir/1.bin", new byte[10]);
        await a.UploadBytesAsync("dir/2.bin", new byte[20]);
        await a.UploadBytesAsync("dir/sub/3.bin", new byte[30]);
        var reports = new List<StorageTransferProgress>();

        var report = await library.CopyAsync("Default", "dir", "B", "dir",
            new StorageTransferOptions { PreScan = true, Progress = new Collect(reports) });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        var last = reports[^1];
        Assert.True(last.IsCompleted);
        Assert.Equal((60L, 60L, 3L, 3L), (last.BytesTransferred, last.TotalBytes!.Value, last.FilesCompleted!.Value, last.FilesTotal!.Value));
        Assert.All(reports, progress => Assert.Equal(60, progress.TotalBytes));
    }

    [Fact]
    public async Task A_server_side_copy_reports_its_start_and_end()
    {
        var (library, directory) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        await library.GetStorage("Default").UploadBytesAsync("f.bin", new byte[42]);
        var reports = new List<StorageTransferProgress>();

        var report = await library.CopyAsync("Default", "f.bin", "Default", "g.bin", new StorageTransferOptions { Progress = new Collect(reports) });

        Assert.True(report.IsSuccess, report.Error?.ToString());
        Assert.Equal((0L, false), (reports[0].BytesTransferred, reports[0].IsCompleted));
        Assert.Equal((42L, true), (reports[^1].BytesTransferred, reports[^1].IsCompleted));
    }

    private sealed class Collect(List<StorageTransferProgress> into) : IProgress<StorageTransferProgress>
    {
        public void Report(StorageTransferProgress value) { lock (into) into.Add(value); }
    }
}

public sealed class GuaranteedUploadTests
{
    [Fact]
    public async Task Verified_uploads_check_digest_and_length_before_committing()
    {
        using var directory = new TestDirectory();
        var storage = new CL.Storage.Providers.Local.LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        var content = "hello"u8.ToArray();

        var good = await storage.UploadBytesAsync("ok.txt", content, new StorageUploadOptions { Verify = true, ExpectedLength = 5 });
        var badDigest = await storage.UploadBytesAsync("bad.txt", content, new StorageUploadOptions { ExpectedSha256 = new string('a', 64) });
        var tooShort = await storage.UploadBytesAsync("short.txt", content, new StorageUploadOptions { ExpectedLength = 6 });

        Assert.True(good.IsSuccess, good.Error?.ToString());
        Assert.Equal(StorageErrors.ConflictCode, badDigest.Error!.Code);
        Assert.Equal(StorageErrors.ConflictCode, tooShort.Error!.Code);
        var names = (await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Path);
        Assert.Equal(["ok.txt"], names);
    }

    [Fact]
    public async Task A_resumed_upload_appends_only_the_missing_tail_and_never_half_writes_the_destination()
    {
        using var directory = new TestDirectory();
        var storage = new CL.Storage.Providers.Local.LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        var content = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 13)).ToArray();
        var modified = DateTimeOffset.UtcNow.AddMinutes(-5);
        var options = new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceLastModified = modified };

        using (var failing = new FailingStream(content, failAfter: 70_000, seekable: true))
        {
            var first = await storage.UploadAsync("big.bin", failing, options);
            Assert.True(first.IsFailure);
        }
        Assert.False((await storage.ExistsAsync("big.bin")).Value);
        var staged = (await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items.Single();
        Assert.StartsWith(".cl-storage-part-", staged.Name);

        var reads = new CountingStream(content);
        var second = await storage.UploadAsync("big.bin", reads, options);

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(content.Length - staged.Size!.Value, reads.BytesRead);
        Assert.Equal(content, (await storage.DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(["big.bin"], (await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Path));
    }
}

public sealed class StreamedWriteTests
{
    private static CL.Storage.Providers.Local.LocalStorageBackend Local(TestDirectory directory) =>
        new("local", new LocalConnectionConfig { RootPath = directory.Path });

    [Fact]
    public async Task Written_content_appears_only_on_commit()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory);
        await using var writer = (await storage.OpenWriteAsync("out.txt", new StorageUploadOptions { Verify = true })).Value!;

        await writer.WriteAsync("part one, "u8.ToArray());
        await writer.WriteAsync("part two"u8.ToArray());
        Assert.False((await storage.ExistsAsync("out.txt")).Value);
        var committed = await writer.CommitAsync();

        Assert.True(committed.IsSuccess, committed.Error?.ToString());
        Assert.Equal("part one, part two", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("out.txt")).Value!));
    }

    [Fact]
    public async Task Aborting_or_a_length_mismatch_leaves_nothing_behind()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory);
        await using (var aborted = (await storage.OpenWriteAsync("a.txt")).Value!)
        {
            await aborted.WriteAsync(new byte[10]);
            await aborted.AbortAsync();
        }
        await using var wrongLength = (await storage.OpenWriteAsync("b.txt", new StorageUploadOptions { ExpectedLength = 5 })).Value!;
        await wrongLength.WriteAsync(new byte[3]);
        var result = await wrongLength.CommitAsync();

        Assert.Equal(StorageErrors.ConflictCode, result.Error!.Code);
        Assert.Empty((await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items);
    }

    [Fact]
    public async Task Opening_a_write_over_an_existing_file_without_overwrite_fails_up_front()
    {
        using var directory = new TestDirectory();
        var storage = Local(directory);
        await storage.UploadBytesAsync("x.txt", [1]);

        var opened = await storage.OpenWriteAsync("x.txt", new StorageUploadOptions { Overwrite = false });

        Assert.Equal(StorageErrors.ConflictCode, opened.Error!.Code);
    }
}

/// <summary>Serves data, then fails partway through as a dropped connection would.</summary>
internal sealed class FailingStream(byte[] data, int failAfter, bool seekable = false) : MemoryStream(data)
{
    public override bool CanSeek => seekable;

    public override int Read(byte[] buffer, int offset, int count) => Guard(base.Read(buffer, offset, Math.Min(count, 8192)));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Guard(await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken));

    private int Guard(int read)
    {
        if (Position > failAfter) throw new IOException("The connection was reset.");
        return read;
    }
}

/// <summary>A seekable stream that counts how many bytes were actually read.</summary>
internal sealed class CountingStream(byte[] data) : MemoryStream(data)
{
    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = base.Read(buffer, offset, count);
        BytesRead += read;
        return read;
    }

    // MemoryStream's async reads call these, so counting here counts every byte once.
    public override int Read(Span<byte> buffer)
    {
        var read = base.Read(buffer);
        BytesRead += read;
        return read;
    }
}
