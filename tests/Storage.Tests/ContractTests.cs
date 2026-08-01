using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

public sealed class StoragePathTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("//alpha///./beta/", "alpha/beta")]
    [InlineData(@"alpha\.\beta", "alpha/beta")]
    public void Normalize_returns_a_relative_slash_separated_path(string input, string expected)
    {
        var result = StoragePath.Normalize(input);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("alpha/../secret")]
    [InlineData(@"alpha\..\secret")]
    [InlineData("alpha\0secret")]
    public void Normalize_rejects_paths_that_can_escape_or_are_malformed(string input)
    {
        var result = StoragePath.Normalize(input);

        Assert.True(result.IsFailure);
        Assert.Equal("storage.invalid_path", result.Error!.Code);
    }
}

public sealed class StorageModelTests
{
    [Fact]
    public void Models_defensively_copy_read_only_collections()
    {
        var metadata = new Dictionary<string, string> { ["color"] = "blue" };
        var item = new StorageItem
        {
            Path = "a.txt",
            Name = "a.txt",
            ItemType = StorageItemType.File,
            Metadata = metadata
        };
        var inputItems = new List<StorageItem> { item };
        var page = new StoragePage(inputItems, "opaque-token");

        metadata["color"] = "red";
        inputItems.Clear();

        Assert.Equal("blue", item.Metadata["color"]);
        Assert.Single(page.Items);
        Assert.Equal("opaque-token", page.ContinuationToken);
    }

    [Fact]
    public void Provider_and_item_enums_expose_the_neutral_contract()
    {
        Assert.Equal(
            new[] { "Local", "S3", "Ftp", "Sftp", "WebDav", "AzureBlob", "GoogleCloudStorage", "OpenStackSwift" },
            Enum.GetNames<StorageProvider>());
        Assert.Equal(new[] { "File", "Directory", "Link" }, Enum.GetNames<StorageItemType>());
    }
}

public sealed class StorageOptionsTests
{
    [Fact]
    public void Options_have_safe_documented_defaults()
    {
        Assert.Equal(1000, new StorageListOptions().PageSize);
        Assert.True(new StorageUploadOptions().Overwrite);
        Assert.True(new StorageUploadOptions().CreateParents);
        Assert.True(new StorageTransferOptions().Overwrite);
        Assert.True(new StorageTransferOptions().CreateParents);
        Assert.Null(new StorageDownloadOptions().MaxBufferedBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void List_options_reject_non_positive_page_sizes(int pageSize)
    {
        var result = new StorageListOptions { PageSize = pageSize }.Validate();

        Assert.True(result.IsFailure);
        Assert.Equal("storage.invalid_path", result.Error!.Code);
    }

    [Fact]
    public void Download_options_reject_negative_or_overflowing_ranges_and_non_positive_limits()
    {
        AssertInvalid(new StorageDownloadOptions { Offset = -1 });
        AssertInvalid(new StorageDownloadOptions { Length = 0 });
        AssertInvalid(new StorageDownloadOptions { MaxBufferedBytes = 0 });
        AssertInvalid(new StorageDownloadOptions { Offset = long.MaxValue, Length = 1 });

        static void AssertInvalid(StorageDownloadOptions options)
        {
            var result = options.Validate();
            Assert.True(result.IsFailure);
            Assert.Equal("storage.invalid_path", result.Error!.Code);
        }
    }
}

public sealed class StorageErrorTests
{
    [Fact]
    public void Error_factories_use_stable_codes()
    {
        Error[] errors =
        [
            StorageErrors.InvalidPath("bad"),
            StorageErrors.NotFound("missing"),
            StorageErrors.Unauthorized("denied"),
            StorageErrors.Timeout("slow"),
            StorageErrors.Conflict("exists"),
            StorageErrors.Unavailable("offline"),
            StorageErrors.Unsupported("nope"),
            StorageErrors.TooLarge("large"),
            StorageErrors.ProviderError("failed")
        ];

        Assert.Equal(
            new[]
            {
                "storage.invalid_path", "storage.not_found", "storage.unauthorized", "storage.timeout",
                "storage.conflict", "storage.unavailable", "storage.unsupported", "storage.too_large",
                "storage.provider_error"
            },
            errors.Select(error => error.Code));
    }
}

public sealed class StorageConfigurationTests
{
    [Fact]
    public void Config_defaults_match_the_library_contract()
    {
        var storage = new StorageConfig();
        var local = new LocalConnectionConfig();

        Assert.True(storage.Enabled);
        Assert.Equal("Default", storage.DefaultConnection);
        Assert.Equal(10, storage.HealthCheckTimeoutSeconds);
        Assert.Equal(67_108_864, storage.MaxBufferedDownloadBytes);
        Assert.True(local.Enabled);
        Assert.Equal(string.Empty, local.RootPath);
        Assert.False(local.FollowLinks);
        Assert.Equal(30, local.TimeoutSeconds);
    }

    [Fact]
    public void Config_validation_rejects_invalid_sizes_and_required_local_root()
    {
        Assert.False(new StorageConfig { HealthCheckTimeoutSeconds = 0 }.Validate().IsValid);
        Assert.False(new StorageConfig { MaxBufferedDownloadBytes = 0 }.Validate().IsValid);
        Assert.False(new LocalConnectionConfig().Validate().IsValid);
        Assert.False(new LocalConnectionConfig { RootPath = "root", TimeoutSeconds = 0 }.Validate().IsValid);
        Assert.False(new LocalStorageConfig
        {
            Connections = new Dictionary<string, LocalConnectionConfig>
            {
                ["Default"] = new() { RootPath = "" }
            }
        }.Validate().IsValid);
    }

    [Fact]
    public void Valid_local_configuration_passes()
    {
        var config = new LocalStorageConfig
        {
            Connections = new Dictionary<string, LocalConnectionConfig>
            {
                ["Default"] = new() { RootPath = Path.GetTempPath() }
            }
        };

        Assert.True(config.Validate().IsValid);
    }
}

public sealed class NativeConnectionLeaseTests
{
    [Fact]
    public async Task DisposeAsync_releases_the_client_exactly_once()
    {
        var releases = 0;
        var client = new object();
        var lease = new NativeConnectionLease<object>(client, _ =>
        {
            Interlocked.Increment(ref releases);
            return ValueTask.CompletedTask;
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lease.DisposeAsync().AsTask()));

        Assert.Same(client, lease.Client);
        Assert.Equal(1, releases);
    }
}
