using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Registry;
using CL.Storage.Sync;
using CodeLogic.Core.Results;
using CodeLogic.Framework.Libraries;
using Xunit;

namespace Storage.Tests;

/// <summary>Sync corrections, native watching through the proxy, download totals, and folder inference.</summary>
public sealed class SyncCorrectionTests
{
    private static readonly DateTimeOffset Old = new(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset New = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Mirror_does_not_copy_an_older_source_over_a_newer_destination_of_the_same_size()
    {
        using var directory = new TestDirectory();
        var a = new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") });
        var b = new LocalStorageBackend("b", new LocalConnectionConfig { RootPath = directory.CreateDirectory("b") });
        await a.UploadBytesAsync("f.txt", "aaa"u8.ToArray());
        await a.SetTimestampsAsync("f.txt", Old);
        await b.UploadBytesAsync("f.txt", "bbb"u8.ToArray());
        await b.SetTimestampsAsync("f.txt", New);

        var report = (await a.SyncAsync("", b, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror })).Value!;

        Assert.Equal(0, report.Copied);
        Assert.Equal("bbb", Encoding.UTF8.GetString((await b.DownloadBytesAsync("f.txt")).Value!));
    }

    [Fact]
    public async Task Mirror_skips_deletes_when_a_copy_failed()
    {
        using var directory = new TestDirectory();
        var source = new LocalStorageBackend("a", new LocalConnectionConfig { RootPath = directory.CreateDirectory("a") });
        await source.UploadBytesAsync("new.txt", [1]);
        var deleted = new List<string>();
        var destination = new FakeStorageBackend(
            "b",
            getInfo: (path, _) => Task.FromResult(path.Length == 0
                ? Result<StorageItem>.Success(new StorageItem { Path = "", Name = "", ItemType = StorageItemType.Directory })
                : Result<StorageItem>.Failure(StorageErrors.NotFound("missing"))),
            list: (_, _, _) => Task.FromResult(Result<StoragePage>.Success(new StoragePage(
                [new StorageItem { Path = "extra.txt", Name = "extra.txt", ItemType = StorageItemType.File, Size = 1 }], null))),
            uploadStream: (_, _, _, _) => Task.FromResult(Result<StorageItem>.Failure(StorageErrors.QuotaExceeded("full"))),
            delete: (path, _) => { deleted.Add(path); return Task.FromResult(Result.Success()); });

        var report = (await source.SyncAsync("", destination, "", new StorageSyncOptions { Direction = StorageSyncDirection.Mirror, DeleteExtraneous = true })).Value!;

        Assert.Empty(deleted);
        var skipped = Assert.Single(report.Actions, action => action.Kind == StorageSyncActionKind.DeleteFromDestination);
        Assert.Equal(StorageErrors.PartialFailureCode, skipped.Error!.Code);
    }
}

public sealed class NativeWatchThroughProxyTests
{
    [Fact]
    public async Task Connections_from_GetStorage_watch_natively()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("watched");
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context,
            configureLocal: local => local.Connections["Default"] = new() { RootPath = root });
        var files = library.GetStorage("Default");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var seen = new List<StorageChange>();
        var watching = Task.Run(async () =>
        {
            try
            {
                await foreach (var change in files.WatchAsync("", cancellationToken: stop.Token))
                {
                    lock (seen) seen.Add(change);
                    if (change.Kind == StorageChangeKind.Renamed) break;
                }
            }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(300);

        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "x");
        File.Move(Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt"));
        await watching.WaitAsync(TimeSpan.FromSeconds(15));

        // Only native watching reports renames; polling would report a delete and a create.
        lock (seen) Assert.Contains(seen, change => change is { Kind: StorageChangeKind.Renamed, Path: "b.txt" });
    }
}

public sealed class DownloadTotalTests
{
    [Fact]
    public async Task Progress_on_a_download_without_a_length_carries_the_item_size()
    {
        var backend = new FakeStorageBackend(
            "fake",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 10 })));
        var reports = new List<StorageTransferProgress>();
        var options = new StorageDownloadOptions { Progress = new SynchronousProgress<StorageTransferProgress>(reports.Add), Offset = 4 };

        var metered = await StorageTransferPipeline.MeterAsync(backend, "f.bin", Result<Stream>.Success(new NonSeekableStream(new byte[6])), options, CancellationToken.None);
        await using (var stream = metered.Value!)
            await stream.CopyToAsync(Stream.Null);

        Assert.Contains(reports, report => report.TotalBytes == 6);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}

public sealed class ImplicitDirectoryTests
{
    private static StorageItem Folder(string path) => new() { Path = path, Name = path, ItemType = StorageItemType.Directory };

