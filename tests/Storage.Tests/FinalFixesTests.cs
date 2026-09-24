using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Xunit;
using static Storage.Tests.NeedsReviewTransferTests;

namespace Storage.Tests;

/// <summary>The last fixes before release (fixes.md, F1–F8).</summary>
public sealed class FinalFixesTests
{
    // ---------------------------------------------------------------- F1

    /// <summary>A destination whose read-back of <paramref name="path"/> fails <paramref name="failures"/> times once it exists.</summary>
    private static InterceptBackend ReadBackFails(string root, string path, int failures, Func<Error> error)
    {
        var local = Local(root);
        var left = failures;
        return new InterceptBackend(local)
        {
            GetInfo = async (candidate, token) =>
            {
                var info = await local.GetInfoAsync(candidate, token);
                if (candidate == path && info.IsSuccess && Interlocked.Decrement(ref left) >= 0)
                    return Result<StorageItem>.Failure(error());
                return info;
            }
        };
    }

    [Fact] // needs-review F1
    public async Task A_read_back_failure_after_a_committed_staged_upload_says_the_destination_is_committed()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("d");
        var destination = ReadBackFails(root, "f.bin", int.MaxValue, () => StorageErrors.Unavailable("read-back failed"));

        var result = await StorageTransferPipeline.StagedUploadAsync(
            destination, "f.bin", new MemoryStream(Content(100)), new StorageUploadOptions { Verify = true }, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.True(StorageErrorInfo.DestinationCommitted(result.Error));
        Assert.Equal(Content(100), await File.ReadAllBytesAsync(Path.Combine(root, "f.bin")));
    }

    [Fact] // needs-review F1
    public async Task A_transient_read_back_failure_after_a_committed_streamed_write_is_retried()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("d");
        var destination = ReadBackFails(root, "f.bin", 1, () => StorageErrors.Timeout("read-back timed out"));

        var opened = await destination.OpenWriteAsync("f.bin", new StorageUploadOptions { Verify = true });
        Assert.True(opened.IsSuccess, opened.Error?.Message);
        await using var stream = opened.Value!;
        await stream.WriteAsync(Content(100));
        var committed = await stream.CommitAsync();

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.Equal(100, committed.Value!.Size);
    }

    [Fact] // needs-review F1
    public async Task A_read_back_failure_after_a_committed_transfer_reports_the_destination_committed()
    {
        var (library, directory, a, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        await File.WriteAllBytesAsync(Path.Combine(a, "f.bin"), Content(100));
        var root = directory.CreateDirectory("d");
        Assert.True(library.RegisterBackend("D", ReadBackFails(root, "g.bin", int.MaxValue, () => StorageErrors.Unavailable("read-back failed")).Named("D")).IsSuccess);

        var report = await library.CopyAsync("Default", "f.bin", "D", "g.bin", new StorageTransferOptions { Verify = true });

        Assert.True(report.DestinationCommitted, report.Error?.ToString());
        Assert.True(report.IsSuccess || StorageErrorInfo.DestinationCommitted(report.Error), report.Error?.ToString());
        Assert.Equal(Content(100), await File.ReadAllBytesAsync(Path.Combine(root, "g.bin")));
    }
}
