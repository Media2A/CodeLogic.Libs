using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.S3;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Needs-review round 3, providers, against the live servers (the acceptance checks R2-2, R3-8, R3-9 among them).</summary>
public sealed class NeedsReviewProviderLiveTests
{
    // needs-review A10 (acceptance R2-2)
    [S3Fact]
    public async Task R2_2_an_S3_create_only_copy_keeps_its_properties_a_plain_ETag_and_the_server_MD5()
    {
        var prefix = $"accept-{Guid.NewGuid():N}";
        await using var live = await LiveLibrary.StartAsync(("s3", CloudEmulators.S3(c => c.Prefix = prefix)));
        var s3 = live.Library.GetStorage("s3");
        var payload = new byte[20 << 20];
        Random.Shared.NextBytes(payload);
        try
        {
            await using (var stream = new MemoryStream(payload))
                Assert.True((await s3.UploadAsync("r2-2/src.bin", stream, new StorageUploadOptions { ContentType = "application/x-test" })).IsSuccess);

            var copy = await live.Library.CopyAsync("s3", "r2-2/src.bin", "s3", "r2-2/dst.bin", new StorageTransferOptions { Overwrite = false });
            var info = await s3.GetInfoAsync("r2-2/dst.bin");
            var md5 = await s3.GetServerChecksumAsync("r2-2/dst.bin", StorageChecksumAlgorithm.Md5);

            Assert.Equal(StorageTransferOutcome.Completed, copy.Outcome);
            Assert.True(info.IsSuccess, info.Error?.ToString());
            Assert.DoesNotContain('-', info.Value!.ETag!);
            Assert.Equal("application/x-test", info.Value.ContentType);
            Assert.True(md5.IsSuccess, md5.Error?.ToString());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(payload)), md5.Value!.HexValue);
        }
        finally
        {
            await s3.DeleteAsync("r2-2", new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review A10 / B64 (contract C3): the probe says what the server really does with copy and delete conditions
    [S3Fact]
    public async Task The_S3_condition_probe_matches_what_the_server_does()
    {
        var config = CloudEmulators.S3(c => c.Prefix = $"probe-{Guid.NewGuid():N}");
        await using var storage = (S3StorageBackend)await CloudEmulators.CreateAsync(config);
        var source = (IStorageConditionEnforcementSource)storage;
        using var client = new AmazonS3Client(config.AccessKey, config.SecretKey,
            new AmazonS3Config { ServiceURL = config.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = config.Region, MaxErrorRetry = 0 });
        await storage.UploadBytesAsync("a.bin", [1]);
        await storage.UploadBytesAsync("b.bin", [2]);
        try
        {
            var copyEnforced = true;
            try
            {
                await client.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = config.Bucket, SourceKey = $"{config.Prefix}/a.bin",
                    DestinationBucket = config.Bucket, DestinationKey = $"{config.Prefix}/b.bin",
                    IfNoneMatch = "*"
                });
                copyEnforced = false;
            }
            catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.PreconditionFailed) { }

            var reported = await source.GetEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true, default);
            var uploads = await source.GetEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: false, default);

            Assert.Equal(copyEnforced ? StorageConditionEnforcement.Atomic : StorageConditionEnforcement.CheckedBeforeCommit, reported);
            Assert.Equal(StorageConditionEnforcement.Atomic, uploads);
            var left = (await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Path).Order();
            Assert.Equal(["a.bin", "b.bin"], left);
        }
        finally
        {
            await storage.DeleteAsync("a.bin", new StorageDeleteOptions { IgnoreMissing = true });
            await storage.DeleteAsync("b.bin", new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review A2 / B23: a pinned server-side copy refuses a source that changed
    [S3Fact]
    public async Task An_S3_copy_pinned_to_an_old_ETag_is_refused_and_a_move_deletes_only_what_it_copied()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.S3(c => c.Prefix = $"pin-{Guid.NewGuid():N}"));
        var first = (await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("one"))).Value!;
        await storage.UploadBytesAsync("a.txt", Encoding.UTF8.GetBytes("two"));
        try
        {
            var stale = await storage.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { ExpectedSourceETag = first.ETag });
            var moved = await storage.MoveAsync("a.txt", "c.txt");

            Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
            Assert.False((await storage.ExistsAsync("b.txt")).Value);
            Assert.True(moved.IsSuccess, moved.Error?.ToString());
            Assert.False((await storage.ExistsAsync("a.txt")).Value);
            Assert.Equal("two", Encoding.UTF8.GetString((await storage.DownloadBytesAsync("c.txt")).Value!));
        }
        finally
        {
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
                await storage.DeleteAsync(name, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review A2 / B23 (contract C2) on Azure Blob and Google Cloud Storage
    [AzureFact]
    public async Task An_Azure_copy_pinned_to_an_old_ETag_is_refused_and_a_move_keeps_nothing_behind() =>
        await PinnedCopyAndMoveAsync(await CloudEmulators.CreateAsync(CloudEmulators.Azure()), disposeAfter: true);

    // needs-review A2 / B23 (contract C2)
    [GcsFact]
    public async Task A_GCS_copy_pinned_to_an_old_ETag_is_refused_and_a_move_keeps_nothing_behind() =>
        await PinnedCopyAndMoveAsync(await CloudEmulators.CreateAsync(CloudEmulators.Gcs()), disposeAfter: true);

    private static async Task PinnedCopyAndMoveAsync(IStorageBackend storage, bool destinationConditions = true, bool disposeAfter = false)
    {
        await using var owned = disposeAfter ? storage : null;
        var dir = $"pin-{Guid.NewGuid():N}";
        var first = (await storage.UploadBytesAsync($"{dir}/a.txt", Encoding.UTF8.GetBytes("one"))).Value!;
        var second = (await storage.UploadBytesAsync($"{dir}/a.txt", Encoding.UTF8.GetBytes("two"))).Value!;
        await storage.UploadBytesAsync($"{dir}/other.txt", Encoding.UTF8.GetBytes("other"));
        try
        {
            var stale = await storage.CopyAsync($"{dir}/a.txt", $"{dir}/b.txt", new StorageTransferOptions { ExpectedSourceETag = first.ETag });
            var wrongDestination = await storage.CopyAsync($"{dir}/a.txt", $"{dir}/other.txt", new StorageTransferOptions
            {
                DestinationCondition = new StorageMutationCondition { ExpectedETag = first.ETag }
            });
            var moved = await storage.MoveAsync($"{dir}/a.txt", $"{dir}/c.txt", new StorageTransferOptions { ExpectedSourceETag = second.ETag });

            Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
            Assert.False((await storage.ExistsAsync($"{dir}/b.txt")).Value);
            Assert.Equal(destinationConditions ? StorageErrors.ConflictCode : StorageErrors.UnsupportedCode, wrongDestination.Error?.Code);
            Assert.Equal("other", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/other.txt")).Value!));
            Assert.True(moved.IsSuccess, moved.Error?.ToString());
            Assert.False((await storage.ExistsAsync($"{dir}/a.txt")).Value);
            Assert.Equal("two", Encoding.UTF8.GetString((await storage.DownloadBytesAsync($"{dir}/c.txt")).Value!));
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review A26 (acceptance R3-9): Swift ignores If-Match on PUT, so the library must check it
    [SwiftFact]
    public async Task R3_9_a_Swift_upload_with_a_wrong_If_Match_is_refused_and_the_file_kept()
    {
        await using var live = await LiveLibrary.StartAsync(("swift", CloudEmulators.Swift()));
        var swift = live.Library.GetStorage("swift");
        var name = $"accept-{Guid.NewGuid():N}.txt";
        try
        {
            var first = await swift.UploadBytesAsync(name, Encoding.UTF8.GetBytes("original"));
            await using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("replacement"));
            var conditional = await swift.UploadAsync(name, replacement, new StorageUploadOptions
            {
                Condition = new StorageMutationCondition { ExpectedETag = "\"00000000000000000000000000000000\"" }
            });
            var now = await swift.DownloadBytesAsync(name);

            Assert.True(first.IsSuccess, first.Error?.ToString());
            Assert.True(conditional.IsFailure);
            Assert.Equal(StorageErrors.ConflictCode, conditional.Error!.Code);
            Assert.Equal("original", Encoding.UTF8.GetString(now.Value!));
            Assert.False(swift.Capabilities.Supports(StorageFeature.ConditionalUpdate));
            Assert.False(swift.Capabilities.Supports(StorageFeature.ConditionalDelete));
        }
        finally
        {
            await swift.DeleteAsync(name, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review A26: the backend's own upload checks the condition too
    [SwiftFact]
    public async Task A_Swift_upload_with_a_condition_is_checked_by_the_backend_itself()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Swift());
        var name = $"cond-{Guid.NewGuid():N}.txt";
        var first = (await storage.UploadBytesAsync(name, Encoding.UTF8.GetBytes("original"))).Value!;
        try
        {
            var wrong = await storage.UploadBytesAsync(name, Encoding.UTF8.GetBytes("x"), new StorageUploadOptions
            {
                Condition = new StorageMutationCondition { ExpectedETag = "0123" },
                PipelineApplied = true
            });
            var right = await storage.UploadBytesAsync(name, Encoding.UTF8.GetBytes("y"), new StorageUploadOptions
            {
                Condition = new StorageMutationCondition { ExpectedETag = first.ETag },
                PipelineApplied = true
            });

            Assert.Equal(StorageErrors.ConflictCode, wrong.Error?.Code);
            Assert.True(right.IsSuccess, right.Error?.ToString());
            Assert.Equal("y", Encoding.UTF8.GetString((await storage.DownloadBytesAsync(name)).Value!));
        }
        finally
        {
            await storage.DeleteAsync(name, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review A2 (contract C2), and a create-only COPY that used to fail with 304
    [SwiftFact]
    public async Task A_Swift_copy_is_pinned_to_the_source_read_and_a_create_only_copy_works()
    {
        await using var storage = await CloudEmulators.CreateAsync(CloudEmulators.Swift());
        var enforcement = (IStorageConditionEnforcementSource)storage;
        await PinnedCopyAndMoveAsync(storage, destinationConditions: false);
        var dir = $"create-{Guid.NewGuid():N}";
        await storage.UploadBytesAsync($"{dir}/a.txt", [1]);
        try
        {
            var created = await storage.CopyAsync($"{dir}/a.txt", $"{dir}/b.txt", new StorageTransferOptions { Overwrite = false });
            var again = await storage.CopyAsync($"{dir}/a.txt", $"{dir}/b.txt", new StorageTransferOptions { Overwrite = false });

            Assert.True(created.IsSuccess, created.Error?.ToString());
            Assert.Equal(StorageErrors.ConflictCode, again.Error?.Code);
            Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, await enforcement.GetEnforcementAsync(StorageConditionKind.CreateOnly, true, default));
            Assert.Equal(StorageConditionEnforcement.Atomic, await enforcement.GetEnforcementAsync(StorageConditionKind.CreateOnly, false, default));
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review B19: versioned Swift containers: items carry VersionId, so reads by version must work
    [SwiftFact]
    public async Task A_versioned_Swift_container_serves_sync_copies_and_cross_connection_moves()
    {
        var container = "cl-test-versioned";
        await EnableSwiftVersioningAsync(container);
        var local = Path.Combine(Path.GetTempPath(), "cl-storage-live", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        await using var live = await LiveLibrary.StartAsync(
            ("swift", CloudEmulators.Swift(c => c.Container = container)),
            ("local", new LocalConnectionConfig { RootPath = local }));
        var swift = live.Library.GetStorage("swift");
        var dir = $"versioned-{Guid.NewGuid():N}";
        try
        {
            await swift.UploadBytesAsync($"{dir}/a.txt", Encoding.UTF8.GetBytes("one"));
            var second = (await swift.UploadBytesAsync($"{dir}/a.txt", Encoding.UTF8.GetBytes("two"))).Value!;
            await swift.UploadBytesAsync($"{dir}/b.txt", Encoding.UTF8.GetBytes("bee"));
            Assert.NotNull(second.VersionId);

            var synced = await live.Library.SyncAsync("swift", dir, "local", "synced", new CL.Storage.Sync.StorageSyncOptions { Direction = CL.Storage.Sync.StorageSyncDirection.Update });
            var moved = await live.Library.MoveAsync("swift", $"{dir}/b.txt", "local", "moved/b.txt");
            var old = await swift.DownloadBytesAsync($"{dir}/a.txt", new StorageDownloadOptions { VersionId = second.VersionId });

            Assert.True(synced.IsSuccess, synced.Error?.ToString());
            Assert.Empty(synced.Value!.Failed);
            Assert.Equal("two", File.ReadAllText(Path.Combine(local, "synced", "a.txt")));
            Assert.Equal(StorageTransferOutcome.Completed, moved.Outcome);
            Assert.Equal("bee", File.ReadAllText(Path.Combine(local, "moved", "b.txt")));
            Assert.Equal("two", Encoding.UTF8.GetString(old.Value!));
        }
        finally
        {
            await swift.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
            try { Directory.Delete(local, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task EnableSwiftVersioningAsync(string container)
    {
        var config = CloudEmulators.Swift();
        using var http = new HttpClient();
        using var auth = new HttpRequestMessage(HttpMethod.Get, config.AuthenticationUrl);
        auth.Headers.Add("X-Auth-User", config.Username);
        auth.Headers.Add("X-Auth-Key", config.Password);
        using var authResponse = await http.SendAsync(auth);
        authResponse.EnsureSuccessStatusCode();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{authResponse.Headers.GetValues("X-Storage-Url").First().TrimEnd('/')}/{container}");
        put.Headers.Add("X-Auth-Token", authResponse.Headers.GetValues("X-Auth-Token").First());
        put.Headers.Add("X-Versions-Enabled", "true");
        using var response = await http.SendAsync(put);
        response.EnsureSuccessStatusCode();
    }

    // needs-review A3 (acceptance R3-8): a folder moved onto an existing folder must not destroy it
    [SftpFact]
    public async Task R3_8_an_SFTP_folder_moved_onto_an_existing_folder_keeps_what_the_folder_held()
    {
        await using var live = await LiveLibrary.StartAsync(("sftp", LiveServers.Sftp()));
        var sftp = live.Library.GetStorage("sftp");
        var dir = $"accept-{Guid.NewGuid():N}";
        try
        {
            await sftp.UploadBytesAsync($"{dir}/from/x.txt", Encoding.UTF8.GetBytes("x"));
            await sftp.UploadBytesAsync($"{dir}/to/keep.txt", Encoding.UTF8.GetBytes("keep"));

            var moved = await live.Library.MoveAsync("sftp", $"{dir}/from", "sftp", $"{dir}/to");
            var kept = await sftp.ExistsAsync($"{dir}/to/keep.txt");

            Assert.True(kept.IsSuccess && kept.Value, $"move outcome {moved.Outcome}: the existing folder's file is gone");
            Assert.Equal("keep", Encoding.UTF8.GetString((await sftp.DownloadBytesAsync($"{dir}/to/keep.txt")).Value!));
        }
        finally
        {
            await sftp.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review A3: the backend itself refuses to replace a directory, for a folder or a file source
    [SftpFact]
    public async Task An_SFTP_move_onto_an_existing_directory_is_refused() =>
        await DirectoryDestinationIsKeptAsync(LiveServers.Create(LiveServers.Sftp()));

    // needs-review A3
    [FtpFact]
    public async Task An_FTP_move_onto_an_existing_directory_is_refused() =>
        await DirectoryDestinationIsKeptAsync(LiveServers.Create(LiveServers.Ftp()));

    // needs-review A3
    [WebDavFact]
    public async Task A_WebDAV_move_or_copy_onto_an_existing_collection_is_refused() =>
        await DirectoryDestinationIsKeptAsync(LiveServers.Create(LiveServers.WebDav()), copyToo: true);

    private static async Task DirectoryDestinationIsKeptAsync(IStorageBackend backend, bool copyToo = false)
    {
        await using var storage = backend;
        var dir = $"dirdest-{Guid.NewGuid():N}";
        try
        {
            Assert.True((await storage.UploadBytesAsync($"{dir}/from/x.txt", [1])).IsSuccess);
            Assert.True((await storage.UploadBytesAsync($"{dir}/file.txt", [2])).IsSuccess);
            Assert.True((await storage.UploadBytesAsync($"{dir}/to/keep.txt", [3])).IsSuccess);

            var folder = await storage.MoveAsync($"{dir}/from", $"{dir}/to", new StorageTransferOptions { Overwrite = true });
            var file = await storage.MoveAsync($"{dir}/file.txt", $"{dir}/to", new StorageTransferOptions { Overwrite = true });

            Assert.Equal(StorageErrors.ConflictCode, folder.Error?.Code);
            Assert.Equal(StorageErrors.ConflictCode, file.Error?.Code);
            if (copyToo)
            {
                var copied = await storage.CopyAsync($"{dir}/from", $"{dir}/to", new StorageTransferOptions { Overwrite = true });
                Assert.Equal(StorageErrors.ConflictCode, copied.Error?.Code);
            }
            Assert.Equal([3], (await storage.DownloadBytesAsync($"{dir}/to/keep.txt")).Value!);
            Assert.True((await storage.ExistsAsync($"{dir}/from/x.txt")).Value);
            Assert.True((await storage.ExistsAsync($"{dir}/file.txt")).Value);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review A11: a replacement that committed but left its backup reports both
    [SftpFact]
    public async Task An_SFTP_replace_whose_backup_cannot_be_removed_reports_the_destination_committed_and_the_backup()
    {
        var config = LiveServers.Sftp();
        await using var storage = new CL.Storage.Providers.Sftp.SftpStorageBackend(
            "live-sftp-backup",
            () => CL.Storage.Providers.Sftp.SftpStorageBackendFactory.CreateClient(config),
            config.Root,
            64L << 20,
            config.Session,
            config.Retry,
            observer: null)
        {
            // A directory where the backup should be deleted: removing it as a file fails.
            BeforeBackupDelete = async (client, backup) =>
            {
                await client.DeleteFileAsync(backup, default);
                await client.CreateDirectoryAsync(backup, default);
                await client.CreateDirectoryAsync(backup + "/inside", default);
            }
        };
        var dir = $"backup-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/f.txt", [1]);

            var replaced = await storage.UploadBytesAsync($"{dir}/f.txt", [2]);

            Assert.True(StorageErrorInfo.DestinationCommitted(replaced.Error), replaced.Error?.ToString());
            Assert.True(StorageErrorInfo.TryGetDetail(replaced.Error, StorageErrorInfo.LeftBehindKey, out var left));
            Assert.StartsWith($"{dir}/.cl-storage-backup-", left, StringComparison.Ordinal);
            Assert.Equal([2], (await storage.DownloadBytesAsync($"{dir}/f.txt")).Value!);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review B22: SFTP appends use the server's append flag, so concurrent appenders both land
    [SftpFact]
    public async Task Concurrent_SFTP_appends_do_not_overwrite_each_other()
    {
        // Two connections of two sessions each (the keep-alive only keeps them from sharing one pool): enough to append
        // concurrently without tripping the server's MaxStartups.
        await using var first = LiveServers.Create(LiveServers.Sftp(c => c.Session = new StorageSessionConfig { MaxSessions = 2, MaxIdleSessions = 2 }));
        await using var second = LiveServers.Create(LiveServers.Sftp(c => c.Session = new StorageSessionConfig { MaxSessions = 2, MaxIdleSessions = 2, KeepAliveSeconds = 1 }));
        var path = $"append-{Guid.NewGuid():N}.log";
        try
        {
            await first.UploadBytesAsync(path, Encoding.UTF8.GetBytes("start\n"));
            var tasks = Enumerable.Range(0, 20).Select(i =>
            {
                var storage = (IStorageAppendService)(i % 2 == 0 ? first : second);
                return storage.AppendAsync(path, new MemoryStream(Encoding.UTF8.GetBytes($"line {i:00}\n")));
            }).ToArray();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.ToString()));
            var text = Encoding.UTF8.GetString((await first.DownloadBytesAsync(path)).Value!);
            Assert.Equal(21, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            for (var i = 0; i < 20; i++) Assert.Contains($"line {i:00}", text);
        }
        finally
        {
            await first.DeleteAsync(path, new StorageDeleteOptions { IgnoreMissing = true });
        }
    }

    // needs-review B17: an SFTP copy on a connection limited to one session no longer waits for a second
    [SftpFact]
    public async Task An_SFTP_copy_works_with_one_session_and_the_replace_keeps_its_own_backup()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c => c.Session = new StorageSessionConfig { MaxSessions = 1, MaxIdleSessions = 1, AcquireTimeoutSeconds = 5 }));
        var dir = $"onesession-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/a.txt", [1, 2, 3]);
            await storage.UploadBytesAsync($"{dir}/b.txt", [9]);

            var copied = await storage.CopyAsync($"{dir}/a.txt", $"{dir}/b.txt", new StorageTransferOptions { Overwrite = true });

            Assert.True(copied.IsSuccess, copied.Error?.ToString());
            Assert.Equal([1, 2, 3], (await storage.DownloadBytesAsync($"{dir}/b.txt")).Value!);
            Assert.IsAssignableFrom<CL.Storage.Providers.IStorageRestoringReplace>(storage);
            var left = (await storage.ListAsync(dir, new StorageListOptions { IncludeInternal = true })).Value!.Items.Select(item => item.Name).Order();
            Assert.Equal(["a.txt", "b.txt"], left);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    private static CL.Storage.Providers.Ftp.FtpStorageBackend FtpWith(Action<FluentFTP.AsyncFtpClient>? configure = null, Func<FluentFTP.AsyncFtpClient, string, Task>? beforeBackupDelete = null)
    {
        var config = LiveServers.Ftp();
        return new CL.Storage.Providers.Ftp.FtpStorageBackend(
            "live-ftp-hooked",
            () =>
            {
                var client = CL.Storage.Providers.Ftp.FtpStorageBackendFactory.CreateClient(config);
                configure?.Invoke(client);
                return client;
            },
            config.Root,
            64L << 20,
            config.Session,
            config.Retry,
            observer: null)
        {
            BeforeBackupDelete = beforeBackupDelete
        };
    }

    // needs-review A11: an FTP replacement that committed but left its backup reports both
    [FtpFact]
    public async Task An_FTP_replace_whose_backup_cannot_be_removed_reports_the_destination_committed_and_the_backup()
    {
        await using var storage = FtpWith(beforeBackupDelete: async (client, backup) =>
        {
            await client.DeleteFile(backup);
            await client.CreateDirectory(backup + "/inside", true);
        });
        var dir = $"ftpbackup-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/f.txt", [1]);

            var replaced = await storage.UploadBytesAsync($"{dir}/f.txt", [2]);

            Assert.True(StorageErrorInfo.DestinationCommitted(replaced.Error), replaced.Error?.ToString());
            Assert.True(StorageErrorInfo.TryGetDetail(replaced.Error, StorageErrorInfo.LeftBehindKey, out var left));
            Assert.StartsWith($"{dir}/.cl-storage-backup-", left, StringComparison.Ordinal);
            Assert.Equal([2], (await storage.DownloadBytesAsync($"{dir}/f.txt")).Value!);
            Assert.IsAssignableFrom<CL.Storage.Providers.IStorageRestoringReplace>(storage);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }

    // needs-review B62: a hidden name is found without listing its folder, and a staged upload lists nothing
    [FtpFact]
    public async Task FTP_hidden_names_are_found_without_listing_the_folder()
    {
        var listings = 0;
        await using var storage = FtpWith(client => client.LegacyLogger = (_, message) =>
        {
            if (message.Contains("LIST", StringComparison.Ordinal) || message.Contains("MLSD", StringComparison.Ordinal))
                Interlocked.Increment(ref listings);
        });
        var dir = $"ftphidden-{Guid.NewGuid():N}";
        try
        {
            await storage.UploadBytesAsync($"{dir}/.hidden.txt", [1, 2, 3]);
            await storage.UploadBytesAsync($"{dir}/visible.txt", [4]);
            Interlocked.Exchange(ref listings, 0);

            // What a staged transfer does: probe a staging name, write it, and move it over the destination.
            var staging = $"{dir}/.cl-storage-transfer-{Guid.NewGuid():N}";
            var missing = await storage.GetInfoAsync(staging);
            var hidden = await storage.GetInfoAsync($"{dir}/.hidden.txt");
            var staged = await storage.UploadBytesAsync(staging, [5], new StorageUploadOptions { Overwrite = false });
            var promoted = await storage.MoveAsync(staging, $"{dir}/visible.txt", new StorageTransferOptions { Overwrite = true });

            Assert.Equal(StorageErrors.NotFoundCode, missing.Error?.Code);
            Assert.True(hidden.IsSuccess, hidden.Error?.ToString());
            Assert.Equal(3, hidden.Value!.Size);
            Assert.True(staged.IsSuccess, staged.Error?.ToString());
            Assert.True(promoted.IsSuccess, promoted.Error?.ToString());
            Assert.Equal([5], (await storage.DownloadBytesAsync($"{dir}/visible.txt")).Value!);
            Assert.Equal(0, listings);
        }
        finally
        {
            await storage.DeleteAsync(dir, new StorageDeleteOptions { Recursive = true, IgnoreMissing = true });
        }
    }
}
