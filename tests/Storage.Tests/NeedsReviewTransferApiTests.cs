using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Transfer findings of the round-3 review whose tests use API added by the fix.</summary>
public sealed class NeedsReviewTransferApiTests
{
    [Fact] // needs-review B1
    public async Task A_skipped_link_says_why_it_was_skipped()
    {
        var (library, directory, _, _) = await NeedsReviewTransferTests.TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.Link })));
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);

        var report = await library.CopyAsync("Src", "link", "B", "link", new StorageTransferOptions { LinkHandling = StorageLinkHandling.Skip });

        Assert.Equal(StorageTransferOutcome.Skipped, report.Outcome);
        Assert.Equal(StorageSkipReason.Link, report.SkipReason);
    }

    [Fact] // needs-review B17
    public async Task A_relay_on_a_connection_limited_to_one_session_fails_at_once()
    {
        var (library, directory, _, _) = await NeedsReviewTransferTests.TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var single = NeedsReviewTransferTests.Local(directory.CreateDirectory("one"), "One");
        StorageTransferPipeline.SetSessionLimit(single, 1);
        Assert.True(library.RegisterBackend("One", single).IsSuccess);
        await single.UploadBytesAsync("a.bin", [1, 2, 3]);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = await library.CopyAsync("One", "a.bin", "One", "b.bin", new StorageTransferOptions { Verify = true });

        Assert.Equal(StorageTransferOutcome.Failed, report.Outcome);
        Assert.Equal(StorageErrors.UnsupportedCode, report.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(report.Error, "requiredSessions", out var needed) && needed == "2");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.False((await single.ExistsAsync("b.bin")).Value);
    }

    [Fact] // needs-review B24
    public async Task A_write_the_destination_stopped_carries_the_storage_error()
    {
        using var directory = new TestDirectory();
        var local = NeedsReviewTransferTests.Local(directory.Path);
        var busy = new InterceptBackend(local)
        {
            Upload = (_, _, _, _) => Task.FromResult(Result<StorageItem>.Failure(StorageErrors.ServerBusy("Slow down.", "retryAfterMs=100")))
        };
        var writer = (await busy.OpenWriteAsync("w.bin")).Value!;

        StorageWriteException? stopped = null;
        for (var i = 0; i < 1000 && stopped is null; i++)
        {
            try { await writer.WriteAsync(new byte[65_536]); }
            catch (StorageWriteException error) { stopped = error; }
        }

        Assert.NotNull(stopped);
        Assert.IsAssignableFrom<IOException>(stopped);
        Assert.True(StorageErrors.ServerBusyCode == stopped!.Error?.Code, stopped.ToString());
        await writer.DisposeAsync();
    }
}
