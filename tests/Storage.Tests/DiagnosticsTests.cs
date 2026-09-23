using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Sftp;
using CodeLogic.Core.Events;
using CodeLogic.Core.Results;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers connection diagnostics, health tracking, operation-failure events, and connection tests.</summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public async Task Failed_operations_publish_an_event_with_operation_path_and_code()
    {
        using var directory = new TestDirectory();
        var eventBus = new EventBus();
        var failures = new List<StorageOperationFailedEvent>();
        using var subscription = eventBus.Subscribe<StorageOperationFailedEvent>(value => { lock (failures) failures.Add(value); });
        var (library, service) = await CreateAsync(directory, eventBus, new FakeStorageBackend(
            "Diag",
            provider: StorageProvider.Ftp,
            upload: (_, _) => Task.FromResult(Result<StorageItem>.Failure(StorageErrors.QuotaExceeded("full")))));
        using var _ = library;

        Assert.True((await service.GetInfoAsync("docs/missing.txt")).IsFailure);
        Assert.True((await service.UploadBytesAsync("big.bin", [1])).IsFailure);
        Assert.True((await service.GetInfoAsync("../escape")).IsFailure); // rejected before the provider

        await WaitFor(() => { lock (failures) return failures.Count >= 2; });
        lock (failures)
        {
            Assert.Equal(2, failures.Count);
            Assert.Contains(failures, value => value is { Operation: "GetInfo", Path: "docs/missing.txt", ErrorCode: StorageErrors.NotFoundCode, Provider: StorageProvider.Ftp });
            Assert.Contains(failures, value => value is { Operation: "UploadBytes", Path: "big.bin", ErrorCode: "storage.quota_exceeded" });
        }
    }

    [Fact]
    public async Task Health_changes_are_recorded_and_published_only_when_the_state_flips()
    {
        using var directory = new TestDirectory();
        var eventBus = new EventBus();
        var changes = new List<StorageConnectionHealthChangedEvent>();
        using var subscription = eventBus.Subscribe<StorageConnectionHealthChangedEvent>(value => { lock (changes) changes.Add(value); });
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"), eventBus);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(
            library,
            context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = directory.CreateDirectory("default") });
        var backend = new FakeStorageBackend("Default");
        Assert.True(library.RegisterBackend("Default", backend).IsSuccess);

        Assert.True((await library.CheckConnectionHealthAsync("Default")).IsSuccess);
        Assert.True((await library.CheckConnectionHealthAsync("Default")).IsSuccess);
        backend.SetHealth(_ => Task.FromResult(Result.Failure(StorageErrors.ConnectionFailed("refused"))));
        await library.HealthCheckAsync();
        backend.SetHealth(_ => Task.FromResult(Result.Success()));
        await library.HealthCheckAsync();

        await WaitFor(() => { lock (changes) return changes.Count >= 3; });
        lock (changes)
        {
            Assert.Equal(3, changes.Count);
            Assert.Equal((null, true), (changes[0].PreviouslyHealthy, changes[0].Healthy));
            Assert.Equal((true, false, "storage.connection_failed"), (changes[1].PreviouslyHealthy, changes[1].Healthy, changes[1].ErrorCode));
            Assert.Equal((false, true), (changes[2].PreviouslyHealthy, changes[2].Healthy));
        }
        var info = library.GetConnections().Single(connection => connection.Id == "Default");
        Assert.True(info.LastHealth!.Healthy);
        Assert.True(info.LastHealth.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Diagnostics_for_a_backend_without_server_details_report_the_basics()
    {
        using var directory = new TestDirectory();
        var (library, _) = await CreateAsync(directory, new EventBus(), new FakeStorageBackend("Diag", provider: StorageProvider.S3));
        using var _ = library;
        await library.CheckConnectionHealthAsync("Diag");

        var diagnostics = (await library.GetConnectionDiagnosticsAsync("Diag")).Value!;

        Assert.Equal(StorageProvider.S3, diagnostics.Provider);
        Assert.Null(diagnostics.ServerIdentity);
        Assert.Null(diagnostics.Pool);
        Assert.True(diagnostics.LastHealth!.Healthy);
    }

    [Fact]
    public async Task Testing_a_local_connection_runs_every_step()
    {
        using var directory = new TestDirectory();
        using var library = new global::CL.Storage.StorageLibrary();

        var report = await library.TestConnectionAsync(new LocalConnectionConfig { RootPath = directory.CreateDirectory("root") });

        Assert.True(report.Succeeded, report.Error?.Message);
        Assert.Equal(["validate", "connect", "list", "details"], report.Steps.Select(step => step.Name));
        Assert.All(report.Steps, step => Assert.True(step.Succeeded));
        Assert.Equal(StorageProvider.Local, report.Diagnostics!.Provider);
    }

    [Fact]
    public async Task Testing_invalid_settings_stops_at_validation_without_connecting()
    {
        using var library = new global::CL.Storage.StorageLibrary();

        var report = await library.TestConnectionAsync(new SftpConnectionConfig { Host = "", Username = "" });

        Assert.False(report.Succeeded);
        var step = Assert.Single(report.Steps);
        Assert.Equal(("validate", false), (step.Name, step.Succeeded));
        Assert.NotNull(report.Error);
    }

    [Fact]
    public async Task Testing_an_unreachable_server_reports_the_connect_step()
    {
        using var library = new global::CL.Storage.StorageLibrary();

        var report = await library.TestConnectionAsync(new FtpConnectionConfig
        {
            Host = "127.0.0.1",
            Port = 1, // nothing listens here
            Username = "user",
            Password = "secret",
            EncryptionMode = StorageFtpEncryptionMode.None,
            TimeoutSeconds = 5,
            Retry = new StorageRetryConfig { RetryCount = 0 }
        });

        Assert.False(report.Succeeded);
        Assert.Equal(["validate", "connect"], report.Steps.Select(step => step.Name));
        Assert.Equal("storage.connection_failed", report.Error!.Code);
    }

    [Fact]
    public void Connection_info_describes_the_endpoint_without_secrets()
    {
        Assert.Equal(("ftp.example.com", 990, StorageTransportSecurity.Tls),
            StorageEndpoints.Describe(new FtpConnectionConfig { Host = "ftp.example.com", Port = 990, EncryptionMode = StorageFtpEncryptionMode.Implicit }));
        Assert.Equal(("files.example.com", 443, StorageTransportSecurity.Tls),
            StorageEndpoints.Describe(new WebDavConnectionConfig { Endpoint = "https://files.example.com/dav/" }));
        Assert.Equal(("127.0.0.1", 10000, StorageTransportSecurity.None),
            StorageEndpoints.Describe(new AzureBlobConnectionConfig { ConnectionString = "UseDevelopmentStorage=true" }));
        Assert.Equal(("acct.blob.core.windows.net", 443, StorageTransportSecurity.Tls),
            StorageEndpoints.Describe(new AzureBlobConnectionConfig { ConnectionString = "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=c2VjcmV0" }));
        Assert.Equal(("s3.eu-west-1.amazonaws.com", 443, StorageTransportSecurity.Tls),
            StorageEndpoints.Describe(new S3ConnectionConfig { Region = "eu-west-1" }));
        Assert.Equal(("localhost", 9010, StorageTransportSecurity.None),
            StorageEndpoints.Describe(new S3ConnectionConfig { ServiceUrl = "http://localhost:9010" }));
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13", "OpenSSH_9.6p1")]
    [InlineData("SSH-2.0-dropbear_2022.83", "dropbear_2022.83")]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Ssh_software_is_read_from_the_version_string(string? version, string? expected) =>
        Assert.Equal(expected, SftpStorageBackend.SoftwareOf(version));

    [Fact]
    public void Identity_recorder_keeps_ssh_fingerprints_in_pin_format()
    {
        var recorder = new ServerIdentityRecorder();
        recorder.RecordHostKey("ssh-ed25519", "abc123", trusted: false);

        Assert.Equal(new StorageServerIdentity("ssh-host-key", "SHA256:abc123", null, "ssh-ed25519", null, null, null, false), recorder.Last);
    }

    private static async Task<(global::CL.Storage.StorageLibrary Library, CL.Storage.Abstractions.IStorageService Service)> CreateAsync(
        TestDirectory directory,
        IEventBus eventBus,
        FakeStorageBackend backend)
    {
        var context = StorageLibraryTestSupport.CreateContext(directory.Path, eventBus);
        var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        Assert.True(library.RegisterBackend(backend.ConnectionId, backend).IsSuccess);
        return (library, library.GetStorage(backend.ConnectionId));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(20);
    }
}
