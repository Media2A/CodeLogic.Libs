using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.S3;
using CL.Storage.Registry;
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
}
