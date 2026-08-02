using System.Text;
using System.Security.Cryptography;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StorageAdvancedTests
{
    [Fact]
    public async Task File_text_and_json_helpers_round_trip_with_atomic_local_commit()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("storage");
        await using var storage = new LocalStorageBackend(
            "Local",
            new LocalConnectionConfig { RootPath = root });
        var uploadFile = Path.Combine(directory.Path, "source.txt");
        await File.WriteAllTextAsync(uploadFile, "from-file", Encoding.UTF8);

        var uploaded = await storage.UploadFileAsync("files/source.txt", uploadFile);
        var text = await storage.ReadTextAsync("files/source.txt");
        var writtenText = await storage.WriteTextAsync("files/other.txt", "other");
        var writtenJson = await storage.WriteJsonAsync("data/value.json", new TestPayload("green", 42));
        var json = await storage.ReadJsonAsync<TestPayload>("data/value.json");
        var localDownload = Path.Combine(directory.Path, "downloads", "value.json");
        var downloaded = await storage.DownloadToFileAsync("data/value.json", localDownload);

        Assert.True(uploaded.IsSuccess, uploaded.Error?.Message);
        Assert.Equal("from-file", text.Value);
        Assert.True(writtenText.IsSuccess, writtenText.Error?.Message);
        Assert.True(writtenJson.IsSuccess, writtenJson.Error?.Message);
        Assert.Equal(new TestPayload("green", 42), json.Value);
        Assert.True(downloaded.IsSuccess, downloaded.Error?.Message);
        Assert.Contains("\"Color\":\"green\"", await File.ReadAllTextAsync(localDownload), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_to_file_preserves_existing_destination_on_conflict_and_source_failure()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("storage");
        await using var storage = new LocalStorageBackend(
            "Local",
            new LocalConnectionConfig { RootPath = root });
        await storage.UploadBytesAsync("remote.bin", [4, 5, 6]);
        var destination = Path.Combine(directory.Path, "destination.bin");
        await File.WriteAllBytesAsync(destination, [1, 2, 3]);

        var conflict = await storage.DownloadToFileAsync("remote.bin", destination, overwrite: false);
        var missing = await storage.DownloadToFileAsync("missing.bin", destination, overwrite: true);

        Assert.True(conflict.IsFailure);
        Assert.Equal(StorageErrors.ConflictCode, conflict.Error!.Code);
        Assert.True(missing.IsFailure);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".clstorage-download-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Bounded_json_serialization_fails_before_upload()
    {
        var uploads = 0;
        var storage = new FakeStorageBackend(
            "Bounded",
            uploadStream: (_, _, _, _) =>
            {
                Interlocked.Increment(ref uploads);
                return Task.FromResult(Result<StorageItem>.Failure(StorageErrors.ProviderError("unexpected")));
            });

        var result = await storage.WriteJsonAsync("large.json", new { Value = new string('x', 1_000) }, maxSerializedBytes: 32);

        Assert.True(result.IsFailure);
        Assert.Equal(StorageErrors.TooLargeCode, result.Error!.Code);
        Assert.Equal(0, uploads);
    }

    [Fact]
    public async Task Progress_helpers_report_monotonic_bytes_and_preserve_upload_stream_ownership()
    {
        using var directory = new TestDirectory();
        await using var storage = new LocalStorageBackend(
            "Local",
            new LocalConnectionConfig { RootPath = directory.Path });
        using var source = new MemoryStream([1, 2, 3, 4, 5], writable: false);
        var uploadProgress = new List<StorageTransferProgress>();

        var upload = await storage.UploadWithProgressAsync(
            "item.bin",
            source,
            new InlineProgress<StorageTransferProgress>(uploadProgress.Add));
        var downloadProgress = new List<StorageTransferProgress>();
        var download = await storage.DownloadWithProgressAsync(
            "item.bin",
            new InlineProgress<StorageTransferProgress>(downloadProgress.Add));
        await using var downloaded = download.Value!;
        await downloaded.CopyToAsync(Stream.Null);

        Assert.True(upload.IsSuccess, upload.Error?.Message);
        Assert.True(source.CanRead);
        Assert.Equal(5, uploadProgress[^1].BytesTransferred);
        Assert.True(uploadProgress[^1].IsCompleted);
        Assert.True(download.IsSuccess, download.Error?.Message);
        Assert.Equal(5, downloadProgress[^1].BytesTransferred);
        Assert.True(downloadProgress[^1].IsCompleted);
        Assert.All(uploadProgress.Zip(uploadProgress.Skip(1)), pair =>
            Assert.True(pair.First.BytesTransferred <= pair.Second.BytesTransferred));
    }

    [Fact]
    public async Task Checksum_helpers_stream_and_compare_expected_bytes_without_buffering()
    {
        using var directory = new TestDirectory();
        await using var storage = new LocalStorageBackend(
            "Local",
            new LocalConnectionConfig { RootPath = directory.Path });
        var content = Encoding.UTF8.GetBytes("checksum-content");
        Assert.True((await storage.UploadBytesAsync("item.bin", content)).IsSuccess);
        var expected = Convert.ToHexStringLower(SHA256.HashData(content));
        var reports = new List<StorageTransferProgress>();

        var checksum = await storage.ComputeChecksumAsync(
            "item.bin",
            progress: new InlineProgress<StorageTransferProgress>(reports.Add));
        var matching = await storage.VerifyChecksumAsync("item.bin", expected);
        var mismatch = await storage.VerifyChecksumAsync("item.bin", new string('0', 64));

        Assert.True(checksum.IsSuccess, checksum.Error?.Message);
        Assert.Equal(expected, checksum.Value!.HexValue);
        Assert.Equal(content.Length, checksum.Value.BytesProcessed);
        Assert.True(reports[^1].IsCompleted);
        Assert.True(matching.Value!.Matches);
        Assert.False(mismatch.Value!.Matches);
    }

    [Fact]
    public async Task Metadata_and_signed_url_calls_use_normalized_proxy_paths_and_hold_the_backend_lease()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IReadOnlyDictionary<string, string>? received = null;
        var backend = new FakeStorageBackend(
            "Advanced",
            capabilities: new StorageCapabilities(
                StorageFeature.MetadataRead |
                StorageFeature.MetadataWrite |
                StorageFeature.SignedReadUrls),
            setMetadata: async (path, metadata, _, cancellationToken) =>
            {
                Assert.Equal("folder/item.bin", path);
                received = metadata;
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Result<StorageItem>.Success(new StorageItem
                {
                    Path = path,
                    Name = "item.bin",
                    ItemType = StorageItemType.File,
                    Metadata = metadata
                });
            },
            createSignedUrl: (path, options, _) =>
            {
                Assert.Equal("folder/item.bin", path);
                Assert.Equal(StorageSignedUrlMethod.Read, options!.Method);
                return Task.FromResult(Result<StorageSignedUrl>.Success(new StorageSignedUrl(
                    new Uri("https://storage.example.test/item?signature=secret"),
                    options.Method,
                    DateTimeOffset.UtcNow.Add(options.ExpiresIn))));
            });
        Assert.True(library.RegisterBackend("Advanced", backend).IsSuccess);
        var service = library.GetStorage("Advanced");
        var callerMetadata = new Dictionary<string, string> { ["color"] = "blue" };

        var update = service.SetMetadataAsync(@"folder\.\item.bin", callerMetadata);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerMetadata["color"] = "red";
        var replacement = Task.Run(() => library.RegisterBackend("Advanced", new FakeStorageBackend("Advanced")));
        await Task.Delay(50);

        Assert.False(replacement.IsCompleted);
        release.TrySetResult();
        Assert.True((await update).IsSuccess);
        Assert.Equal("blue", received!["color"]);
        Assert.True((await replacement).IsSuccess);

        Assert.True(library.RegisterBackend("Advanced", backend, ownsBackend: false).IsSuccess);
        var signed = await service.CreateSignedUrlAsync("folder//item.bin");
        Assert.True(signed.IsSuccess, signed.Error?.Message);
        Assert.Equal(StorageSignedUrlMethod.Read, signed.Value!.Method);
    }

    [Fact]
    public async Task Tag_calls_validate_snapshot_and_normalize_proxy_paths()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        IReadOnlyDictionary<string, string>? received = null;
        var backend = new FakeStorageBackend(
            "Tags",
            capabilities: new StorageCapabilities(StorageFeature.Tags),
            getTags: (path, _) =>
            {
                Assert.Equal("folder/item.bin", path);
                return Task.FromResult(Result<IReadOnlyDictionary<string, string>>.Success(
                    new Dictionary<string, string> { ["tier"] = "archive" }));
            },
            setTags: (path, tags, options, _) =>
            {
                Assert.Equal("folder/item.bin", path);
                Assert.Equal(StorageTagUpdateMode.Merge, options!.Mode);
                received = tags;
                return Task.FromResult(Result<StorageItem>.Success(new StorageItem
                {
                    Path = path,
                    Name = "item.bin",
                    ItemType = StorageItemType.File
                }));
            });
        Assert.True(library.RegisterBackend("Tags", backend).IsSuccess);
        var service = library.GetStorage("Tags");
        var callerTags = new Dictionary<string, string> { ["project"] = "apollo" };

        var updated = await service.SetTagsAsync(
            @"folder\.\item.bin",
            callerTags,
            new StorageTagUpdateOptions { Mode = StorageTagUpdateMode.Merge });
        callerTags["project"] = "changed";
        var read = await service.GetTagsAsync("folder//item.bin");

        Assert.True(updated.IsSuccess, updated.Error?.Message);
        Assert.Equal("apollo", received!["project"]);
        Assert.Equal("archive", read.Value!["tier"]);
        Assert.True(service.Capabilities.Supports(StorageFeature.Tags));

        var tooMany = Enumerable.Range(0, 11).ToDictionary(index => $"tag-{index}", _ => "value");
        var invalid = await service.SetTagsAsync("folder/item.bin", tooMany);
        Assert.True(invalid.IsFailure);
        Assert.Equal(StorageErrors.TooLargeCode, invalid.Error!.Code);
    }

    [Fact]
    public async Task Option_validation_rejects_header_injection_and_unsupported_metadata()
    {
        var invalid = new StorageUploadOptions
        {
            Metadata = new Dictionary<string, string> { ["safe"] = "value\r\ninjected" }
        }.Validate();
        Assert.True(invalid.IsFailure);

        using var directory = new TestDirectory();
        await using var local = new LocalStorageBackend(
            "Local",
            new LocalConnectionConfig { RootPath = directory.Path });
        var unsupported = await local.UploadBytesAsync("item.bin", [1], new StorageUploadOptions
        {
            Metadata = new Dictionary<string, string> { ["color"] = "blue" }
        });

        Assert.True(unsupported.IsFailure);
        Assert.Equal(StorageErrors.UnsupportedCode, unsupported.Error!.Code);

        var incompatible = new StorageUploadOptions
        {
            Overwrite = false,
            Condition = new StorageMutationCondition { ExpectedETag = "etag" }
        }.Validate();
        Assert.True(incompatible.IsFailure);

        var conditional = await local.UploadBytesAsync("conditional.bin", [1], new StorageUploadOptions
        {
            Condition = new StorageMutationCondition { ExpectedETag = "etag" }
        });
        Assert.True(conditional.IsFailure);
        Assert.Equal(StorageErrors.UnsupportedCode, conditional.Error!.Code);
    }

    [Fact]
    public async Task Version_service_normalizes_paths_and_forwards_exact_version_operations()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        string? listedPath = null;
        (string Path, string Version)? deleted = null;
        var backend = new FakeStorageBackend(
            "Versions",
            capabilities: new StorageCapabilities(StorageFeature.Versioning),
            listVersions: (path, _, _) =>
            {
                listedPath = path;
                return Task.FromResult(Result<StorageVersionPage>.Success(new StorageVersionPage(
                    [new StorageVersion { Path = path, VersionId = "v2", IsLatest = true }],
                    null)));
            },
            deleteVersion: (path, versionId, _) =>
            {
                deleted = (path, versionId);
                return Task.FromResult(Result.Success());
            });
        Assert.True(library.RegisterBackend("Versions", backend).IsSuccess);
        var storage = library.GetStorage("Versions");

        var page = await storage.ListVersionsAsync(@"folder\.\item.bin");
        var removal = await storage.DeleteVersionAsync("folder//item.bin", "v1");

        Assert.True(page.IsSuccess, page.Error?.Message);
        Assert.Equal("folder/item.bin", listedPath);
        Assert.True(removal.IsSuccess, removal.Error?.Message);
        Assert.Equal(("folder/item.bin", "v1"), deleted);
    }

    private sealed record TestPayload(string Color, int Count);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