    [Fact]
    public void Folders_are_reported_once_even_across_pages()
    {
        var items = new List<StorageItem>();
        string? previous = null;
        foreach (var key in new[] { "a/b/1.txt", "a/b/2.txt", "a/c.txt" })
        {
            ImplicitDirectories.AddParents(items, key, "", previous, Folder);
            previous = key;
        }
        // A page boundary: the next page starts with the last key carried in the token.
        var (_, carried) = ImplicitDirectories.Unwrap(ImplicitDirectories.Wrap("native", previous));
        ImplicitDirectories.AddParents(items, "a/d/3.txt", "", carried, Folder);

        Assert.Equal(["a", "a/b", "a/d"], items.Select(item => item.Path));
    }

    [Fact]
    public void Folders_at_or_above_the_listing_root_are_not_reported()
    {
        var items = new List<StorageItem>();
        ImplicitDirectories.AddParents(items, "root/sub/x/file.txt", "root/sub", null, Folder);
        Assert.Equal(["root/sub/x"], items.Select(item => item.Path));
    }

    [Fact]
    public void Foreign_tokens_pass_through_unchanged()
    {
        Assert.Equal(("provider-token", (string?)null), ImplicitDirectories.Unwrap("provider-token"));
        Assert.Null(ImplicitDirectories.Wrap(null, "a"));
    }
}

public sealed class RuntimeOnlyTests
{
    [Fact]
    public async Task Runtime_only_mode_writes_no_configuration_and_needs_no_default_connection()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.CreateDirectory("library"));
        using var library = new global::CL.Storage.StorageLibrary(new StorageLibraryOptions { RuntimeOnly = true });
        await library.OnConfigureAsync(context);
        await context.Configuration.LoadAllAsync();
        await library.OnInitializeAsync(context);
        await library.OnStartAsync(context);

        Assert.Empty(library.GetConnections());
        Assert.NotEqual(HealthStatusLevel.Unhealthy, (await library.HealthCheckAsync()).Status);

        var added = await library.AddOrUpdateConnectionAsync("files", new LocalConnectionConfig { RootPath = directory.CreateDirectory("files") });
        Assert.True(added.IsSuccess, added.Error?.Message);
        Assert.True((await library.GetStorage("files").UploadBytesAsync("x.txt", [1])).IsSuccess);
        Assert.True((await library.RemoveConnectionAsync("files")).IsSuccess);

        var configDirectory = Path.Combine(directory.Path, "library", "config");
        Assert.Empty(Directory.Exists(configDirectory) ? Directory.GetFiles(configDirectory, "*storage*", SearchOption.AllDirectories) : []);
    }
}

public sealed class TlsDiagnosisTests
{
    [Theory]
    [InlineData("The remote certificate was rejected by the provided RemoteCertificateValidationCallback.", "server_certificate_rejected")]
    [InlineData("error:0A000412:SSL routines::sslv3 alert bad certificate:SSL alert number 42", "client_certificate_rejected")]
    [InlineData("error:0A00045C:SSL routines::tlsv13 alert certificate required:SSL alert number 116", "client_certificate_rejected")]
    [InlineData("error:0A00042E:SSL routines::tlsv1 alert protocol version:SSL alert number 70", "protocol_mismatch")]
    [InlineData("Authentication failed because the remote party has closed the transport stream.", "handshake_failed")]
    public void Tls_failures_are_classified(string message, string reason) =>
        Assert.Equal(reason, TlsDiagnosis.Reason(new System.Security.Authentication.AuthenticationException("Authentication failed", new Exception(message))));

    [Fact]
    public void A_recent_refused_certificate_is_attached_to_the_failure()
    {
        var recorder = new ServerIdentityRecorder();
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        recorder.RecordCertificate(certificate, trusted: false);

        var enriched = TlsDiagnosis.Enrich(StorageErrors.TlsFailure("refused", "tlsReason=handshake_failed"), recorder);

        Assert.True(StorageErrorInfo.TryGetDetail(enriched, StorageErrorInfo.TlsReasonKey, out var reason));
        Assert.Equal("server_certificate_rejected", reason);
        Assert.True(StorageErrorInfo.TryGetDetail(enriched, StorageErrorInfo.PresentedPublicKeyKey, out var pin));
        Assert.Equal(TlsPins.PublicKeyPin(certificate), pin);
    }
}

public sealed class ClientCertificateContentTests
{
    [Fact]
    public void A_certificate_can_be_loaded_from_bytes()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = certificate.Export(X509ContentType.Pfx, "secret");

        using var loaded = ClientCertificates.Load(null, pfx, "secret");

        Assert.Equal(certificate.Thumbprint, loaded!.Thumbprint);
        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void Path_and_content_are_mutually_exclusive()
    {
        var config = new WebDavConnectionConfig
        {
            Endpoint = "https://dav.example.com/",
            ClientCertificatePath = OperatingSystem.IsWindows() ? @"C:\c.pfx" : "/c.pfx",
            ClientCertificateContent = [1, 2, 3]
        };
        Assert.Contains(config.GetValidationErrors(), error => error.Contains("not both", StringComparison.Ordinal));
    }
}
