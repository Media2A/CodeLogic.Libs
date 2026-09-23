using System.Diagnostics.CodeAnalysis;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Transfer defects found in review: stale restores, pinned versions, and resume identity.</summary>
public sealed class ReviewTransferTests
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
    public async Task A_condition_refused_at_promote_leaves_a_concurrent_write_in_place()
    {
        var (library, directory, b) = await TwoConnectionsAsync();
        using var _ = library; using var __ = directory;
        var local = new LocalStorageBackend("H", new LocalConnectionConfig { RootPath = b });
        await local.UploadBytesAsync("target.bin", [1]);
        var seen = (await local.GetInfoAsync("target.bin")).Value!;
        // Someone else writes the destination right after the transfer backed it up.
        var hooked = new HookedBackend(local, async (_, destination) =>
        {
            if (destination.Contains(".cl-storage-transfer-backup-", StringComparison.Ordinal))
                await local.UploadBytesAsync("target.bin", [7, 7, 7]);
        });
        Assert.True(library.RegisterBackend("H", hooked).IsSuccess);
        await library.GetStorage("Default").UploadBytesAsync("new.bin", [9, 9]);

        var report = await library.CopyAsync("Default", "new.bin", "H", "target.bin",
            new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = seen.ETag } });

        Assert.Equal(StorageErrors.ConflictCode, report.Error?.Code);
        Assert.False(report.DestinationCommitted);
        Assert.Equal([7, 7, 7], (await local.DownloadBytesAsync("target.bin")).Value!);
        Assert.Equal(["target.bin"], await ListAllAsync(local));
    }

    [Fact]
    public async Task A_pinned_source_version_is_checked_and_moved_by_its_own_identity()
    {
        var (library, directory, _) = await TwoConnectionsAsync();
        using var __ = library; using var ___ = directory;
        var deletes = 0;
        var versions = new[]
        {
            new StorageVersion { Path = "f.bin", VersionId = "v1", ETag = "\"e1\"", Size = 3, LastModified = DateTimeOffset.UnixEpoch },
            new StorageVersion { Path = "f.bin", VersionId = "v2", ETag = "\"e2\"", Size = 5, LastModified = DateTimeOffset.UnixEpoch.AddDays(1), IsLatest = true }
        };
        var source = new FakeStorageBackend(
            "Src",
            capabilities: new StorageCapabilities(new StorageCapabilities(true, true, true, true, true, true).Features | StorageFeature.Versioning | StorageFeature.ConditionalDelete),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path, Name = path, ItemType = StorageItemType.File, Size = 5, ETag = "\"e2\"", VersionId = "v2"
            })),
            listVersions: (_, _, _) => Task.FromResult(Result<StorageVersionPage>.Success(new StorageVersionPage(versions, null))),
            downloadWithOptions: (_, options, _) => Task.FromResult(Result<Stream>.Success(
                options?.VersionId == "v1" ? new MemoryStream([1, 2, 3]) : new MemoryStream([5, 5, 5, 5, 5]))),
            delete: (_, _) => { Interlocked.Increment(ref deletes); return Task.FromResult(Result.Failure(StorageErrors.Conflict("not the expected version"))); });
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);

        var copied = await library.CopyAsync("Src", "f.bin", "B", "old.bin",
            new StorageTransferOptions { SourceVersionId = "v1", ExpectedSourceLength = 3, ExpectedSourceETag = "\"e1\"" });
        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.Equal([1, 2, 3], (await library.GetStorage("B").DownloadBytesAsync("old.bin")).Value!);

        // An older version is copied, but the current object is a different version, so it is not deleted.
        var moved = await library.MoveAsync("Src", "f.bin", "B", "moved.bin", new StorageTransferOptions { SourceVersionId = "v1" });
        Assert.Equal(StorageTransferOutcome.NeedsReconciliation, moved.Outcome);
        Assert.False(moved.SourceDeleted);

        var missing = await library.CopyAsync("Src", "f.bin", "B", "x.bin", new StorageTransferOptions { SourceVersionId = "v9" });
        Assert.Equal(StorageErrors.NotFoundCode, missing.Error?.Code);
    }

    [Fact]
    public async Task Resuming_an_upload_needs_a_source_identity()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });

        var refused = await storage.UploadAsync("f.bin", new MemoryStream([1, 2, 3]), new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume });

        Assert.Equal(StorageErrors.InvalidContentCode, refused.Error?.Code);
    }

    [Fact]
    public async Task A_different_source_never_continues_another_sources_staged_prefix()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        byte[] content = [.. "BBBBBBBB"u8];
        // Bytes staged for source "a" of the same length.
        var stagedForA = StagedWriter.ResumableStagingPath("f.bin", StagedWriter.SourceKey("a", 8, null, null, null));
        await storage.UploadBytesAsync(stagedForA, [.. "AAAA"u8]);

        var uploaded = await storage.UploadAsync("f.bin", new MemoryStream(content),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "b" });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.Equal(content, (await storage.DownloadBytesAsync("f.bin")).Value!);
    }

    [Fact]
    public async Task A_verified_resume_checks_the_staged_prefix_against_the_source()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        byte[] content = [.. "AAAAAAAA"u8];
        // The staged prefix claims to be this source but holds other bytes.
        var staged = StagedWriter.ResumableStagingPath("f.bin", StagedWriter.SourceKey("a", 8, null, null, null));
        await storage.UploadBytesAsync(staged, [.. "XXXX"u8]);

        var uploaded = await storage.UploadAsync("f.bin", new MemoryStream(content),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "a", Verify = true });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.Equal(content, (await storage.DownloadBytesAsync("f.bin")).Value!);
    }

    [Fact]
    public async Task A_complete_looking_destination_of_the_same_size_but_other_content_is_replaced()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        await storage.UploadBytesAsync("f.bin", [.. "XXXXXXXX"u8]);
        byte[] content = [.. "AAAAAAAA"u8];

        var uploaded = await storage.UploadAsync("f.bin", new MemoryStream(content),
            new StorageUploadOptions { ConflictPolicy = StorageConflictPolicy.Resume, SourceIdentity = "a" });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.Equal(content, (await storage.DownloadBytesAsync("f.bin")).Value!);
    }

    [Fact]
    public async Task A_tampered_resume_token_is_not_followed()
    {
        var (library, directory, _) = await TwoConnectionsAsync();
        using var __ = library; using var ___ = directory;
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var attempts = 0;
        var source = new FakeStorageBackend(
            "Src",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = content.Length, ETag = "\"v1\"" })),
            downloadWithOptions: (_, options, _) =>
            {
                var rest = content[(int)(options?.Offset ?? 0)..];
                Stream stream = Interlocked.Increment(ref attempts) == 1 ? new FailingStream(rest, failAfter: 120_000) : new MemoryStream(rest);
                return Task.FromResult(Result<Stream>.Success(stream));
            });
        Assert.True(library.RegisterBackend("Src", source).IsSuccess);
        var options = new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume };
        var first = await library.CopyAsync("Src", "big.bin", "B", "big.bin", options);
        Assert.NotNull(first.ResumeToken);
        var foreign = ".cl-storage-part-00000000000000000000000000000000.tmp";
        await library.GetStorage("B").UploadBytesAsync(foreign, [.. "junk"u8], new StorageUploadOptions { });

        var second = await library.CopyAsync("Src", "big.bin", "B", "big.bin",
            options with { ResumeToken = first.ResumeToken! with { StagingPath = foreign } });

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(content, (await library.GetStorage("B").DownloadBytesAsync("big.bin")).Value!);
        Assert.Equal(["big.bin"], await ListAllAsync(library.GetStorage("B")));
    }

    [Fact]
    public async Task A_retried_move_whose_destination_already_holds_the_file_deletes_its_source()
    {
        var (library, directory, _) = await TwoConnectionsAsync();
        using var __ = library; using var ___ = directory;
        await library.GetStorage("Default").UploadBytesAsync("f.bin", [1, 2, 3]);
        // A crash after the commit, before the source was deleted.
        await library.GetStorage("B").UploadBytesAsync("f.bin", [1, 2, 3]);

        var moved = await library.MoveAsync("Default", "f.bin", "B", "f.bin", new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume });

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.True(moved.SourceDeleted);
        Assert.False((await library.GetStorage("Default").ExistsAsync("f.bin")).Value);
        Assert.Equal([1, 2, 3], (await library.GetStorage("B").DownloadBytesAsync("f.bin")).Value!);
    }
}

