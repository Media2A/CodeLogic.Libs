using System.Security.Cryptography;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Cancellation leaves nothing behind (except bytes to resume from) and is reported; digests are returned.</summary>
public sealed class ReviewCancellationTests
{
    private static async Task<(global::CL.Storage.StorageLibrary Library, TestDirectory Directory, string B)> TwoConnectionsAsync()
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
        return (library, directory, b);
    }

    private static async Task<List<string>> ListAllAsync(IStorageService storage)
    {
        var page = await storage.ListAsync("", new StorageListOptions { Recursive = true, IncludeInternal = true });
        return [.. page.Value!.Items.Select(item => item.Path).Order()];
    }

    [Fact]
    public async Task A_cancelled_resumable_copy_reports_a_token_that_continues_it()
    {
        var (library, directory, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = content.Length, ETag = "\"v1\"" })),
            downloadWithOptions: (_, options, _) =>
            {
                var rest = content[(int)(options?.Offset ?? 0)..];
                // The first attempt is cancelled by the caller part-way through.
                Stream stream = Interlocked.Increment(ref attempts) == 1 ? new CancellingStream(rest, 150_000, cancellation) : new MemoryStream(rest);
                return Task.FromResult(Result<Stream>.Success(stream));
            });
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };

        var first = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options, cancellation.Token);

        Assert.Equal(StorageTransferOutcome.Cancelled, first.Outcome);
        Assert.NotNull(first.ResumeToken);
        Assert.True(first.ResumeToken!.BytesStaged > 0);
        Assert.False((await library.GetStorage("B").ExistsAsync("big.bin")).Value);

        var second = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options with { ResumeToken = first.ResumeToken });

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.True(second.BytesResumed > 0);
        Assert.Equal(content, (await library.GetStorage("B").DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(["big.bin"], await ListAllAsync(library.GetStorage("B")));
    }

    [Fact]
    public async Task Cancelling_while_the_destination_is_backed_up_leaves_it_and_nothing_else()
    {
        var (library, directory, b) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var local = new LocalStorageBackend("H", new LocalConnectionConfig { RootPath = b });
        await local.UploadBytesAsync("target.bin", [1, 1]);
        using var cancellation = new CancellationTokenSource();
        var hooked = new HookedBackend(local, (_, destination) =>
        {
            if (destination.Contains(".cl-storage-transfer-backup-", StringComparison.Ordinal)) cancellation.Cancel();
            return Task.CompletedTask;
        });
        Assert.True(library.RegisterBackend("H", hooked).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin", new StorageTransferOptions { Overwrite = true }, cancellation.Token);

        Assert.Equal(StorageTransferOutcome.Cancelled, report.Outcome);
        Assert.Equal([1, 1], (await local.DownloadBytesAsync("target.bin")).Value!);
        Assert.Equal(["target.bin"], await ListAllAsync(local));
    }

    [Fact]
    public async Task A_cancelled_commit_of_a_streamed_write_leaves_nothing()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        var writer = (await storage.OpenWriteAsync("f.bin")).Value!;
        await writer.WriteAsync(new byte[100_000]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CommitAsync(new CancellationToken(canceled: true)));

        Assert.Empty(await ListAllAsync(storage));
    }

    [Fact]
    public async Task Disposing_a_streamed_write_synchronously_does_not_wait_and_still_cleans_up()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        var writer = (await storage.OpenWriteAsync("f.bin")).Value!;
        await writer.WriteAsync(new byte[10_000]);

        writer.Dispose();

        for (var i = 0; i < 100 && (await ListAllAsync(storage)).Count > 0; i++) await Task.Delay(20);
        Assert.Empty(await ListAllAsync(storage));
    }

    [Fact]
    public async Task Verified_uploads_and_streamed_writes_return_their_digest()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        byte[] content = [.. "digest me"u8];
        var expected = Convert.ToHexStringLower(SHA256.HashData(content));

        var uploaded = await storage.UploadAsync("a.bin", new MemoryStream(content), new StorageUploadOptions { Verify = true });
        var writer = (await storage.OpenWriteAsync("b.bin", new StorageUploadOptions { Verify = true })).Value!;
        await writer.WriteAsync(content);
        var committed = await writer.CommitAsync();

        Assert.Equal(expected, uploaded.Value!.Sha256);
        Assert.Equal(expected, committed.Value!.Sha256);
        Assert.Equal(content.Length, committed.Value.Size);
    }

    /// <summary>Cancels a token once a given number of bytes has been read, then reports the cancellation.</summary>
    private sealed class CancellingStream(byte[] data, int cancelAfter, CancellationTokenSource cancellation) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= cancelAfter)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
        }
    }
}
