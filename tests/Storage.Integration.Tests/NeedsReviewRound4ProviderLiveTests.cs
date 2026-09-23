using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Storage.Blobs;
using CL.Storage.Abstractions;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Azure;
using CL.Storage.Providers.WebDav;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Needs-review round 4, providers, against the live servers.</summary>
public sealed class NeedsReviewRound4ProviderLiveTests
{
    // needs-review R4-A5: FTP removes only an empty folder, and a hidden file inside keeps it
    [FtpFact]
    public async Task A_non_recursive_FTP_folder_delete_keeps_a_folder_holding_a_hidden_file() =>
        await NonRecursiveDeleteKeepsContentsAsync(LiveServers.Create(LiveServers.Ftp()));

    // needs-review R4-A5: WebDAV deletes a collection non-recursively only under a lock, and never one that holds anything
    [WebDavFact]
    public async Task A_non_recursive_WebDAV_folder_delete_keeps_a_folder_holding_a_hidden_file() =>
        await NonRecursiveDeleteKeepsContentsAsync(LiveServers.Create(LiveServers.WebDav()));

    private static async Task NonRecursiveDeleteKeepsContentsAsync(IStorageBackend backend)
    {
        await using var storage = backend;
        var dir = $"r4a5-{Guid.NewGuid():N}";
        try
        {
            Assert.True((await storage.UploadBytesAsync($"{dir}/full/.hidden", [1])).IsSuccess);
            Assert.True((await storage.CreateDirectoryAsync($"{dir}/empty")).IsSuccess);

            var full = await storage.DeleteAsync($"{dir}/full", new StorageDeleteOptions { Recursive = false });
            var empty = await storage.DeleteAsync($"{dir}/empty", new StorageDeleteOptions { Recursive = false });

            Assert.True(full.Error?.Code == StorageErrors.ConflictCode, full.Error?.ToString() ?? "deleted");
            Assert.Equal([1], (await storage.DownloadBytesAsync($"{dir}/full/.hidden")).Value!);
            Assert.True(empty.IsSuccess, empty.Error?.ToString());
            Assert.False((await storage.ExistsAsync($"{dir}/empty")).Value);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review R4-A8: a WebDAV upload with Overwrite onto an existing folder is refused and the folder kept
    [WebDavFact]
    public async Task A_WebDAV_upload_with_overwrite_onto_a_folder_is_refused_and_the_folder_kept()
    {
        await using var storage = LiveServers.Create(LiveServers.WebDav());
        var dir = $"r4a8-{Guid.NewGuid():N}";
        try
        {
            Assert.True((await storage.UploadBytesAsync($"{dir}/target/keep.txt", [3])).IsSuccess);

            var uploaded = await storage.UploadBytesAsync($"{dir}/target", [1, 2], new StorageUploadOptions { Overwrite = true });

            Assert.Equal(StorageErrors.ConflictCode, uploaded.Error?.Code);
            Assert.Equal([3], (await storage.DownloadBytesAsync($"{dir}/target/keep.txt")).Value!);
            var left = (await storage.ListAsync(dir, new StorageListOptions { IncludeInternal = true, IncludeHidden = true })).Value!.Items.Select(item => item.Name);
            Assert.Equal(["target"], left);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review R4-A6 / R4-A7 / R4-B24: on MinIO the public query, the flags, and what the backend sends agree with the server
    [S3Fact]
    public async Task MinIO_condition_enforcement_is_reported_per_condition_and_copies_and_moves_still_work()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.S3(c => c.Prefix = $"r4a7-{Guid.NewGuid():N}"));
        await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("a"));
        await storage.UploadBytesAsync("taken.txt", Encoding.UTF8.GetBytes("taken"));
        try
        {
            var uploadCreate = await storage.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly);
            var copyCreate = await storage.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true);
            var delete = await storage.GetConditionEnforcementAsync(StorageConditionKind.DeleteMatchVersion);
            var created = await storage.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { Overwrite = false });
            var refused = await storage.CopyAsync("a.txt", "taken.txt", new StorageTransferOptions { Overwrite = false });
            var uploadRefused = await storage.UploadBytesAsync("taken.txt", [9], new StorageUploadOptions { Overwrite = false });
            var moved = await storage.MoveAsync("b.txt", "c.txt");