/// <summary>Passes every call to an inner backend and runs a hook after each copy.</summary>
internal sealed class HookedBackend(IStorageBackend inner, Func<string, string, Task> afterCopy) : IStorageBackend
{
    public string ConnectionId => inner.ConnectionId;
    public StorageProvider Provider => inner.Provider;
    public string Root => inner.Root;
    public StorageCapabilities Capabilities => inner.Capabilities;
    public Task<Result> CheckHealthAsync(CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cancellationToken);
    public bool TryGetNativeClient<TClient>([NotNullWhen(true)] out TClient? client) where TClient : class => inner.TryGetNativeClient(out client);
    public Task<Result<NativeConnectionLease<TClient>>> OpenNativeConnectionAsync<TClient>(CancellationToken cancellationToken = default) where TClient : class =>
        inner.OpenNativeConnectionAsync<TClient>(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    public Task<Result<StorageItem>> GetInfoAsync(string path, CancellationToken cancellationToken = default) => inner.GetInfoAsync(path, cancellationToken);
    public Task<Result<bool>> ExistsAsync(string path, CancellationToken cancellationToken = default) => inner.ExistsAsync(path, cancellationToken);
    public Task<Result<StoragePage>> ListAsync(string path, StorageListOptions? options = null, CancellationToken cancellationToken = default) => inner.ListAsync(path, options, cancellationToken);
    public Task<Result> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);
    public Task<Result<StorageItem>> UploadAsync(string path, Stream source, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) => inner.UploadAsync(path, source, options, cancellationToken);
    public Task<Result<StorageItem>> UploadBytesAsync(string path, byte[] content, StorageUploadOptions? options = null, CancellationToken cancellationToken = default) => inner.UploadBytesAsync(path, content, options, cancellationToken);
    public Task<Result<Stream>> DownloadAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) => inner.DownloadAsync(path, options, cancellationToken);
    public Task<Result<byte[]>> DownloadBytesAsync(string path, StorageDownloadOptions? options = null, CancellationToken cancellationToken = default) => inner.DownloadBytesAsync(path, options, cancellationToken);
    public Task<Result> DeleteAsync(string path, StorageDeleteOptions? options = null, CancellationToken cancellationToken = default) => inner.DeleteAsync(path, options, cancellationToken);
    public async Task<Result> CopyAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default)
    {
        var copied = await inner.CopyAsync(sourcePath, destinationPath, options, cancellationToken);
        await afterCopy(sourcePath, destinationPath);
        return copied;
    }
    public Task<Result> MoveAsync(string sourcePath, string destinationPath, StorageTransferOptions? options = null, CancellationToken cancellationToken = default) => inner.MoveAsync(sourcePath, destinationPath, options, cancellationToken);
}
