using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using CL.Storage.Providers.S3;
using CL.Storage.Providers.WebDav;
using CodeLogic.Core.Results;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;
using Xunit;

namespace Storage.Tests;

/// <summary>Needs-review round 4, providers: folder deletes, S3 condition enforcement, WebDAV uploads, TLS diagnosis, and smaller provider fixes.</summary>
public sealed class NeedsReviewRound4ProviderTests
{
    private const int PartSize = 5 * 1024 * 1024;

    // ---------------------------------------------------------------- WebDAV

    /// <summary>A WebDAV client whose PROPFIND answers come from a table, and whose uploads are accepted.</summary>
    public class DavClient : DispatchProxy
    {
        public Dictionary<string, Item[]> Listings { get; } = new(StringComparer.Ordinal);
        public List<string> Uploaded { get; } = [];
        public List<string> DeletedFolders { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var path = args is { Length: > 0 } ? args[0] as string : null;
            return targetMethod!.Name switch
            {
                nameof(IClient.List) => Task.FromResult<IEnumerable<Item>>(Listings.TryGetValue(path!.TrimEnd('/') is { Length: > 0 } key ? key : "/", out var items)
                    ? items
                    : throw new WebDAVException(404, "not found")),
                nameof(IClient.GetFolder) or nameof(IClient.GetFile) => Task.FromException<Item>(new WebDAVException(404, "not found")),
                nameof(IClient.Upload) => Accept(path + args![2]),
                nameof(IClient.DeleteFolder) => Done(targetMethod, () => DeletedFolders.Add(path!)),
                nameof(IClient.DeleteFile) => Done(targetMethod, () => { }),
                "get_CustomHeaders" => null,
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }

        private Task<bool> Accept(string path)
        {
            Uploaded.Add(path);
            return Task.FromResult(true);
        }

        private static object Done(MethodInfo method, Action action)
        {
            action();
            return method.ReturnType == typeof(Task<bool>) ? Task.FromResult(true) : Task.CompletedTask;
        }
    }

