using CL.Storage.Errors;
using CL.Storage.Configuration;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StorageLibraryConvenienceTests
{
    [Fact]
    public async Task Try_get_storage_returns_stable_proxy_or_false_without_lookup_exception()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);

        Assert.False(library.TryGetStorage("Missing", out var missing));
        Assert.Null(missing);
        Assert.True(library.RegisterBackend("Present", new FakeStorageBackend("Present")).IsSuccess);
        Assert.True(library.TryGetStorage("present", out var first));
        Assert.True(library.TryGetStorage("PRESENT", out var second));
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Async_registration_preserves_cancellation_and_transfers_no_ownership_on_failure()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var backend = new FakeStorageBackend(
            "Cancelled",
            health: async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Result.Success();
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            library.RegisterBackendAsync("Cancelled", backend, cancellationToken: cancellation.Token));

        Assert.False(library.TryGetStorage("Cancelled", out _));
        Assert.Equal(0, backend.DisposeCount);
    }

    [Fact]
    public async Task Per_connection_health_returns_the_provider_neutral_failure()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var backend = new FakeStorageBackend("Health");
        Assert.True(library.RegisterBackend("Health", backend).IsSuccess);
        backend.SetHealth(_ => Task.FromResult(Result.Failure(StorageErrors.Unavailable("offline"))));

        var health = await library.CheckConnectionHealthAsync("Health");

        Assert.True(health.IsFailure);
        Assert.Equal(StorageErrors.UnavailableCode, health.Error!.Code);
    }

    [Fact]
    public async Task Async_disposal_stops_and_disposes_owned_backends_once()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var backend = new FakeStorageBackend("Owned");
        Assert.True(library.RegisterBackend("Owned", backend).IsSuccess);

        await library.DisposeAsync();
        await library.DisposeAsync();

        Assert.Equal(1, backend.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => library.GetConnections());
    }

    [Fact]
    public async Task Local_directory_upload_and_download_are_recursive_bounded_and_report_counts()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var remoteRoot = directory.CreateDirectory("remote");
        var sourceRoot = directory.CreateDirectory("source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "first.bin"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "nested", "second.bin"), [4, 5]);
        var remote = new LocalStorageBackend(
            "Remote",
            new LocalConnectionConfig { RootPath = remoteRoot });
        Assert.True(library.RegisterBackend("Remote", remote).IsSuccess);

        var uploaded = await library.UploadDirectoryAsync(sourceRoot, "Remote", "backup");
        var localDownload = Path.Combine(directory.Path, "downloads", "restored");
        var downloaded = await library.DownloadDirectoryAsync("Remote", "backup", localDownload);

        Assert.True(uploaded.IsSuccess, uploaded.Error?.Message);
        Assert.Equal(2, uploaded.Value!.Files);
        Assert.Equal(2, uploaded.Value.Directories);
        Assert.Equal(5, uploaded.Value.Bytes);
        Assert.True(downloaded.IsSuccess, downloaded.Error?.Message);
        Assert.Equal(uploaded.Value, downloaded.Value);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(localDownload, "first.bin")));
        Assert.Equal([4, 5], await File.ReadAllBytesAsync(Path.Combine(localDownload, "nested", "second.bin")));
    }
}
