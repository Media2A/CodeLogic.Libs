using System.Collections.Concurrent;
using CL.Storage.Registry;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StorageLibraryTransferTests
{
    [Fact]
    public async Task Cross_connection_copy_relays_through_a_unique_staging_object_before_commit()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var content = Enumerable.Range(0, 64_000).Select(index => (byte)(index % 251)).ToArray();
        string? stagedPath = null;
        byte[]? stagedContent = null;
        byte[]? committedContent = null;
        var sourceDeleted = false;
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, content.Length))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new NonSeekableReadStream(content))),
            delete: (_, _) =>
            {
                sourceDeleted = true;
                return Task.FromResult(Result.Success());
            });
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                Assert.False(stream.CanSeek);
                stagedPath = path;
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                stagedContent = buffer.ToArray();
                return Result<StorageItem>.Success(File(path, stagedContent.Length));
            },
            move: (sourcePath, destinationPath, _) =>
            {
                Assert.Equal(stagedPath, sourcePath);
                Assert.Equal("target/copy.bin", destinationPath);
                committedContent = stagedContent;
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.CopyAsync(
            "Source",
            @"folder\source.bin",
            "Destination",
            "/target//copy.bin");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(stagedPath);
        Assert.NotEqual("target/copy.bin", stagedPath);
        Assert.StartsWith("target/.", stagedPath, StringComparison.Ordinal);
        Assert.Equal(content, committedContent);
        Assert.False(sourceDeleted);
    }

    [Fact]
    public async Task Cross_connection_move_deletes_source_only_after_destination_commit()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var committed = false;
        var deleted = false;
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 3))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1, 2, 3], writable: false))),
            delete: (_, _) =>
            {
                Assert.True(committed);
                deleted = true;
                return Task.FromResult(Result.Success());
            });
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                await stream.CopyToAsync(Stream.Null, cancellationToken);
                Assert.False(deleted);
                return Result<StorageItem>.Success(File(path, 3));
            },
            move: (_, destinationPath, _) =>
            {
                Assert.Equal("archive/item.bin", destinationPath);
                Assert.False(deleted);
                committed = true;
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.MoveAsync(
            "Source", "item.bin", "Destination", "archive/item.bin");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(committed);
        Assert.True(deleted);
    }

    [Fact]
    public async Task Failed_cross_connection_upload_cleans_only_its_unique_staging_object()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var deletedPaths = new ConcurrentQueue<string>();
        var sourceDeleted = false;
        string? stagingPath = null;
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 10))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream(new byte[10], writable: false))),
            delete: (_, _) =>
            {
                sourceDeleted = true;
                return Task.FromResult(Result.Success());
            });
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (path, _) => Task.FromResult(Result<bool>.Success(
                path is "preexisting" or "preexisting/final.bin")),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(
                path == "preexisting" ? Directory(path) : File(path, 99))),
            uploadStream: async (path, stream, uploadOptions, cancellationToken) =>
            {
                stagingPath = path;
                var oneByte = new byte[1];
                await stream.ReadAtLeastAsync(oneByte, minimumBytes: 1, throwOnEndOfStream: true, cancellationToken);
                return Result<StorageItem>.Failure(StorageErrors.Unavailable("simulated upload failure"));
            },
            move: (_, _, _) => throw new Xunit.Sdk.XunitException("A failed upload must not be committed."),
            delete: (path, _) =>
            {
                deletedPaths.Enqueue(path);
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.MoveAsync(
            "Source", "item.bin", "Destination", "preexisting/final.bin");

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.UnavailableCode, result.Error!.Code);
        Assert.NotNull(stagingPath);
        Assert.Equal([stagingPath], deletedPaths);
        Assert.DoesNotContain("preexisting/final.bin", deletedPaths);
        Assert.False(sourceDeleted);
    }

    [Fact]
    public async Task Failed_staging_cleanup_is_reported_as_a_sanitized_partial_failure()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 1))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream([1], writable: false))));
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: (_, _, _, _) => Task.FromResult(Result<StorageItem>.Failure(
                StorageErrors.Unavailable("provider upload secret"))),
            delete: (_, _) => Task.FromResult(Result.Failure(
                StorageErrors.Unauthorized("provider cleanup secret"))));
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.CopyAsync(
            "Source", "item.bin", "Destination", "copy.bin");

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.PartialFailureCode, result.Error!.Code);
        Assert.Contains(StorageErrors.UnavailableCode, result.Error.Details, StringComparison.Ordinal);
        Assert.Contains(StorageErrors.UnauthorizedCode, result.Error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Error.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_directory_copy_restores_preexisting_files_and_removes_internal_backups()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var sourceContents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["tree/one.bin"] = [1],
            ["tree/two.bin"] = [2]
        };
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(
                path == "tree" ? Directory(path) : File(path, sourceContents[path].Length))),
            list: (_, _, _) => Task.FromResult(Result<StoragePage>.Success(new StoragePage([
                File("tree/one.bin", 1),
                File("tree/two.bin", 1)
            ]))),
            download: (path, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream(sourceContents[path], writable: false))));
        var files = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["target/one.bin"] = [9]
        };
        var uploadCount = 0;
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (path, _) => Task.FromResult(Result<bool>.Success(
                path == "target" || files.ContainsKey(path))),
            getInfo: (path, _) => Task.FromResult(
                path == "target"
                    ? Result<StorageItem>.Success(Directory(path))
                    : files.TryGetValue(path, out var content)
                        ? Result<StorageItem>.Success(File(path, content.Length))
                        : Result<StorageItem>.Failure(StorageErrors.NotFound("missing"))),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref uploadCount) == 2)
                    return Result<StorageItem>.Failure(StorageErrors.Unavailable("second upload failed"));
                using var content = new MemoryStream();
                await stream.CopyToAsync(content, cancellationToken);
                files[path] = content.ToArray();
                return Result<StorageItem>.Success(File(path, content.Length));
            },
            copy: (sourcePath, destinationPath, _) =>
            {
                if (!files.TryGetValue(sourcePath, out var content))
                    return Task.FromResult(Result.Failure(StorageErrors.NotFound("copy source missing")));
                files[destinationPath] = content.ToArray();
                return Task.FromResult(Result.Success());
            },
            move: (sourcePath, destinationPath, _) =>
            {
                if (!files.TryRemove(sourcePath, out var content))
                    return Task.FromResult(Result.Failure(StorageErrors.NotFound("move source missing")));
                files[destinationPath] = content;
                return Task.FromResult(Result.Success());
            },
            delete: (path, _) =>
            {
                files.TryRemove(path, out var _removed);
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.CopyAsync("Source", "tree", "Destination", "target");

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.UnavailableCode, result.Error!.Code);
        Assert.Equal([9], files["target/one.bin"]);
        Assert.DoesNotContain(files.Keys, path => path.Contains(".cl-storage-transfer-", StringComparison.Ordinal));
        Assert.DoesNotContain("target/two.bin", files.Keys);
    }

    [Fact]
    public async Task Cross_provider_metadata_is_best_effort_or_required_by_explicit_policy()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem
            {
                Path = path,
                Name = "source.bin",
                ItemType = StorageItemType.File,
                Size = 1,
                Metadata = new Dictionary<string, string> { ["color"] = "blue" }
            })),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1], writable: false))));
        var uploads = 0;
        var destination = new FakeStorageBackend(
            "Destination",
            capabilities: new StorageCapabilities(StorageFeature.PhysicalDirectories),
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, options, cancellationToken) =>
            {
                Interlocked.Increment(ref uploads);
                Assert.Empty(options!.Metadata);
                await stream.CopyToAsync(Stream.Null, cancellationToken);
                return Result<StorageItem>.Success(File(path, 1));
            },
            move: (_, _, _) => Task.FromResult(Result.Success()));
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var bestEffort = await library.CopyAsync("Source", "source.bin", "Destination", "copy.bin");
        var required = await library.CopyAsync(
            "Source",
            "source.bin",
            "Destination",
            "required.bin",
            new StorageTransferOptions { MetadataPreservation = StorageMetadataPreservation.Require });

        Assert.True(bestEffort.IsSuccess, bestEffort.Error?.Message);
        Assert.True(required.IsFailure);
        Assert.Equal(StorageErrors.UnsupportedCode, required.Error!.Code);
        Assert.Equal(1, uploads);
    }

    [Fact]
    public async Task Cross_connection_directory_copy_preserves_relative_paths_empty_directories_and_paging()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["tree/a.bin"] = [1],
            ["tree/nested/b.bin"] = [2, 3]
        };
        var createdDirectories = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var staged = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var committed = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(Directory(path))),
            list: (path, options, _) => Task.FromResult(Result<StoragePage>.Success(
                options?.ContinuationToken is null
                    ? new StoragePage([
                        Directory("tree/empty"),
                        File("tree/a.bin", 1)
                    ], "page-2")
                    : new StoragePage([
                        Directory("tree/nested"),
                        File("tree/nested/b.bin", 2)
                    ]))),
            download: (path, _) => Task.FromResult(Result<Stream>.Success(
                new MemoryStream(contents[path], writable: false))));
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (path, _) => Task.FromResult(Result<bool>.Success(
                createdDirectories.ContainsKey(path) || committed.ContainsKey(path))),
            getInfo: (path, _) => Task.FromResult(
                createdDirectories.ContainsKey(path)
                    ? Result<StorageItem>.Success(Directory(path))
                    : committed.TryGetValue(path, out var bytes)
                        ? Result<StorageItem>.Success(File(path, bytes.Length))
                        : Result<StorageItem>.Failure(StorageErrors.NotFound("missing"))),
            createDirectory: (path, _) =>
            {
                createdDirectories[path] = 0;
                return Task.FromResult(Result.Success());
            },
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                staged[path] = buffer.ToArray();
                return Result<StorageItem>.Success(File(path, buffer.Length));
            },
            move: (sourcePath, destinationPath, _) =>
            {
                Assert.True(staged.TryRemove(sourcePath, out var bytes));
                committed[destinationPath] = bytes;
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.CopyAsync(
            "Source", "tree", "Destination", "backup");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(createdDirectories.ContainsKey("backup"));
        Assert.True(createdDirectories.ContainsKey("backup/empty"));
        Assert.True(createdDirectories.ContainsKey("backup/nested"));
        Assert.Equal([1], committed["backup/a.bin"]);
        Assert.Equal([2, 3], committed["backup/nested/b.bin"]);
        Assert.Empty(staged);
    }

    [Fact]
    public async Task Cross_connection_relay_keeps_read_ahead_within_the_declared_pipe_bound()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        const long length = 6L * 1_048_576;
        var generated = new GeneratingReadStream(length);
        long consumed = 0;
        long maximumReadAhead = 0;
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, length))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(generated)));
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                var buffer = new byte[16_384];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    consumed += read;
                    maximumReadAhead = Math.Max(maximumReadAhead, generated.BytesRead - consumed);
                    await Task.Delay(1, cancellationToken);
                }
                return Result<StorageItem>.Success(File(path, consumed));
            },
            move: (_, _, _) => Task.FromResult(Result.Success()));
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.CopyAsync(
            "Source", "large.bin", "Destination", "large-copy.bin");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(length, consumed);
        Assert.InRange(
            maximumReadAhead,
            1,
            StorageTransferCoordinator.PauseWriterThreshold + StorageTransferCoordinator.SegmentSize);
    }

    [Fact]
    public async Task Cancellation_stops_both_relay_peers_and_cleans_the_staging_object()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var enteredUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletedPaths = new ConcurrentQueue<string>();
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 16L * 1_048_576))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new GeneratingReadStream(16L * 1_048_576))));
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                var buffer = new byte[1];
                await stream.ReadExactlyAsync(buffer, cancellationToken);
                enteredUpload.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Result<StorageItem>.Success(File(path, 1));
            },
            move: (_, _, _) => throw new Xunit.Sdk.XunitException("A cancelled upload must not be committed."),
            delete: (path, _) =>
            {
                deletedPaths.Enqueue(path);
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
        using var cancellation = new CancellationTokenSource();
        var transfer = library.CopyAsync(
            "Source", "large.bin", "Destination", "cancelled.bin", cancellationToken: cancellation.Token);
        await enteredUpload.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        var deleted = Assert.Single(deletedPaths);
        Assert.StartsWith(".cl-storage-transfer-", deleted, StringComparison.Ordinal);
        Assert.EndsWith(".tmp", deleted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_cross_connection_transfer_holds_both_backend_leases_until_commit()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var enteredUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldSource = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 2L * 1_048_576))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new GeneratingReadStream(2L * 1_048_576))));
        var oldDestination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                enteredUpload.TrySetResult();
                await releaseUpload.Task.WaitAsync(cancellationToken);
                await stream.CopyToAsync(Stream.Null, cancellationToken);
                return Result<StorageItem>.Success(File(path, 2L * 1_048_576));
            },
            move: (_, _, _) => Task.FromResult(Result.Success()));
        Assert.True(library.RegisterBackend("Source", oldSource).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", oldDestination).IsSuccess);
        var transfer = library.CopyAsync(
            "Source", "large.bin", "Destination", "copy.bin");
        await enteredUpload.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var replaceSource = Task.Run(() => library.RegisterBackend("Source", new FakeStorageBackend("Source")));
        var replaceDestination = Task.Run(() => library.RegisterBackend("Destination", new FakeStorageBackend("Destination")));
        await Task.Delay(50);

        Assert.False(replaceSource.IsCompleted);
        Assert.False(replaceDestination.IsCompleted);
        Assert.Equal(0, oldSource.DisposeCount);
        Assert.Equal(0, oldDestination.DisposeCount);
        releaseUpload.TrySetResult();
        Assert.True((await transfer).IsSuccess);
        Assert.True((await replaceSource).IsSuccess);
        Assert.True((await replaceDestination).IsSuccess);
        Assert.Equal(1, oldSource.DisposeCount);
        Assert.Equal(1, oldDestination.DisposeCount);
    }

    [Fact]
    public async Task Same_connection_copy_falls_back_to_the_bounded_relay_when_native_copy_is_unavailable()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        byte[]? staged = null;
        byte[]? committed = null;
        var backend = new FakeStorageBackend(
            "Relay",
            capabilities: new StorageCapabilities(true, false, true, true, true, true),
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 3))),
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([4, 5, 6], writable: false))),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                staged = buffer.ToArray();
                return Result<StorageItem>.Success(File(path, staged.Length));
            },
            move: (_, _, _) =>
            {
                committed = staged;
                return Task.FromResult(Result.Success());
            },
            copy: (_, _, _) => throw new Xunit.Sdk.XunitException("Native copy must not be called."));
        Assert.True(library.RegisterBackend("Relay", backend).IsSuccess);

        var result = await library.CopyAsync(
            "Relay", "source.bin", "Relay", "destination.bin");

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([4, 5, 6], committed);
    }

    [Fact]
    public async Task Cross_connection_move_reports_partial_failure_when_commit_succeeds_but_source_delete_fails()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var committed = false;
        var source = new FakeStorageBackend(
            "Source",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(File(path, 1))),
            download: (_, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([7], writable: false))),
            delete: (_, _) => Task.FromResult(Result.Failure(
                StorageErrors.Unauthorized("provider secret must not be surfaced"))));
        var destination = new FakeStorageBackend(
            "Destination",
            exists: (_, _) => Task.FromResult(Result<bool>.Success(false)),
            uploadStream: async (path, stream, _, cancellationToken) =>
            {
                await stream.CopyToAsync(Stream.Null, cancellationToken);
                return Result<StorageItem>.Success(File(path, 1));
            },
            move: (_, _, _) =>
            {
                committed = true;
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Source", source).IsSuccess);
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);

        var result = await library.MoveAsync(
            "Source", "source.bin", "Destination", "destination.bin");

        Assert.True(committed);
        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.PartialFailureCode, result.Error!.Code);
        Assert.Contains(StorageErrors.UnauthorizedCode, result.Error.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Error.Details, StringComparison.OrdinalIgnoreCase);
    }

    private static StorageItem File(string path, long size) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        ItemType = StorageItemType.File,
        Size = size,
        ContentType = "application/octet-stream"
    };

    private static StorageItem Directory(string path) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        ItemType = StorageItemType.Directory
    };

    private sealed class NonSeekableReadStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }

    private sealed class GeneratingReadStream : Stream
    {
        private readonly long _length;
        private long _remaining;
        private long _bytesRead;

        internal GeneratingReadStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        internal long BytesRead => Interlocked.Read(ref _bytesRead);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - Interlocked.Read(ref _remaining);
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Interlocked.Read(ref _remaining);
            if (remaining == 0)
                return ValueTask.FromResult(0);
            var read = (int)Math.Min(buffer.Length, remaining);
            buffer.Span[..read].Fill(0x5A);
            Interlocked.Add(ref _remaining, -read);
            Interlocked.Add(ref _bytesRead, read);
            return ValueTask.FromResult(read);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
