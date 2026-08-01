using CL.Storage.Configuration;
using Xunit;

namespace Storage.Tests;

public sealed class StorageLibraryConfigurationTests
{
    [Fact]
    public async Task Configure_registers_each_provider_with_its_required_filename()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();

        await library.OnConfigureAsync(context);
        await context.Configuration.GenerateAllDefaultsAsync();

        Assert.Equal(
            new[]
            {
                "config.storage.azure.json",
                "config.storage.ftp.json",
                "config.storage.gcs.json",
                "config.storage.json",
                "config.storage.local.json",
                "config.storage.s3.json",
                "config.storage.sftp.json",
                "config.storage.swift.json",
                "config.storage.webdav.json"
            },
            Directory.GetFiles(context.ConfigDirectory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Configure_registers_strong_local_and_forward_compatible_remote_sections()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();

        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();

        Assert.NotNull(context.Configuration.Get<StorageConfig>());
        Assert.NotNull(context.Configuration.Get<LocalStorageConfig>());
        Assert.NotNull(context.Configuration.Get<S3StorageConfig>());
        Assert.NotNull(context.Configuration.Get<FtpStorageConfig>());
        Assert.NotNull(context.Configuration.Get<SftpStorageConfig>());
        Assert.NotNull(context.Configuration.Get<WebDavStorageConfig>());
        Assert.NotNull(context.Configuration.Get<AzureStorageConfig>());
        Assert.NotNull(context.Configuration.Get<GoogleCloudStorageConfig>());
        Assert.NotNull(context.Configuration.Get<SwiftStorageConfig>());
    }
}