    /// <summary>Answers each HTTP method from a table and records the requests.</summary>
    private sealed class DavHttp : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Answers { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Answers.TryGetValue(request.Method.Method, out var answer) ? answer(request) : new HttpResponseMessage(HttpStatusCode.Created));
        }
    }

    private static Item Folder(string href) => new() { Href = href, IsCollection = true };
    private static Item File(string href) => new() { Href = href, IsCollection = false, ContentLength = 1 };

    private static (WebDavStorageBackend Backend, DavClient Client, DavHttp Http) Dav(bool ownsHttp = true)
    {
        var client = (DavClient)(object)DispatchProxy.Create<IClient, DavClient>();
        var http = new DavHttp();
        var backend = ownsHttp
            ? new WebDavStorageBackend("dav", (IClient)(object)client, null, "/", ownsClient: false, 1 << 20, retry: null, observer: null, new HttpClient(http), new Uri("http://dav.test"))
            : new WebDavStorageBackend("dav", (IClient)(object)client, null, "/", ownsClient: false, 1 << 20, retry: null, observer: null);
        return (backend, client, http);
    }

    private static HttpResponseMessage Members(params string[] hrefs) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent("<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\">" +
            string.Concat(hrefs.Select(href => $"<D:response><D:href>{href}</D:href><D:propstat><D:prop><D:resourcetype/></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>")) +
            "</D:multistatus>")
    };

    private static HttpResponseMessage Locked(string token)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Lock-Token", $"<{token}>");
        return response;
    }

    // needs-review R4-A5: a non-recursive delete of a WebDAV collection locks it and deletes it only under that lock
    [Fact]
    public async Task A_non_recursive_WebDAV_folder_delete_locks_the_collection_and_deletes_under_the_lock()
    {
        var (backend, client, http) = Dav();
        client.Listings["/"] = [Folder("/"), Folder("/empty/")];
        client.Listings["/empty"] = [Folder("/empty/")];
        http.Answers["LOCK"] = _ => Locked("opaquelocktoken:1");
        http.Answers["PROPFIND"] = _ => Members("/empty/");
        http.Answers["DELETE"] = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var deleted = await backend.DeleteAsync("empty", new StorageDeleteOptions { Recursive = false });

        Assert.True(deleted.IsSuccess, deleted.Error?.ToString());
        Assert.Equal(["LOCK", "PROPFIND", "DELETE"], http.Requests.Select(request => request.Method.Method));
        Assert.Equal("0", http.Requests[0].Headers.GetValues("Depth").Single());
        Assert.Equal("<http://dav.test/empty/> (<opaquelocktoken:1>)", http.Requests[2].Headers.GetValues("If").Single());
        Assert.Empty(client.DeletedFolders);
    }

    // needs-review R4-A5: a collection holding anything (hidden names too) is kept, and its lock released
    [Fact]
    public async Task A_non_recursive_WebDAV_delete_of_a_folder_holding_a_hidden_file_keeps_it_and_unlocks()
    {
        var (backend, client, http) = Dav();
        client.Listings["/"] = [Folder("/"), Folder("/dir/")];
        client.Listings["/dir"] = [Folder("/dir/"), File("/dir/.hidden")];
        http.Answers["LOCK"] = _ => Locked("opaquelocktoken:2");
        http.Answers["PROPFIND"] = _ => Members("/dir/", "/dir/.hidden");

        var deleted = await backend.DeleteAsync("dir", new StorageDeleteOptions { Recursive = false });

        Assert.Equal(StorageErrors.ConflictCode, deleted.Error?.Code);
        Assert.Equal(["LOCK", "PROPFIND", "UNLOCK"], http.Requests.Select(request => request.Method.Method));
        Assert.Equal("<opaquelocktoken:2>", http.Requests[2].Headers.GetValues("Lock-Token").Single());
        Assert.Empty(client.DeletedFolders);
    }

    // needs-review R4-A5: a server without locks gets no DELETE at all: the folder is left, and the caller told why
    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task A_WebDAV_server_without_locks_never_gets_a_bare_DELETE_for_a_non_recursive_folder_delete(HttpStatusCode lockAnswer)
    {
        var (backend, client, http) = Dav();
        client.Listings["/"] = [Folder("/"), Folder("/empty/")];
        client.Listings["/empty"] = [Folder("/empty/")];
        http.Answers["LOCK"] = _ => new HttpResponseMessage(lockAnswer);

        var deleted = await backend.DeleteAsync("empty", new StorageDeleteOptions { Recursive = false });
        var (unowned, unownedClient, _) = Dav(ownsHttp: false);
        unownedClient.Listings["/"] = [Folder("/"), Folder("/empty/")];
        unownedClient.Listings["/empty"] = [Folder("/empty/")];
        var withoutHttp = await unowned.DeleteAsync("empty", new StorageDeleteOptions { Recursive = false });

        Assert.Equal(StorageErrors.UnsupportedCode, deleted.Error?.Code);
        Assert.DoesNotContain(http.Requests, request => request.Method == HttpMethod.Delete);
        Assert.Empty(client.DeletedFolders);
        Assert.Equal(StorageErrors.UnsupportedCode, withoutHttp.Error?.Code);
        Assert.Empty(unownedClient.DeletedFolders);
    }

    // needs-review R4-A8: an upload with Overwrite onto an existing collection is refused, never MOVEd with Overwrite: T
    [Fact]
    public async Task A_WebDAV_upload_with_overwrite_onto_a_collection_is_refused_without_a_MOVE()
    {
        var (backend, client, http) = Dav();
        client.Listings["/"] = [Folder("/"), Folder("/target/")];

        var uploaded = await backend.UploadBytesAsync("target", [1, 2, 3], new StorageUploadOptions { Overwrite = true });

        Assert.Equal(StorageErrors.ConflictCode, uploaded.Error?.Code);
        Assert.DoesNotContain(http.Requests, request => request.Method.Method == "MOVE");
    }

    // needs-review R4-A8: Overwrite: T is sent only when a file was found at the destination
    [Fact]
    public async Task A_WebDAV_upload_to_a_new_name_is_committed_with_Overwrite_F_even_when_overwrite_is_allowed()
    {
        var (backend, client, http) = Dav();
        client.Listings["/"] = [Folder("/"), File("/existing.txt")];

        await backend.UploadBytesAsync("new.txt", [1], new StorageUploadOptions { Overwrite = true });
        await backend.UploadBytesAsync("existing.txt", [2], new StorageUploadOptions { Overwrite = true });

        var moves = http.Requests.Where(request => request.Method.Method == "MOVE").ToList();
        Assert.Equal(2, moves.Count);
        Assert.Equal("F", moves[0].Headers.GetValues("Overwrite").Single());
        Assert.Equal("T", moves[1].Headers.GetValues("Overwrite").Single());
    }

    // ---------------------------------------------------------------- S3

    /// <summary>
    /// The shared fake S3 with the behaviours of other S3-compatible servers: headers it rejects with 400, PUT
    /// conditions it ignores, a probe that fails, cancellation honoured after a commit, and a version history.
    /// </summary>
    public class ServerFakeS3 : ProviderFakeS3
    {
        public bool RejectCopyConditions { get; set; }
        public bool RejectDeleteCondition { get; set; }
        public bool IgnorePutConditions { get; set; }
        public bool FailProbe { get; set; }
        public int ProbePuts;
        public Func<string, bool>? RefuseChecksumHead { get; set; }
        /// <summary>Commits, then answers as a request whose caller's token was cancelled meanwhile.</summary>
        public bool CancelAfterCommit { get; set; }
        public List<S3ObjectVersion> History { get; } = [];

        public static ServerFakeS3 New(bool enforceCopyConditions = true, bool enforceDeleteCondition = true)
        {
            var fake = (ServerFakeS3)(object)DispatchProxy.Create<IAmazonS3, ServerFakeS3>();
            fake.EnforceCopyConditions = enforceCopyConditions;
            fake.EnforceDeleteCondition = enforceDeleteCondition;
            return fake;
        }

        private static Task Rejected(MethodInfo method) =>
            (Task)typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0])
                .Invoke(null, [new AmazonS3Exception("fake 400") { StatusCode = HttpStatusCode.BadRequest, ErrorCode = "InvalidArgument" }])!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var request = args?.Length > 0 ? args[0] : null;
            var token = args?.Length > 1 && args[1] is CancellationToken given ? given : default;
            switch (request)
            {
                case CopyObjectRequest copy when RejectCopyConditions && (copy.IfNoneMatch is not null || copy.IfMatch is not null):
                    return Rejected(targetMethod!);
                case DeleteObjectRequest delete when RejectDeleteCondition && delete.IfMatch is not null:
                    return Rejected(targetMethod!);
                case PutObjectRequest put when put.Key.Contains(".cl-storage-probe-", StringComparison.Ordinal):
                    Interlocked.Increment(ref ProbePuts);
                    if (FailProbe) return Task.FromException<PutObjectResponse>(new AmazonS3Exception("fake 500") { StatusCode = HttpStatusCode.InternalServerError });
                    if (IgnorePutConditions) { put.IfNoneMatch = null; put.IfMatch = null; }
                    break;
                case PutObjectRequest put when IgnorePutConditions:
                    put.IfNoneMatch = null;
                    put.IfMatch = null;
                    break;
                case GetObjectMetadataRequest head when head.ChecksumMode == ChecksumMode.ENABLED && RefuseChecksumHead?.Invoke(head.Key) == true:
                    return Rejected(targetMethod!);
                case ListVersionsRequest list:
                    return Task.FromResult(new ListVersionsResponse { Versions = [.. History.Where(version => version.Key == list.Prefix).Reverse()] });
            }
            var result = base.Invoke(targetMethod, args);
            if (CancelAfterCommit && token.CanBeCanceled && request is CopyObjectRequest or CompleteMultipartUploadRequest && result is Task task)
                return Cancelled(task, token, targetMethod!);
            return result;
        }

        private static Task Cancelled(Task committed, CancellationToken token, MethodInfo method)
        {
            committed.GetAwaiter().GetResult();
            return token.IsCancellationRequested
                ? (Task)typeof(Task).GetMethod(nameof(Task.FromCanceled), 1, [typeof(CancellationToken)])!
                    .MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [token])!
                : committed;
        }

        public void Record(string key)
        {
            var item = Objects[key];
            History.Add(new S3ObjectVersion { Key = key, VersionId = item.VersionId, ETag = $"\"{item.ETag}\"", Size = item.Size });
        }
    }

    private static S3StorageBackend S3(IAmazonS3 client, long copyThreshold = 5L * 1024 * 1024 * 1024, string id = "s3") =>
        new(id, client, "bucket", multipartPartSizeBytes: PartSize, multipartThresholdBytes: PartSize)
        {
            MultipartCopyThresholdBytes = copyThreshold
        };

    private static bool IsProbe(string key) => key.Contains(".cl-storage-probe-", StringComparison.Ordinal);

    // needs-review R4-A6: conditions the probe found rejected are not sent; the existence check just before is the guard
    [Fact]
    public async Task S3_copy_conditions_the_server_rejects_are_not_sent_and_the_copy_is_checked_before_commit()
    {
        var fake = ServerFakeS3.New();
        fake.RejectCopyConditions = true;
        fake.Put("a.txt", [1]);
        fake.Put("taken.txt", [2]);
        await using var backend = S3(fake.Client);

        var created = await backend.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { Overwrite = false });
        var refused = await backend.CopyAsync("a.txt", "taken.txt", new StorageTransferOptions { Overwrite = false });
        var enforcement = await backend.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true);

        Assert.True(created.IsSuccess, created.Error?.ToString());
        Assert.Equal([1], fake.Objects["b.txt"].Content);
        Assert.Equal(StorageErrors.ConflictCode, refused.Error?.Code);
        Assert.Equal([2], fake.Objects["taken.txt"].Content);
        Assert.All(fake.Copies.Where(copy => !IsProbe(copy.DestinationKey)), copy => Assert.Null(copy.IfNoneMatch));
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, enforcement);
    }

    // needs-review R4-A6: a server that rejects If-Match on DeleteObject still completes a move (the delete is checked just before)
    [Fact]
    public async Task S3_move_on_a_server_rejecting_delete_conditions_deletes_the_source()
    {
        var fake = ServerFakeS3.New();
        fake.RejectDeleteCondition = true;
        fake.Put("a.txt", [1]);
        await using var backend = S3(fake.Client);

        var moved = await backend.MoveAsync("a.txt", "b.txt");

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.False(fake.Objects.ContainsKey("a.txt"));
        Assert.Equal([1], fake.Objects["b.txt"].Content);
        Assert.All(fake.Deletes.Where(delete => !IsProbe(delete.Key)), delete => Assert.Null(delete.IfMatch));
    }

    // needs-review R4-A6: a probe that cannot finish is kept, as not enforced, until its back-off ends
    [Fact]
    public async Task An_inconclusive_S3_probe_is_not_repeated_by_every_conditional_request_until_its_backoff_ends()
    {
        var fake = ServerFakeS3.New();
        fake.FailProbe = true;
        var now = DateTimeOffset.UtcNow;
        await using var backend = new S3StorageBackend("s3", fake.Client, "bucket") { Clock = () => now, InconclusiveProbeBackoff = TimeSpan.FromMinutes(1) };

        var first = await backend.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true);
        var second = await backend.GetConditionEnforcementAsync(StorageConditionKind.DeleteMatchVersion);
        var afterFirst = fake.ProbePuts;
        now += TimeSpan.FromMinutes(2);
        await backend.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true);
        var afterBackoff = fake.ProbePuts;
        now += TimeSpan.FromMinutes(1);
        await backend.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true);

        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, first);
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, second);
        Assert.Equal(1, afterFirst);
        Assert.Equal(2, afterBackoff);
        // The second failure doubles the back-off to two minutes.
        Assert.Equal(2, fake.ProbePuts);
    }

    // needs-review R4-A7: the per-connection answer is public, reaches the backend through the library's proxy, and
    // after the probe the capability flags name only what the server enforces
    [Fact]
    public async Task The_condition_enforcement_query_is_public_and_the_S3_flags_follow_the_probe()
    {
        using var directory = new TestDirectory();
        var context = StorageLibraryTestSupport.CreateContext(directory.Path);
        using var library = new global::CL.Storage.StorageLibrary();
        await StorageLibraryTestSupport.InitializeAsync(library, context, storage => storage.Enabled = false);
        var minio = ProviderFakeS3.Create(enforceCopyConditions: false, enforceDeleteCondition: false);
        var aws = ProviderFakeS3.Create();
        var registered = library.RegisterBackend("minio", S3(minio.Client, id: "minio"));
        Assert.True(registered.IsSuccess, registered.Error?.ToString());
        registered = library.RegisterBackend("aws", S3(aws.Client, id: "aws"));
        Assert.True(registered.IsSuccess, registered.Error?.ToString());
        var minioStorage = library.GetStorage("minio");
        var awsStorage = library.GetStorage("aws");

        var minioCopy = await minioStorage.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly, serverSideCopy: true);
        var minioDelete = await minioStorage.GetConditionEnforcementAsync(StorageConditionKind.DeleteMatchVersion);
        var minioUpload = await minioStorage.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly);
        var awsDelete = await awsStorage.GetConditionEnforcementAsync(StorageConditionKind.DeleteMatchVersion);

        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, minioCopy);
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, minioDelete);
        Assert.Equal(StorageConditionEnforcement.Atomic, minioUpload);
        Assert.Equal(StorageConditionEnforcement.Atomic, awsDelete);
        Assert.False(minioStorage.Capabilities.Supports(StorageFeature.ConditionalCreate));
        Assert.False(minioStorage.Capabilities.Supports(StorageFeature.ConditionalUpdate));
        Assert.False(minioStorage.Capabilities.Supports(StorageFeature.ConditionalDelete));
        Assert.True(awsStorage.Capabilities.Supports(StorageFeature.ConditionalCreate));
        Assert.True(awsStorage.Capabilities.Supports(StorageFeature.ConditionalUpdate));
        Assert.True(awsStorage.Capabilities.Supports(StorageFeature.ConditionalDelete));
    }

    // needs-review R4-B24: under Auto, an upload's condition is sent only where the probe saw it enforced
    [Fact]
    public async Task A_create_only_S3_upload_on_a_server_ignoring_PUT_conditions_is_checked_and_keeps_the_object()
    {
        var fake = ServerFakeS3.New();
        fake.IgnorePutConditions = true;
        fake.Put("taken.txt", [1]);
        await using var backend = S3(fake.Client);

        var created = await backend.UploadBytesAsync("taken.txt", [2], new StorageUploadOptions { Overwrite = false });
        var enforcement = await backend.GetConditionEnforcementAsync(StorageConditionKind.CreateOnly);

        Assert.Equal(StorageErrors.ConflictCode, created.Error?.Code);
        Assert.Equal([1], fake.Objects["taken.txt"].Content);
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, enforcement);
    }

    // needs-review R4-B4: a CopyObject whose caller cancelled after it committed is still a completed copy, and a move deletes its source
    [Fact]
    public async Task An_S3_move_cancelled_while_its_CopyObject_commits_completes()
    {
        var fake = ServerFakeS3.New();
        fake.CancelAfterCommit = true;
        fake.Put("a.txt", [1]);
        using var cancel = new CancellationTokenSource();
        fake.AfterCopy = _ => cancel.Cancel();
        await using var backend = S3(fake.Client);

        var moved = await backend.MoveAsync("a.txt", "b.txt", cancellationToken: cancel.Token);

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.False(fake.Objects.ContainsKey("a.txt"));
        Assert.Equal([1], fake.Objects["b.txt"].Content);
    }

    // needs-review R4-B4: a multipart copy cancelled while its completion commits is a completed copy
    [Fact]
    public async Task An_S3_multipart_copy_cancelled_while_it_completes_is_reported_complete()
    {
        var fake = ServerFakeS3.New();
        fake.CancelAfterCommit = true;
        var content = new byte[PartSize * 2 + 7];
        Random.Shared.NextBytes(content);
        fake.Put("big.bin", content);
        using var cancel = new CancellationTokenSource();
        fake.AfterComplete = _ => cancel.Cancel();
        await using var backend = S3(fake.Client, copyThreshold: PartSize);

        var copied = await backend.CopyAsync("big.bin", "copy.bin", cancellationToken: cancel.Token);

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        Assert.Equal(content, fake.Objects["copy.bin"].Content);
        Assert.Equal(0, fake.Aborts);
    }

    // needs-review R4-B4: a completion whose reply was lost is recognised by its version even when a newer one is on top
    [Fact]
    public async Task A_lost_S3_completion_is_found_among_the_versions_when_another_writer_is_already_on_top()
    {
        var fake = ServerFakeS3.New();
        fake.LoseCompletionReply = true;
        string? ours = null;
        fake.AfterComplete = key =>
        {
            fake.Record(key);
            ours = fake.Objects[key].VersionId;
            fake.Put(key, [7]);
            fake.Record(key);
        };
        await using var backend = S3(fake.Client);

        var uploaded = await backend.UploadBytesAsync("big.bin", new byte[PartSize + 17]);

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.Equal(ours, uploaded.Value!.VersionId);
        Assert.Equal([7], fake.Objects["big.bin"].Content);
    }

    // needs-review R4-B25: AWS refuses a HEAD of an SSE-C object without its key (400): no MD5, not a provider failure
    [Fact]
    public async Task An_SSE_C_object_whose_headers_S3_refuses_reports_no_MD5()
    {
        var fake = ServerFakeS3.New();
        fake.Put("secret.bin", [1, 2, 3]);
        fake.RefuseChecksumHead = key => key == "secret.bin";
        await using var backend = S3(fake.Client);

        var secret = await backend.GetServerChecksumAsync("secret.bin", StorageChecksumAlgorithm.Md5);

        Assert.Equal(StorageErrors.UnsupportedCode, secret.Error?.Code);
        Assert.Contains("SSE-C", secret.Error!.Message, StringComparison.Ordinal);
    }

    // needs-review R4-B28: a large server-side copy uses parts of at least 128 MiB, not the 16 MiB upload part size
    [Fact]
    public void A_6_GiB_S3_copy_uses_parts_of_at_least_128_MiB()
    {
        const long MiB = 1024 * 1024;
        var size = 6L * 1024 * MiB;

        var part = S3StorageBackend.MultipartCopyPartSize(size, 16 * MiB);
        var huge = S3StorageBackend.MultipartCopyPartSize(5L * 1024 * 1024 * MiB, 16 * MiB);

        Assert.True(part >= 128 * MiB, $"part size {part}");
        Assert.True((size + part - 1) / part <= 48);
        Assert.True(huge <= 5L * 1024 * MiB);
        Assert.True((5L * 1024 * 1024 * MiB + huge - 1) / huge <= 10_000);
    }

    // ---------------------------------------------------------------- TLS client-certificate diagnosis

    /// <summary>A transport whose reads return the scripted byte counts; -1 throws as a dropped connection does.</summary>
    private sealed class ScriptedStream(int[] reads) : Stream
    {
        private int _next;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var scripted = _next < reads.Length ? reads[_next++] : 0;
            if (scripted < 0) throw new IOException("dropped", new SocketException((int)SocketError.ConnectionReset));
            return Math.Min(scripted, count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    // needs-review R4-A10: a spare connection that read nothing after the certificate reply records no refusal when it is closed
    [Fact]
    public async Task An_idle_spare_connection_closed_after_the_certificate_reply_records_no_refusal()
    {
        var recorder = new ServerIdentityRecorder();
        var started = DateTimeOffset.UtcNow;
        var watch = new TlsConnectionWatch(new ScriptedStream([]), recorder);

        watch.CertificateRequested();
        await watch.WriteAsync(new byte[10]);
        await watch.DisposeAsync();

        Assert.Null(recorder.ClientCertificateRefusedAt);
        Assert.Equal(StorageErrors.ConnectionLostCode, TlsDiagnosis.Enrich(StorageErrors.ConnectionLost("lost"), recorder, started).Code);
    }

    // needs-review R4-A10: a refusal explains only the failure of the attempt that sent its request on that connection
    [Fact]
    public async Task A_refused_connection_explains_its_own_attempt_and_leaves_a_concurrent_drop_transient()
    {
        var recorder = new ServerIdentityRecorder();
        var policy = new ProviderRetryPolicy(new StorageRetryConfig { RetryCount = 0 }, "dav", StorageProvider.WebDav);
        policy.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, recorder, attempt);
        var otherStarted = new TaskCompletionSource();
        var refusalDone = new TaskCompletionSource();

        // A long transfer on another connection, running while the refusal happens, then dropping.
        var other = policy.ExecuteAsync<int>("Upload", RetryKind.Idempotent, async (_, _) =>
        {
            otherStarted.SetResult();
            await refusalDone.Task;
            return Result<int>.Failure(StorageErrors.ConnectionLost("Upload: the connection was lost."));
        }, CancellationToken.None);
        await otherStarted.Task;
        var refused = await policy.ExecuteAsync<int>("List", RetryKind.Idempotent, async (_, _) =>
        {
            var watch = new TlsConnectionWatch(new ScriptedStream([24]), recorder);
            watch.CertificateRequested();
            await watch.WriteAsync(new byte[10]); // the certificate reply
            await watch.WriteAsync(new byte[10]); // the request
            Assert.Equal(24, await watch.ReadAsync(new byte[1024])); // the alert
            await watch.DisposeAsync();
            return Result<int>.Failure(StorageErrors.ConnectionLost("List: the connection was lost."));
        }, CancellationToken.None);
        refusalDone.SetResult();
        var dropped = await other;

        Assert.Equal(StorageErrors.TlsFailureCode, refused.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(refused.Error, StorageErrorInfo.TlsReasonKey, out var reason));
        Assert.Equal(TlsDiagnosis.ClientCertificateRejected, reason);
        Assert.Equal(StorageErrors.ConnectionLostCode, dropped.Error?.Code);
        Assert.True(StorageErrorInfo.IsTransient(dropped.Error));
    }

    // needs-review R4-A10: a server that resets the connection instead of sending an alert (as the lab's mutual-TLS server
    // does on Windows) still counts, once a request was sent after the certificate reply; an idle connection reset does not
    [Fact]
    public async Task A_reset_after_a_request_on_a_refused_connection_counts_but_an_idle_reset_and_a_zero_byte_read_do_not()
    {
        var recorder = new ServerIdentityRecorder();
        var policy = new ProviderRetryPolicy(new StorageRetryConfig { RetryCount = 0 }, "dav", StorageProvider.WebDav);
        policy.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, recorder, attempt);

        var idle = await policy.ExecuteAsync<int>("Idle", RetryKind.Idempotent, async (_, _) =>
        {
            var spare = new TlsConnectionWatch(new ScriptedStream([0, -1]), recorder);
            spare.CertificateRequested();
            await spare.WriteAsync(new byte[10]); // the certificate reply only
            Assert.Equal(0, await spare.ReadAsync(Memory<byte>.Empty)); // SslStream waiting for data: not the end of the stream
            await Assert.ThrowsAsync<IOException>(async () => Assert.Equal(0, await spare.ReadAsync(new byte[1024])));
            await spare.DisposeAsync();
            return Result<int>.Failure(StorageErrors.ConnectionLost("Idle: the connection was lost."));
        }, CancellationToken.None);
        Assert.Null(recorder.ClientCertificateRefusedAt);

        var refused = await policy.ExecuteAsync<int>("Check", RetryKind.Idempotent, async (_, _) =>
        {
            var watch = new TlsConnectionWatch(new ScriptedStream([-1]), recorder);
            watch.CertificateRequested();
            await watch.WriteAsync(new byte[10]); // the certificate reply
            await watch.WriteAsync(new byte[10]); // the request
            await Assert.ThrowsAsync<IOException>(async () => Assert.Equal(0, await watch.ReadAsync(new byte[1024])));
            await watch.DisposeAsync();
            return Result<int>.Failure(StorageErrors.ConnectionLost("Check: the connection was lost."));
        }, CancellationToken.None);

        Assert.Equal(StorageErrors.ConnectionLostCode, idle.Error?.Code);
        Assert.Equal(StorageErrors.TlsFailureCode, refused.Error?.Code);
    }

    // needs-review R4-A10: an alert on a connection no request of this attempt used (a spare's close) is not this attempt's refusal
    [Fact]
    public async Task An_alert_on_a_connection_that_carried_no_request_of_the_attempt_leaves_its_drop_transient()
    {
        var recorder = new ServerIdentityRecorder();
        var policy = new ProviderRetryPolicy(new StorageRetryConfig { RetryCount = 0 }, "dav", StorageProvider.WebDav);
        policy.Enrich = (error, attempt) => TlsDiagnosis.Enrich(error, recorder, attempt);

        var result = await policy.ExecuteAsync<int>("List", RetryKind.Idempotent, async (_, _) =>
        {
            var spare = new TlsConnectionWatch(new ScriptedStream([24]), recorder);
            spare.CertificateRequested();
            await spare.WriteAsync(new byte[10]); // the certificate reply, then the pool keeps it idle
            Assert.Equal(24, await spare.ReadAsync(new byte[1024])); // the server's close_notify
            await spare.DisposeAsync();
            return Result<int>.Failure(StorageErrors.ConnectionLost("List: the connection was lost."));
        }, CancellationToken.None);

        Assert.Equal(StorageErrors.ConnectionLostCode, result.Error?.Code);
        Assert.True(StorageErrorInfo.IsTransient(result.Error));
    }

    // needs-review R4-A10: behind a proxy tunnel the TLS stream does not sit on the watched socket: nothing is recorded
    [Fact]
    public void A_TLS_stream_that_is_not_on_a_watched_connection_records_nothing()
    {
        var recorder = new ServerIdentityRecorder();
        using var tunnel = new SslStream(new MemoryStream());

        TlsConnectionWatch.OnCertificateSelection(tunnel, null, ["CN=issuer"]);

        Assert.Null(recorder.ClientCertificateRefusedAt);
        Assert.Equal(StorageErrors.ConnectionLostCode, TlsDiagnosis.Enrich(StorageErrors.ConnectionLost("lost"), recorder, ProviderAttempt.At(DateTimeOffset.UtcNow)).Code);
    }

    // ---------------------------------------------------------------- Client certificates, local provider

    // needs-review R4-B26: only a missing user key store falls back to the machine store; a wrong password does not
    [Fact]
    public void Only_an_unavailable_user_key_store_falls_back_to_machine_keys()
    {
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = certificate.Export(X509ContentType.Pkcs12, "right");
        var wrongPassword = Assert.ThrowsAny<CryptographicException>(() => ClientCertificates.Load(null, pfx, "wrong"));

        Assert.False(ClientCertificates.IsUserKeyStoreUnavailable(wrongPassword));
        Assert.True(ClientCertificates.IsUserKeyStoreUnavailable(new CryptographicException("The system cannot find the file specified.") { HResult = unchecked((int)0x80070002) }));
        Assert.True(ClientCertificates.IsUserKeyStoreUnavailable(new CryptographicException("Keyset does not exist") { HResult = unchecked((int)0x80090016) }));
    }

    // needs-review R4-B27: the case probe swaps ASCII letters only, so non-ASCII names cannot misjudge the file system
    [Fact]
    public void The_local_case_probe_uses_ASCII_letters_only()
    {
        using var directory = new TestDirectory();
        // .NET maps U+0131 (dotless i) to "I"; NTFS does not, so a probe on this name judged NTFS case-sensitive.
        System.IO.File.WriteAllBytes(Path.Combine(directory.Path, "ı"), [1]);

        Assert.Equal("ıXႠ", LocalStorageBackend.SwapCase("ıxႠ"));
        Assert.Equal("aBC", LocalStorageBackend.SwapCase("Abc"));
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), LocalStorageBackend.DetectCaseInsensitive(directory.Path));
    }

    // needs-review R4-B23: a weak ETag proves a change but never strongly matches, and the local pin compares weakly
    [Fact]
    public async Task Weak_ETags_never_match_strongly_and_the_local_pin_compares_them_weakly()
    {
        using var directory = new TestDirectory();
        await using var local = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path }, 1 << 20);
        await local.UploadBytesAsync("a.txt", [1]);
        var etag = (await local.GetInfoAsync("a.txt")).Value!.ETag!;

        var pinned = await local.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { ExpectedSourceETag = etag });
        var stale = await local.CopyAsync("a.txt", "c.txt", new StorageTransferOptions { ExpectedSourceETag = "W/\"0-0-1\"" });

        Assert.True(StorageETags.IsWeak(etag));
        Assert.False(StorageETags.StrongEquals(etag, etag));
        Assert.True(StorageETags.WeakEquals(etag, etag.Replace("W/", "", StringComparison.Ordinal)));
        Assert.True(StorageETags.StrongEquals("\"abc\"", "abc"));
        Assert.False(StorageETags.StrongEquals("abc", null));
        Assert.True(pinned.IsSuccess, pinned.Error?.ToString());
        Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
    }
}
