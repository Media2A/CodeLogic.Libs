using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Events;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Queue;
using CodeLogic.Core.Events;
using CL.Storage.Registry;
using CL.Storage.Sync;
using CodeLogic.Core.Results;
using Xunit;
using static Storage.Tests.NeedsReviewTransferTests;

namespace Storage.Tests;

/// <summary>The last fixes before release (fixes.md, F1–F8).</summary>
public sealed class FinalFixesTests
{
    // ---------------------------------------------------------------- F1

    /// <summary>A destination whose read-back of <paramref name="path"/> fails <paramref name="failures"/> times once it exists.</summary>
    private static InterceptBackend ReadBackFails(string root, string path, int failures, Func<Error> error)
    {
        var local = Local(root);
        var left = failures;
        return new InterceptBackend(local)
        {
            GetInfo = async (candidate, token) =>
            {
                var info = await local.GetInfoAsync(candidate, token);
                if (candidate == path && info.IsSuccess && Interlocked.Decrement(ref left) >= 0)
                    return Result<StorageItem>.Failure(error());
                return info;
            }
        };
    }

    [Fact] // needs-review F1
    public async Task A_read_back_failure_after_a_committed_staged_upload_says_the_destination_is_committed()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("d");
        var destination = ReadBackFails(root, "f.bin", int.MaxValue, () => StorageErrors.Unavailable("read-back failed"));

        var result = await StorageTransferPipeline.StagedUploadAsync(
            destination, "f.bin", new MemoryStream(Content(100)), new StorageUploadOptions { Verify = true }, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.True(StorageErrorInfo.DestinationCommitted(result.Error));
        Assert.Equal(Content(100), await File.ReadAllBytesAsync(Path.Combine(root, "f.bin")));
    }

    [Fact] // needs-review F1
    public async Task A_transient_read_back_failure_after_a_committed_streamed_write_is_retried()
    {
        using var directory = new TestDirectory();
        var root = directory.CreateDirectory("d");
        var destination = ReadBackFails(root, "f.bin", 1, () => StorageErrors.Timeout("read-back timed out"));

        var opened = await destination.OpenWriteAsync("f.bin", new StorageUploadOptions { Verify = true });
        Assert.True(opened.IsSuccess, opened.Error?.Message);
        await using var stream = opened.Value!;
        await stream.WriteAsync(Content(100));
        var committed = await stream.CommitAsync();

        Assert.True(committed.IsSuccess, committed.Error?.Message);
        Assert.Equal(100, committed.Value!.Size);
    }

