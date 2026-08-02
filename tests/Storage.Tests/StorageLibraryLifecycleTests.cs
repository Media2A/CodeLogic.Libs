using CL.Storage.Configuration;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace Storage.Tests;

public sealed class StorageLibraryLifecycleTests
{
    [Fact]
    public async Task Stop_after_default_snapshot_reports_the_lifecycle_state()
    {
        using var directory = new TestDirectory();
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeStorageBackendFactory((id, _) => new FakeStorageBackend(id));
        using var library = new global::CL.Storage.StorageLibrary(
            [factory],
            () =>
            {
                snapshotCaptured.TrySetResult();
                releaseSnapshot.Task.GetAwaiter().GetResult();
            });
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = directory.Path });
        var access = Task.Run(() => library.DefaultStorage);

        try
        {
            await snapshotCaptured.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
            await library.OnStopAsync().WaitAsync(TimeSpan.FromMilliseconds(750));

            releaseSnapshot.TrySetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => access);

            Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            releaseSnapshot.TrySetResult();
        }
    }

    [Fact]
    public void Public_storage_access_rejects_use_before_initialization()
    {
        using var library = new global::CL.Storage.StorageLibrary();

        var error = Assert.Throws<InvalidOperationException>(() => library.GetStorage());

        Assert.Contains("initialized", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configured_default_resolves_case_insensitively_and_connections_are_sanitized_snapshots()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("archive-root");
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            storage => storage.DefaultConnection = "archive",
            local => local.Connections["Archive"] = new LocalConnectionConfig { RootPath = root });

        var service = library.DefaultStorage;
        var firstSnapshot = library.GetConnections();

        Assert.Same(service, library.GetStorage("ARCHIVE"));
        Assert.Equal("Archive", service.ConnectionId);
        var connection = Assert.Single(firstSnapshot);
        Assert.Equal("Archive", connection.Id);
        Assert.Equal(root, connection.Root);
        Assert.True(connection.Enabled);
        Assert.DoesNotContain(connection.GetType().GetProperties(), property =>
            property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<NotSupportedException>(() => ((IList<global::CL.Storage.Models.StorageConnectionInfo>)firstSnapshot).Clear());
    }

    [Fact]
    public async Task Initialization_rejects_case_insensitive_duplicate_ids_across_provider_sections()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();
        context.Configuration.Get<StorageConfig>().DefaultConnection = "Shared";
        context.Configuration.Get<LocalStorageConfig>().Connections["Shared"] = new LocalConnectionConfig
        {
            RootPath = directory.CreateDirectory("local")
        };
        context.Configuration.Get<S3StorageConfig>().Connections["shared"] = new S3ConnectionConfig
        {
            Bucket = "bucket",
            Prefix = "prefix"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => library.OnInitializeAsync(context));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Shared", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disabled_library_is_an_initialized_healthy_no_op()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);

        var health = await library.HealthCheckAsync();

        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
        Assert.Empty(library.GetConnections());
        Assert.Throws<KeyNotFoundException>(() => library.GetStorage());
    }

    [Fact]
    public async Task Stop_is_idempotent_and_rejects_later_public_access()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);

        await library.OnStopAsync();
        await library.OnStopAsync();

        var error = Assert.Throws<InvalidOperationException>(() => library.GetConnections());
        Assert.Contains("stopped", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enabled_library_requires_the_configured_default_to_be_effective()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => library.OnInitializeAsync(context));

        Assert.Contains("Default", error.Message, StringComparison.Ordinal);
    }
}
