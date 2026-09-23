using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.S3;
using Xunit;

namespace Storage.Tests;

/// <summary>Needs-review round 3, S3 backend: copies, moves, conditions, and multipart completion.</summary>
public sealed class NeedsReviewProviderS3Tests
{
    private const int PartSize = 5 * 1024 * 1024;

    private static S3StorageBackend Backend(ProviderFakeS3 fake, long copyThreshold = 5L * 1024 * 1024 * 1024, S3ConditionalRequestSupport conditions = S3ConditionalRequestSupport.Auto) =>
        new("s3", fake.Client, "bucket", multipartPartSizeBytes: PartSize, multipartThresholdBytes: PartSize)
        {
            MultipartCopyThresholdBytes = copyThreshold,
            ConditionalRequests = conditions
        };

    private static ProviderFakeS3.FakeObject Decorated(ProviderFakeS3 fake, string key, byte[] content)
    {
        var item = fake.Put(key, content, "application/x-test");
        item.CacheControl = "max-age=60";
        item.ContentEncoding = "gzip";
        item.ContentDisposition = "attachment";
        item.StorageClass = "STANDARD_IA";
        item.Encryption = "aws:kms";
        item.KmsKeyId = "key-1";
        item.Metadata["x-amz-meta-color"] = "blue";
        item.Tags = [new Amazon.S3.Model.Tag { Key = "team", Value = "red" }];
        return item;
    }

    private static void AssertKeepsProperties(ProviderFakeS3.FakeObject copy)
    {
        Assert.Equal("application/x-test", copy.ContentType);
        Assert.Equal("max-age=60", copy.CacheControl);
        Assert.Equal("gzip", copy.ContentEncoding);
        Assert.Equal("attachment", copy.ContentDisposition);
        Assert.Equal("STANDARD_IA", copy.StorageClass);
        Assert.Equal("aws:kms", copy.Encryption);
        Assert.Equal("key-1", copy.KmsKeyId);
        Assert.Equal("blue", copy.Metadata["x-amz-meta-color"]);
        Assert.Equal("red", Assert.Single(copy.Tags).Value);
    }

    // needs-review A10 (R2-2)
    [Fact]
    public async Task A_create_only_copy_below_5_GiB_is_one_pinned_CopyObject_that_keeps_properties_and_a_plain_ETag()
    {
        var fake = ProviderFakeS3.Create();
        var content = Encoding.UTF8.GetBytes("small object");
        var source = Decorated(fake, "src.bin", content);
        await using var backend = Backend(fake);

        var copied = await backend.CopyAsync("src.bin", "dst.bin", new StorageTransferOptions { Overwrite = false });

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        var request = Assert.Single(fake.Copies);
        Assert.Equal("*", request.IfNoneMatch);
        Assert.Equal(source.ETag, request.ETagToMatch?.Trim('"'));
        Assert.Empty(fake.Initiated);
        var copy = fake.Objects["dst.bin"];
        Assert.DoesNotContain('-', copy.ETag);
        Assert.Equal(ProviderFakeS3.Md5(content), copy.ETag);
        AssertKeepsProperties(copy);
    }

    // needs-review A10
    [Fact]
    public async Task A_copy_above_the_single_request_limit_pins_every_part_carries_properties_and_runs_parts_in_parallel()
    {
        var fake = ProviderFakeS3.Create();
        var content = new byte[PartSize * 4 + 123];
        Random.Shared.NextBytes(content);
        var source = Decorated(fake, "big.bin", content);
        await using var backend = Backend(fake, copyThreshold: PartSize);

        var copied = await backend.CopyAsync("big.bin", "copy.bin", new StorageTransferOptions { Overwrite = false });

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        var parts = fake.CopiedParts.ToArray();
        Assert.Equal(5, parts.Length);
        Assert.All(parts, part => Assert.Equal(source.ETag, Assert.Single(part.ETagToMatch).Trim('"')));
        Assert.True(fake.MaxConcurrentParts > 1, $"parts ran one at a time ({fake.MaxConcurrentParts})");
        Assert.Equal("*", Assert.Single(fake.Completed).IfNoneMatch);
        var copy = fake.Objects["copy.bin"];
        Assert.Equal(content, copy.Content);
        AssertKeepsProperties(copy);
    }

    // needs-review A10: a source replaced mid-copy must not mix versions or be truncated
    [Fact]
    public async Task A_source_replaced_during_a_multipart_copy_fails_the_copy_and_aborts_it()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("big.bin", new byte[PartSize * 3]);
        var replaced = 0;
        fake.BeforeCopy = key =>
        {
            if (fake.CopiedParts.Count >= 2 && Interlocked.Exchange(ref replaced, 1) == 0)
                fake.Put(key, new byte[PartSize * 4]);
        };
        await using var backend = Backend(fake, copyThreshold: PartSize);