    [Fact] // needs-review F1
    public async Task A_read_back_failure_after_a_committed_transfer_reports_the_destination_committed()
    {
        var (library, directory, a, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        await File.WriteAllBytesAsync(Path.Combine(a, "f.bin"), Content(100));
        var root = directory.CreateDirectory("d");
        Assert.True(library.RegisterBackend("D", ReadBackFails(root, "g.bin", int.MaxValue, () => StorageErrors.Unavailable("read-back failed")).Named("D")).IsSuccess);

        var report = await library.CopyAsync("Default", "f.bin", "D", "g.bin", new StorageTransferOptions { Verify = true });

        Assert.True(report.DestinationCommitted, report.Error?.ToString());
        Assert.True(report.IsSuccess || StorageErrorInfo.DestinationCommitted(report.Error), report.Error?.ToString());
        Assert.Equal(Content(100), await File.ReadAllBytesAsync(Path.Combine(root, "g.bin")));
    }

    // ---------------------------------------------------------------- F2

    [Fact] // needs-review F2
    public async Task A_connection_test_step_that_throws_reports_the_exception_type_not_its_message()
    {
        using var directory = new TestDirectory();
        var factory = new FakeStorageBackendFactory((id, _) => new FakeStorageBackend(id,
            health: _ => throw new InvalidOperationException("sftp://admin:hunter2@secret-host/private")));
        using var library = new global::CL.Storage.StorageLibrary([factory]);

        var report = await library.TestConnectionAsync(new LocalConnectionConfig { RootPath = directory.Path });

        Assert.False(report.Succeeded);
        Assert.DoesNotContain("hunter2", report.Error!.Message + report.Error.Details);
        Assert.All(report.Steps, step => Assert.DoesNotContain("hunter2", step.Error?.Message ?? string.Empty));
    }

    [Fact] // needs-review F2
    public async Task Connection_settings_that_cannot_be_applied_report_the_exception_type_not_its_message()
    {
        using var directory = new TestDirectory();
        var factory = new FakeStorageBackendFactory((_, _) => throw new FormatException("key material hunter2 is malformed"));
        using var library = new global::CL.Storage.StorageLibrary([factory]);

        var report = await library.TestConnectionAsync(new LocalConnectionConfig { RootPath = directory.Path });

        Assert.False(report.Succeeded);
        Assert.Equal(StorageErrors.InvalidContentCode, report.Error!.Code);
        Assert.DoesNotContain("hunter2", report.Error.Message + report.Error.Details);
    }

    [Fact] // needs-review F2
    public async Task Invalid_settings_give_invalid_content_from_both_the_test_and_the_registration()
    {
        var (library, directory, _, _) = await TwoConnectionsAsync();
        using var _l = library; using var _d = directory;
        var invalid = new SftpConnectionConfig { Host = "", Username = "" };

        var tested = await library.TestConnectionAsync(invalid);
        var added = await library.AddOrUpdateConnectionAsync("S", invalid, persist: false);
        var local = await library.AddOrUpdateConnectionAsync("L", new LocalConnectionConfig { RootPath = "" }, persist: false);

        Assert.Equal(StorageErrors.InvalidContentCode, tested.Error!.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, added.Error!.Code);
        Assert.Equal(StorageErrors.InvalidContentCode, local.Error!.Code);
    }

    [Theory] // needs-review F2
    [InlineData("ftp")]
    [InlineData("sftp")]
    public async Task A_connection_test_uses_its_own_session_pool_and_leaves_none_behind(string provider)
    {
        StorageConnectionConfigBase settings = provider == "ftp"
            ? new FtpConnectionConfig
            {
                Host = "127.0.0.1", Port = 1, Username = "u", Password = "p", EncryptionMode = StorageFtpEncryptionMode.None,
                TimeoutSeconds = 5, Retry = new StorageRetryConfig { RetryCount = 0 }, Session = new StorageSessionConfig { LingerSeconds = 600 }
            }
            : new SftpConnectionConfig
            {
                Host = "127.0.0.1", Port = 1, Username = "u", Password = "p", AutoAcceptHostKey = true,
                TimeoutSeconds = 5, Retry = new StorageRetryConfig { RetryCount = 0 }, Session = new StorageSessionConfig { LingerSeconds = 600 }
            };
        // The key of the copy the library works from, as a registration of the same settings would have.
        var key = ProviderSettingsKey.For(System.Text.Json.JsonSerializer.Deserialize(
            System.Text.Json.JsonSerializer.Serialize(settings, settings.GetType()), settings.GetType())!);
        using var library = new global::CL.Storage.StorageLibrary();

        var report = await library.TestConnectionAsync(settings);

        Assert.False(report.Succeeded);
        Assert.Equal(["validate", "connect"], report.Steps.Select(step => step.Name));
        Assert.False(SharedResources.Holds(key));
    }

    // ---------------------------------------------------------------- F3

    [Fact] // needs-review F3
    public async Task The_condition_query_for_an_upload_answers_what_a_conditional_upload_on_MinIO_really_does()
    {
        // MinIO: conditions are enforced on PutObject and ignored on CopyObject.
        var minio = ProviderFakeS3.Create(enforceCopyConditions: false, enforceDeleteCondition: false);
        var existing = minio.Put("f.bin", [1]);
        await using var backend = new S3StorageBackend("minio", minio.Client, "bucket");

        var answered = await backend.GetConditionEnforcementAsync(StorageConditionKind.MatchVersion);
        var uploaded = await backend.UploadAsync("f.bin", new MemoryStream([2]), new StorageUploadOptions
        {
            Condition = new StorageMutationCondition { ExpectedETag = existing.ETag }
        });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        // The upload was staged and promoted with a copy, so its condition was checked just before the commit.
        var staged = minio.Copies.Any(copy => copy.DestinationKey == "f.bin");
        Assert.Equal(staged ? StorageConditionEnforcement.CheckedBeforeCommit : StorageConditionEnforcement.Atomic, answered);
        Assert.True(staged);
    }

    // ---------------------------------------------------------------- F4

    /// <summary>A stream that fails with a dropped connection after <paramref name="limit"/> bytes.</summary>
    private sealed class FailingAfter(Stream inner, int limit) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit) throw new IOException("The connection dropped.");
            var read = inner.Read(buffer, offset, Math.Min(count, limit - _read));
            _read += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Theory] // needs-review F4
    [InlineData(40)]
    [InlineData(100)]
    public async Task A_resumed_download_does_not_trust_a_local_file_it_did_not_leave(int localLength)
    {
        using var directory = new TestDirectory();
        var storage = Local(directory.CreateDirectory("remote"));
        await storage.UploadBytesAsync("f.bin", Content(100));
        var target = Path.Combine(directory.CreateDirectory("local"), "f.bin");
        await File.WriteAllBytesAsync(target, Enumerable.Repeat((byte)9, localLength).ToArray());

        var result = await storage.DownloadToFileAsync("f.bin", target, conflictPolicy: StorageConflictPolicy.Resume);

        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(Content(100), await File.ReadAllBytesAsync(target));
    }

    [Fact] // needs-review F4
    public async Task An_interrupted_download_resumes_only_while_the_remote_is_the_version_it_started_from()
    {
        using var directory = new TestDirectory();
        var local = Local(directory.CreateDirectory("remote"));
        await local.UploadBytesAsync("f.bin", Content(100));
        var offsets = new List<long>();
        var drop = true;
        var storage = new InterceptBackend(local)
        {
            Download = async (path, options, token) =>
            {
                offsets.Add(options?.Offset ?? 0);
                var opened = await local.DownloadAsync(path, options, token);
                if (!drop || opened.IsFailure) return opened;
                drop = false;
                return Result<Stream>.Success(new FailingAfter(opened.Value!, 30));
            }
        };
        var target = Path.Combine(directory.CreateDirectory("local"), "f.bin");

        var failed = await storage.DownloadToFileAsync("f.bin", target, conflictPolicy: StorageConflictPolicy.Resume);
        var resumed = await storage.DownloadToFileAsync("f.bin", target, conflictPolicy: StorageConflictPolicy.Resume);

        Assert.True(failed.IsFailure);
        Assert.True(resumed.IsSuccess, resumed.Error?.ToString());
        Assert.Equal([0L, 30L], offsets);
        Assert.Equal(Content(100), await File.ReadAllBytesAsync(target));
        Assert.Equal(["f.bin"], Directory.GetFiles(Path.GetDirectoryName(target)!).Select(Path.GetFileName));

        // Interrupted again, then the remote is replaced by another version of the same length: start over.
        drop = true;
        offsets.Clear();
        File.Delete(target);
        Assert.True((await storage.DownloadToFileAsync("f.bin", target, conflictPolicy: StorageConflictPolicy.Resume)).IsFailure);
        await local.UploadBytesAsync("f.bin", Enumerable.Repeat((byte)7, 100).ToArray());
        File.SetLastWriteTimeUtc(Path.Combine(directory.Path, "remote", "f.bin"), DateTime.UtcNow.AddMinutes(5));
        var restarted = await storage.DownloadToFileAsync("f.bin", target, conflictPolicy: StorageConflictPolicy.Resume);

        Assert.True(restarted.IsSuccess, restarted.Error?.ToString());
        Assert.Equal([0L, 0L], offsets);
        Assert.Equal(Enumerable.Repeat((byte)7, 100).ToArray(), await File.ReadAllBytesAsync(target));
    }

    // ---------------------------------------------------------------- F5

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>A library with a local <c>Destination</c> connection, and the directory it lives in.</summary>
    private static async Task<(global::CL.Storage.StorageLibrary Library, TestDirectory Directory, LocalStorageBackend Destination)> QueueLibraryAsync()
    {
        var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path, new EventBus());
        var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var destination = new LocalStorageBackend("Destination", new LocalConnectionConfig { RootPath = directory.CreateDirectory("dst") });
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
        return (library, directory, destination);
    }

    [Fact] // needs-review F5
    public async Task Removing_a_running_move_whose_copy_committed_keeps_the_job_for_reconciliation()
    {
        var (library, directory, destination) = await QueueLibraryAsync();
        using var _l = library; using var _d = directory;
        var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The source delete of the move never finishes on its own: the copy has committed when it is stopped.
        var sticky = new FakeStorageBackend(
            "Sticky",
            getInfo: (path, _) => Task.FromResult(Result<StorageItem>.Success(new StorageItem { Path = path, Name = path, ItemType = StorageItemType.File, Size = 1 })),
            downloadWithOptions: (_, _, _) => Task.FromResult(Result<Stream>.Success(new MemoryStream([1]))),
            delete: async (_, token) =>
            {
                deleting.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Result.Success();
            });
        Assert.True(library.RegisterBackend("Sticky", sticky).IsSuccess);
        var store = new InMemoryStorageTransferJobStore();
        var opened = await library.OpenTransferQueueAsync(new StorageTransferQueueOptions { Store = store });
        await using var queue = opened.Value!;

        var job = (await queue.EnqueueMoveAsync("Sticky", "a.bin", "Destination", "a.bin")).Value!;
        await deleting.Task.WaitAsync(Wait);
        var removed = await queue.RemoveAsync(job.Id);
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.Equal(StorageErrors.ConflictCode, removed.Error?.Code);
        Assert.Equal(StorageTransferState.NeedsReconciliation, queue.Get(job.Id)?.State);
        Assert.Equal(StorageTransferState.NeedsReconciliation, (await store.GetAsync(job.Id, default))?.State);
        Assert.True((await destination.ExistsAsync("a.bin")).Value);
    }

    // ---------------------------------------------------------------- F6

    /// <summary>An in-memory job store whose claims and saves can be made to throw.</summary>
    private sealed class FailingStore : IStorageTransferJobStore
    {
        public InMemoryStorageTransferJobStore Inner { get; } = new();
        public Func<bool> ClaimThrows { get; set; } = () => false;
        public Func<StorageTransferJobRecord, bool> SaveThrows { get; set; } = _ => false;
        public int Claims;

        public Task<IReadOnlyList<StorageTransferJobRecord>> LoadAsync(CancellationToken cancellationToken) => Inner.LoadAsync(cancellationToken);
        public Task<StorageTransferJobRecord?> AddAsync(StorageTransferJobRecord record, CancellationToken cancellationToken) => Inner.AddAsync(record, cancellationToken);
        public Task<StorageTransferJobRecord?> GetAsync(string jobId, CancellationToken cancellationToken) => Inner.GetAsync(jobId, cancellationToken);
        public Task<StorageTransferLease?> TryClaimAsync(string jobId, string workerId, long expectedRevision, TimeSpan duration, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Claims);
            if (ClaimThrows()) throw new IOException("The job store is down.");
            return Inner.TryClaimAsync(jobId, workerId, expectedRevision, duration, cancellationToken);
        }
        public Task<StorageTransferLease?> RenewAsync(StorageTransferLease lease, TimeSpan duration, CancellationToken cancellationToken) => Inner.RenewAsync(lease, duration, cancellationToken);
        public Task<StorageTransferJobRecord?> SaveAsync(StorageTransferJobRecord record, StorageTransferLease? lease, bool releaseLease, CancellationToken cancellationToken)
        {
            if (SaveThrows(record)) throw new IOException("The job store is down.");
            return Inner.SaveAsync(record, lease, releaseLease, cancellationToken);
        }
        public Task<bool> ReleaseAsync(StorageTransferLease lease, CancellationToken cancellationToken) => Inner.ReleaseAsync(lease, cancellationToken);
        public Task<bool> RemoveAsync(string jobId, long expectedRevision, StorageTransferLease? lease, CancellationToken cancellationToken) =>
            Inner.RemoveAsync(jobId, expectedRevision, lease, cancellationToken);
    }

    [Fact] // needs-review F6
    public async Task A_job_whose_claim_always_throws_fails_after_the_store_failure_limit()
    {
        var (library, directory, destination) = await QueueLibraryAsync();
        using var _l = library; using var _d = directory;
        await destination.UploadBytesAsync("a.bin", [1]);
        var store = new FailingStore { ClaimThrows = () => true };
        var opened = await library.OpenTransferQueueAsync(new StorageTransferQueueOptions { Store = store, RetryBaseDelay = TimeSpan.Zero, RetryMaxDelay = TimeSpan.Zero });
        await using var queue = opened.Value!;

        var job = (await queue.EnqueueCopyAsync("Destination", "a.bin", "Destination", "b.bin")).Value!;
        var deadline = DateTime.UtcNow + Wait;
        while (queue.Get(job.Id)?.State != StorageTransferState.Failed && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(StorageTransferState.Failed, queue.Get(job.Id)?.State);
        Assert.Equal(StorageErrors.UnavailableCode, queue.Get(job.Id)?.Error?.Code);
        Assert.Equal(8, Volatile.Read(ref store.Claims));
    }

    [Fact] // needs-review F6
    public async Task A_job_requeued_after_a_store_failure_announces_its_retry_as_unavailable()
    {
        var events = new RecordingEventBus();
        var directory = new TestDirectory();
        using var _d = directory;
        var context = StorageLibraryTestSupport.CreateContext(directory.Path, events);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var destination = new LocalStorageBackend("Destination", new LocalConnectionConfig { RootPath = directory.CreateDirectory("dst") });
        Assert.True(library.RegisterBackend("Destination", destination).IsSuccess);
        await destination.UploadBytesAsync("a.bin", [1]);
        // The first phase the attempt records fails in the store, every time the queue tries to save it.
        var failed = 0;
        var store = new FailingStore
        {
            SaveThrows = record => record.State == StorageTransferState.Running && record.Checkpoint.Phase != StorageTransferPhase.NotStarted &&
                Interlocked.Increment(ref failed) <= 6
        };
        var opened = await library.OpenTransferQueueAsync(new StorageTransferQueueOptions { Store = store, RetryBaseDelay = TimeSpan.Zero, RetryMaxDelay = TimeSpan.Zero });
        await using var queue = opened.Value!;

        var job = (await queue.EnqueueCopyAsync("Destination", "a.bin", "Destination", "b.bin")).Value!;
        await queue.WaitForIdleAsync().WaitAsync(Wait);

        Assert.True(failed > 6);
        Assert.Equal(StorageTransferState.Completed, queue.Get(job.Id)?.State);
        var retrying = Assert.Single(events.Published.OfType<StorageTransferRetryingEvent>());
        Assert.Equal(StorageErrors.UnavailableCode, retrying.ErrorCode);
    }

    // ---------------------------------------------------------------- F7

    [Fact] // needs-review F7
    public async Task Applying_a_plan_as_a_dry_run_reports_it_and_changes_nothing()
    {
        using var directory = new TestDirectory();
        var left = Local(directory.CreateDirectory("left"), "Left");
        var right = Local(directory.CreateDirectory("right"), "Right");
        await left.UploadBytesAsync("a.txt", [1]);
        var store = new InMemoryStorageSyncStateStore();
        var options = new StorageSyncOptions { Direction = StorageSyncDirection.TwoWay, StateStore = store, SyncId = "s" };
        var plan = await left.PlanSyncAsync("", right, "", options);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        Assert.NotEmpty(plan.Value!.Actions);

        var applied = await left.ApplySyncAsync("", right, "", plan.Value, plan.Value.Digest, options with { DryRun = true });

        Assert.True(applied.IsSuccess, applied.Error?.ToString());
        Assert.True(applied.Value!.DryRun);
        Assert.All(applied.Value.Results, result => Assert.Equal(StorageSyncActionOutcome.NotRun, result.Outcome));
        Assert.False(applied.Value.BaselineSaved);
        Assert.False((await right.ExistsAsync("a.txt")).Value);
        Assert.Null(await store.LoadAsync("s", default));
    }

    // ---------------------------------------------------------------- F8

    [Fact] // needs-review F8
    public async Task Two_writers_racing_for_a_part_file_where_create_only_is_checked_before_the_write_do_not_both_get_it()
    {
        using var directory = new TestDirectory();
        directory.CreateDirectory("d");
        var checkedBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var writers = 0;
        // Like FTP and SFTP: a create-only upload checks that the name is free, then writes, and the second of two
        // racing writers overwrites the first one's marker a moment later.
        InterceptBackend Server(LocalStorageBackend local) => new(local)
        {
            CapabilitiesMap = capabilities => new StorageCapabilities(capabilities.Features & ~StorageFeature.ConditionalCreate),
            Upload = async (path, source, options, token) =>
            {
                if (!path.EndsWith(".lock", StringComparison.Ordinal) || options?.Overwrite != false)
                    return await local.UploadAsync(path, source, options, token);
                if ((await local.ExistsAsync(path, token)).Value)
                    return Result<StorageItem>.Failure(StorageErrors.Conflict("exists"));
                if (Interlocked.Increment(ref checks) == 2) checkedBoth.TrySetResult();
                await checkedBoth.Task.WaitAsync(Wait, token);
                if (Interlocked.Increment(ref writers) == 2) await Task.Delay(200, token);
                return await local.UploadAsync(path, source, options with { Overwrite = true }, token);
            }
        };
        // Two processes: the second reaches the same part file through another root, so the in-process table
        // does not turn it away before it reaches the marker.
        var one = Server(Local(Path.Combine(directory.Path, "d")));
        var other = Server(Local(directory.Path));

        var results = await Task.WhenAll(
            Task.Run(() => PartLease.AcquireAsync(one, ".cl-storage-part-x", createParents: true, CancellationToken.None)),
            Task.Run(() => PartLease.AcquireAsync(other, "d/.cl-storage-part-x", createParents: true, CancellationToken.None)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.ToString()));
        Assert.Single(results, result => result.Value is not null);
    }
}
