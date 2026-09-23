using CL.Storage.Models;
using CL.Storage.Registry;
using CL.Storage.Sync;
using Xunit;
using static Storage.Tests.NeedsReviewTransferTests;

namespace Storage.Tests;

/// <summary>
/// The round-4 finding that crossed two fixers' files: a weak ETag (the local provider's write time, creation time
/// and length) is compared as weak everywhere a match must prove the content unchanged.
/// </summary>
public sealed class NeedsReviewRound4FollowUpTests
{
    private const string Weak = "W/\"8dd1a2b3c4d5e6f-8dd1a2b3c4d5e6f-100000\"";

    [Fact] // needs-review R4-B23
    public async Task A_source_known_only_by_a_weak_ETag_resumes_only_when_the_prefix_is_verified()
    {
        var content = Content(100_000);

        async Task<StorageTransferReport> CopyAsync(bool verify)
        {
            var (library, directory, _, b) = await TwoConnectionsAsync();
            using var _l = library; using var _d = directory;
            Assert.True(library.RegisterBackend("Src", Source(content, eTag: Weak)).IsSuccess);
            var local = Local(b, "D");
            Assert.True(library.RegisterBackend("D", local).IsSuccess);
            var part = StagedWriter.ResumableStagingPath("big.bin", StagedWriter.SourceKey("big.bin", content.Length, null, Weak, null));
            await local.UploadBytesAsync(part, content[..60_000]);

            var report = await library.CopyAsync("Src", "big.bin", "D", "big.bin",
                new StorageTransferOptions { ConflictPolicy = StorageConflictPolicy.Resume, Verify = verify });

            Assert.True(report.IsSuccess, report.Error?.ToString());
            Assert.Equal(content, (await local.DownloadBytesAsync("big.bin")).Value!);
            return report;
        }

        // Without Verify an equal weak ETag cannot prove the staged prefix came from this content: start over.
        Assert.Equal(0, (await CopyAsync(verify: false)).BytesResumed);
        // With Verify the prefix is read back and compared, so the staged bytes are used.
        Assert.Equal(60_000, (await CopyAsync(verify: true)).BytesResumed);
    }

    [Fact] // needs-review R4-B23
    public void A_matching_weak_ETag_does_not_prove_the_same_version_but_a_different_one_proves_a_change()
    {
        var at = DateTimeOffset.UnixEpoch;
        StorageItem Item(string eTag, long size) =>
            new() { Path = "f", Name = "f", ItemType = StorageItemType.File, Size = size, ETag = eTag, LastModified = at };

        Assert.False(StorageTransferCoordinator.SameVersion(Item(Weak, 1), Item(Weak, 2)));
        Assert.True(StorageTransferCoordinator.SameVersion(Item(Weak, 1), Item(Weak, 1)));
        Assert.False(StorageTransferCoordinator.SameVersion(Item(Weak, 1), Item("W/\"other\"", 1)));
        Assert.True(StorageTransferCoordinator.SameVersion(Item("\"s\"", 1), Item("\"s\"", 2)));

        var identity = new StorageSyncIdentity(1, at, Weak, null, null);
        Assert.False(identity.Matches(Item(Weak, 2), TimeSpan.Zero));
        Assert.True(identity.Matches(Item(Weak, 1), TimeSpan.Zero));
        Assert.False(identity.SameVersionAs(new StorageSyncIdentity(2, at, Weak, null, null), TimeSpan.Zero));
    }
}