        var copied = await backend.CopyAsync("big.bin", "copy.bin");

        Assert.True(copied.IsFailure);
        Assert.Equal(StorageErrors.ConflictCode, copied.Error!.Code);
        Assert.False(fake.Objects.ContainsKey("copy.bin"));
        Assert.Equal(1, fake.Aborts);
    }

    // needs-review E: abort on part failure
    [Fact]
    public async Task A_failed_part_aborts_the_multipart_copy()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("big.bin", new byte[PartSize * 3]);
        fake.FailPart = 2;
        await using var backend = Backend(fake, copyThreshold: PartSize);

        var copied = await backend.CopyAsync("big.bin", "copy.bin");

        Assert.True(copied.IsFailure);
        Assert.Equal(1, fake.Aborts);
        Assert.Empty(fake.Completed);
        Assert.False(fake.Objects.ContainsKey("copy.bin"));
    }

    // needs-review E: overwrite over 5 GiB
    [Fact]
    public async Task An_overwrite_copy_over_5_GiB_is_multipart_without_a_create_condition()
    {
        var fake = ProviderFakeS3.Create();
        var size = 6L * 1024 * 1024 * 1024;
        fake.PutSynthetic("huge.bin", size);
        fake.PutSynthetic("target.bin", 1);
        await using var backend = new S3StorageBackend("s3", fake.Client, "bucket");

        var copied = await backend.CopyAsync("huge.bin", "target.bin", new StorageTransferOptions { Overwrite = true });

        Assert.True(copied.IsSuccess, copied.Error?.ToString());
        var complete = Assert.Single(fake.Completed);
        Assert.Null(complete.IfNoneMatch);
        Assert.Equal(size, fake.Objects["target.bin"].Size);
        var ranges = fake.CopiedParts.OrderBy(part => part.PartNumber).ToArray();
        Assert.Equal(0, ranges[0].FirstByte);
        Assert.Equal(size - 1, ranges[^1].LastByte);
    }

    // needs-review E: the 10,000-part math
    [Theory]
    [InlineData(16L * 1024 * 1024 * 10_000 + 1)]
    [InlineData(5L * 1024 * 1024 * 1024 * 1024)]
    [InlineData(5L * 1024 * 1024 * 1024 + 1)]
    public void Multipart_copy_parts_stay_within_10000_and_cover_the_object(long size)
    {
        var part = S3StorageBackend.MultipartCopyPartSize(size, 16 * 1024 * 1024);
        var count = (size + part - 1) / part;

        Assert.InRange(count, 1, 10_000);
        Assert.True(part >= 16 * 1024 * 1024);
        Assert.True(part <= 5L * 1024 * 1024 * 1024, "a part may not exceed 5 GiB");
        Assert.True((count - 1) * part < size && count * part >= size);
    }

    // needs-review B23 / A2 (contract C2)
    [Fact]
    public async Task Copy_honours_the_expected_source_ETag_and_version()
    {
        var fake = ProviderFakeS3.Create();
        var source = fake.Put("a.txt", [1, 2, 3]);
        await using var backend = Backend(fake);

        var stale = await backend.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { ExpectedSourceETag = "\"not-it\"" });
        var pinned = await backend.CopyAsync("a.txt", "c.txt", new StorageTransferOptions { SourceVersionId = source.VersionId, ExpectedSourceETag = source.ETag });

        Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
        Assert.False(fake.Objects.ContainsKey("b.txt"));
        Assert.True(pinned.IsSuccess, pinned.Error?.ToString());
        Assert.Equal(source.VersionId, Assert.Single(fake.Copies).SourceVersionId);
    }

    // needs-review B23 (contract C2): a destination condition is enforced by the server, or refused
    [Fact]
    public async Task A_destination_condition_is_sent_as_If_Match_or_refused_where_the_server_ignores_it()
    {
        var aws = ProviderFakeS3.Create();
        aws.Put("a.txt", [1]);
        var target = aws.Put("b.txt", [2]);
        await using var awsBackend = Backend(aws);
        var minio = ProviderFakeS3.Create(enforceCopyConditions: false, enforceDeleteCondition: false);
        minio.Put("a.txt", [1]);
        var minioTarget = minio.Put("b.txt", [2]);
        await using var minioBackend = Backend(minio);

        var wrong = await awsBackend.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = "\"other\"" } });
        var right = await awsBackend.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = target.ETag } });
        var ignored = await minioBackend.CopyAsync("a.txt", "b.txt", new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = minioTarget.ETag } });

        Assert.Equal(StorageErrors.ConflictCode, wrong.Error?.Code);
        Assert.True(right.IsSuccess, right.Error?.ToString());
        Assert.Equal(StorageErrors.UnsupportedCode, ignored.Error?.Code);
        Assert.Equal([2], minio.Objects["b.txt"].Content);
    }

    // needs-review A2: the source is deleted only while it is the version that was copied
    [Fact]
    public async Task A_move_keeps_a_source_rewritten_after_the_copy_and_reports_the_destination_committed()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("a.txt", [1, 1, 1]);
        fake.AfterCopy = key => fake.Put(key, [9, 9, 9]);
        await using var backend = Backend(fake);

        var moved = await backend.MoveAsync("a.txt", "b.txt");

        Assert.True(moved.IsFailure);
        Assert.True(StorageErrorInfo.DestinationCommitted(moved.Error), moved.Error!.ToString());
        Assert.True(StorageErrorInfo.TryGetDetail(moved.Error, StorageErrorInfo.LeftBehindKey, out var left));
        Assert.Equal("a.txt", left);
        Assert.Equal([9, 9, 9], fake.Objects["a.txt"].Content);
        Assert.Equal([1, 1, 1], fake.Objects["b.txt"].Content);
    }

    // needs-review A2: a file move deletes one object under If-Match, never recursively
    [Fact]
    public async Task A_file_move_deletes_only_the_copied_object_with_If_Match()
    {
        var fake = ProviderFakeS3.Create();
        var source = fake.Put("a", [1]);
        fake.Put("a/inside.txt", [2]);
        await using var backend = Backend(fake);

        var moved = await backend.MoveAsync("a", "b");

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        var delete = Assert.Single(fake.Deletes);
        Assert.Equal("a", delete.Key);
        Assert.Equal(source.ETag, delete.IfMatch?.Trim('"'));
        Assert.True(fake.Objects.ContainsKey("a/inside.txt"));
    }

    // needs-review A11: a failure after the commit carries destinationState=complete and leftBehind
    [Fact]
    public async Task A_move_whose_source_delete_fails_reports_the_destination_committed_and_what_was_left()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put(".cl-storage-transfer-1", [1]);
        fake.FailDelete = key => key == ".cl-storage-transfer-1";
        await using var backend = Backend(fake);

        var moved = await backend.MoveAsync(".cl-storage-transfer-1", "file.txt");

        Assert.True(StorageErrorInfo.DestinationCommitted(moved.Error), moved.Error?.ToString());
        Assert.True(StorageErrorInfo.TryGetDetail(moved.Error, StorageErrorInfo.LeftBehindKey, out var left));
        Assert.Equal(".cl-storage-transfer-1", left);
        Assert.True(fake.Objects.ContainsKey("file.txt"));
    }

    // needs-review A11: a cancel after the copy committed does not leave the move half-reported
    [Fact]
    public async Task A_cancel_after_the_copy_still_deletes_the_source()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("a.txt", [1]);
        using var cancel = new CancellationTokenSource();
        fake.AfterCopy = _ => cancel.Cancel();
        await using var backend = Backend(fake);

        var moved = await backend.MoveAsync("a.txt", "b.txt", cancellationToken: cancel.Token);

        Assert.True(moved.IsSuccess, moved.Error?.ToString());
        Assert.False(fake.Objects.ContainsKey("a.txt"));
        Assert.True(fake.Objects.ContainsKey("b.txt"));
    }

    // needs-review A2: a directory move deletes per file under the listed identity, never recursively
    [Fact]
    public async Task A_directory_move_keeps_a_file_changed_after_it_was_listed()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("dir/one.txt", [1]);
        fake.Put("dir/two.txt", [2]);
        var changed = 0;
        fake.AfterList = prefix =>
        {
            if (prefix == "dir/" && Interlocked.Exchange(ref changed, 1) == 0)
                fake.Put("dir/two.txt", [2, 2]);
        };
        await using var backend = Backend(fake);

        var moved = await backend.MoveAsync("dir", "moved");

        Assert.True(StorageErrorInfo.DestinationCommitted(moved.Error), moved.Error?.ToString());
        Assert.False(fake.Objects.ContainsKey("dir/one.txt"));
        Assert.Equal([2, 2], fake.Objects["dir/two.txt"].Content);
        Assert.Equal([1], fake.Objects["moved/one.txt"].Content);
        Assert.Equal([2, 2], fake.Objects["moved/two.txt"].Content);
    }

    // needs-review B21: a completion whose reply was lost is recognised by its multipart ETag
    [Fact]
    public async Task A_completed_multipart_upload_whose_reply_was_lost_succeeds()
    {
        var fake = ProviderFakeS3.Create();
        fake.LoseCompletionReply = true;
        await using var backend = Backend(fake);
        var content = new byte[PartSize + 17];
        Random.Shared.NextBytes(content);

        var uploaded = await backend.UploadBytesAsync("big.bin", content, new StorageUploadOptions { Overwrite = false });

        Assert.True(uploaded.IsSuccess, uploaded.Error?.ToString());
        Assert.Equal(content, fake.Objects["big.bin"].Content);
        Assert.Equal(0, fake.Aborts);
    }

    // needs-review B21: someone else's object at the key is not taken for ours
    [Fact]
    public async Task A_lost_completion_reply_with_a_different_object_at_the_key_still_fails()
    {
        var fake = ProviderFakeS3.Create();
        fake.LoseCompletionReply = true;
        fake.AfterComplete = key => fake.Put(key, [7]);
        await using var backend = Backend(fake);

        var uploaded = await backend.UploadBytesAsync("big.bin", new byte[PartSize + 17]);

        Assert.True(uploaded.IsFailure);
        Assert.Equal([7], fake.Objects["big.bin"].Content);
        Assert.Equal(
            $"{Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Convert.FromHexString(ProviderFakeS3.Md5([1]) + ProviderFakeS3.Md5([2]))))}-2",
            S3StorageBackend.MultipartETag(["\"" + ProviderFakeS3.Md5([1]) + "\"", ProviderFakeS3.Md5([2])]));
        Assert.Null(S3StorageBackend.MultipartETag(["not-an-md5"]));
    }

    // needs-review B64: a server that does not enforce conditions gets checks instead of headers
    [Fact]
    public async Task A_connection_marked_not_enforcing_checks_conditions_itself_and_claims_no_atomic_conditions()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("taken.txt", [1]);
        await using var backend = Backend(fake, conditions: S3ConditionalRequestSupport.NotEnforced);
        var enforcement = (IStorageConditionEnforcementSource)backend;

        var created = await backend.UploadBytesAsync("taken.txt", [2], new StorageUploadOptions { Overwrite = false });

        Assert.Equal(StorageErrors.ConflictCode, created.Error?.Code);
        Assert.Equal([1], fake.Objects["taken.txt"].Content);
        Assert.DoesNotContain(fake.Puts, put => put.IfNoneMatch is not null || put.IfMatch is not null);
        Assert.False(backend.Capabilities.Supports(StorageFeature.ConditionalCreate));
        Assert.False(backend.Capabilities.Supports(StorageFeature.ConditionalUpdate));
        Assert.False(backend.Capabilities.Supports(StorageFeature.ConditionalDelete));
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, await enforcement.GetEnforcementAsync(StorageConditionKind.CreateOnly, false, default));
    }

    // needs-review A10 / B64 (contract C3): copy and delete enforcement is probed per connection
    [Fact]
    public async Task Copy_and_delete_enforcement_is_probed_once_per_connection_and_cleans_up()
    {
        var minio = ProviderFakeS3.Create(enforceCopyConditions: false, enforceDeleteCondition: false);
        var aws = ProviderFakeS3.Create();
        await using var minioBackend = Backend(minio);
        await using var awsBackend = Backend(aws);
        var minioSource = (IStorageConditionEnforcementSource)minioBackend;
        var awsSource = (IStorageConditionEnforcementSource)awsBackend;

        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, await minioSource.GetEnforcementAsync(StorageConditionKind.CreateOnly, true, default));
        Assert.Equal(StorageConditionEnforcement.CheckedBeforeCommit, await minioSource.GetEnforcementAsync(StorageConditionKind.DeleteMatchVersion, false, default));
        Assert.Equal(StorageConditionEnforcement.Atomic, await minioSource.GetEnforcementAsync(StorageConditionKind.CreateOnly, false, default));
        Assert.Equal(StorageConditionEnforcement.Atomic, await awsSource.GetEnforcementAsync(StorageConditionKind.CreateOnly, true, default));
        Assert.Equal(StorageConditionEnforcement.Atomic, await awsSource.GetEnforcementAsync(StorageConditionKind.MatchVersion, true, default));
        Assert.Equal(StorageConditionEnforcement.Atomic, await awsSource.GetEnforcementAsync(StorageConditionKind.DeleteMatchVersion, false, default));
        Assert.Equal(2, minio.Puts.Count);
        Assert.Empty(minio.Objects);
        Assert.Empty(aws.Objects);
    }

    // needs-review B60: an SSE-C ETag is not an MD5
    [Fact]
    public async Task An_SSE_C_object_reports_no_MD5()
    {
        var fake = ProviderFakeS3.Create();
        fake.Put("secret.bin", [1, 2, 3]).CustomerEncryption = "AES256";
        fake.Put("plain.bin", [1, 2, 3]);
        await using var backend = Backend(fake);

        var secret = await backend.GetServerChecksumAsync("secret.bin", StorageChecksumAlgorithm.Md5);
        var plain = await backend.GetServerChecksumAsync("plain.bin", StorageChecksumAlgorithm.Md5);

        Assert.Equal(StorageErrors.UnsupportedCode, secret.Error?.Code);
        Assert.True(plain.IsSuccess);
    }
}