            // MinIO enforces conditions on PutObject, and ignores them on CopyObject and DeleteObject.
            Assert.Equal(StorageConditionEnforcement.Atomic, uploadCreate);
            Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, copyCreate);
            Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, delete);
            Assert.False(storage.Capabilities.Supports(StorageFeature.ConditionalCreate));
            Assert.False(storage.Capabilities.Supports(StorageFeature.ConditionalDelete));
            Assert.True(created.IsSuccess, created.Error?.ToString());
            Assert.Equal(StorageErrors.ConflictCode, refused.Error?.Code);
            Assert.Equal(StorageErrors.ConflictCode, uploadRefused.Error?.Code);
            Assert.Equal("taken", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("taken.txt")).Value!));
            Assert.True(moved.IsSuccess, moved.Error?.ToString());
            Assert.False((await storage.ExistsAsync("b.txt")).Value);
            var left = (await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Path).Order(StringComparer.Ordinal);
            Assert.Equal(["a.txt", "c.txt", "taken.txt"], left);
        }
        finally
        {
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt", "taken.txt" })
                await storage.DeleteAsync(name, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review R4-B3: a server-side copy whose caller cancels while it is pending is waited for and reported done
    [AzureFact]
    public async Task An_Azure_copy_cancelled_while_pending_is_waited_for_and_reported_complete()
    {
        var config = CloudEmulators.Azure();
        var container = new BlobContainerClient(config.ConnectionString, config.Container);
        await container.CreateIfNotExistsAsync();
        using var cancel = new CancellationTokenSource();
        await using var storage = new AzureBlobStorageBackend("r4b3", container, $"r4b3-{Guid.NewGuid():N}")
        {
            AfterCopyStarted = () => cancel.CancelAsync()
        };
        await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("new"));
        await storage.UploadBytesAsync("b.txt", Encoding.UTF8.GetBytes("old"));
        try
        {
            var copied = await storage.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { Overwrite = true }, cancel.Token);

            Assert.True(copied.IsSuccess, copied.Error?.ToString());
            Assert.Equal("new", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("b.txt")).Value!));
        }
        finally
        {
            await storage.DeleteAsync("a.txt", new StorageDeleteOptions { IgnoreMissing = true });
            await storage.DeleteAsync("b.txt", new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review R4-A10: with an accepted client certificate, spare connections closed later record no refusal
    [MutualTlsFact]
    public async Task Connections_with_an_accepted_client_certificate_record_no_refusal_when_they_are_closed()
    {
        var endpoint = new Uri(LiveServers.Env("CL_STORAGE_TEST_WEBDAV_MTLS_URL") ?? "https://localhost:8444/");
        var pin = await ServerPinAsync(endpoint);
        var config = LiveServers.WebDav(c =>
        {
            c.Endpoint = endpoint.ToString();
            c.AllowInsecureHttp = false;
            c.TrustedPublicKeySha256 = [pin];
            c.ClientCertificateContent = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "mtls", "client.pfx"));
            c.ClientCertificatePassword = "cltest";
            c.Retry.RetryCount = 0;
        });
        var storage = (WebDavStorageBackend)LiveServers.Create(config);
        var recorder = storage.Identity!;
        var dir = $"r4a10-{Guid.NewGuid():N}";
        try
        {
            Assert.True((await storage.UploadBytesAsync($"{dir}/a.txt", [1])).IsSuccess);
            // Parallel reads open several connections; the pool keeps the spares idle until it closes them.
            var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => storage.GetInfoAsync($"{dir}/a.txt")));
            Assert.All(reads, read => Assert.True(read.IsSuccess, read.Error?.ToString()));
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            await storage.DisposeAsync();
        }

        Assert.Null(recorder.ClientCertificateRefusedAt);
    }

    private static async Task<string> ServerPinAsync(Uri endpoint)
    {
        string? pin = null;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(endpoint.Host, endpoint.Port);
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            using var parsed = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
            pin = Convert.ToHexString(SHA256.HashData(parsed.PublicKey.ExportSubjectPublicKeyInfo()));
            return true;
        });
        try { await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = endpoint.Host }); }
        catch (Exception) when (pin is not null) { /* Refused for lack of a client certificate; the pin was read. */ }
        return pin!;
    }
}
