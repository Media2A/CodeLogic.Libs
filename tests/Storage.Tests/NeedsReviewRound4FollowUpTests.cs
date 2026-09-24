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

    [Fact] // needs-review R4-B20
    public void At_apply_a_time_listed_to_the_minute_matches_the_same_file_read_to_the_second()
    {
        // An FTP LIST without MLSD gives minutes; MDTM at apply gives seconds. Same file, same minute.
        var listed = new DateTimeOffset(2026, 9, 24, 12, 34, 0, TimeSpan.Zero);
        StorageItem Read(DateTimeOffset modified, long size = 1) =>
            new() { Path = "f", Name = "f", ItemType = StorageItemType.File, Size = size, LastModified = modified };
        var planned = StorageSyncIdentity.Of(Read(listed) with { ModifiedPrecision = TimeSpan.FromMinutes(1) })!;

        Assert.True(planned.Matches(Read(listed.AddSeconds(27)), TimeSpan.Zero));
        Assert.True(planned.Matches(Read(listed), TimeSpan.Zero));
        Assert.False(planned.Matches(Read(listed.AddSeconds(60)), TimeSpan.Zero));
        Assert.False(planned.Matches(Read(listed.AddSeconds(-1)), TimeSpan.Zero));
        Assert.False(planned.Matches(Read(listed.AddSeconds(27), size: 2), TimeSpan.Zero));

        // Without a precision a whole-minute time is exact: an edit a second later is a change (R4-B20 still holds).
        var exact = new StorageSyncIdentity(1, listed, null, null, null);
        Assert.False(exact.Matches(Read(listed.AddSeconds(1)), TimeSpan.Zero));
    }

    [Fact] // needs-review R4-B20
    public void A_file_copied_to_a_listing_that_gives_minutes_does_not_look_changed_on_the_next_compare()
    {
        var local = new DateTimeOffset(2026, 9, 24, 12, 34, 56, TimeSpan.Zero);
        var listed = new DateTimeOffset(2026, 9, 24, 12, 34, 0, TimeSpan.Zero);
        StorageItem Item(DateTimeOffset modified, TimeSpan? precision) =>
            new() { Path = "f", Name = "f", ItemType = StorageItemType.File, Size = 1, LastModified = modified, ModifiedPrecision = precision };
        var options = new StorageCompareOptions();

        Assert.Equal(StorageDiffReason.None, StorageCompare.Basic(Item(local, null), Item(listed, TimeSpan.FromMinutes(1)), options));
        Assert.Equal(StorageDiffReason.SourceNewer,
            StorageCompare.Basic(Item(local.AddMinutes(2), null), Item(listed, TimeSpan.FromMinutes(1)), options));
    }

    [Theory] // needs-review R4-B20
    [InlineData("-rw-r--r--    1 1000     1000           12 Sep 24 12:34 a.txt", 60.0)]
    [InlineData("-rw-r--r--    1 1000     1000           12 Sep 24  2025 a.txt", 86400.0)]
    [InlineData("09-24-26  12:34PM                   12 a.txt", 60.0)]
    [InlineData("type=file;size=12;modify=20260924123456; a.txt", -1.0)]
    [InlineData("", -1.0)]
    public void An_FTP_listing_line_says_how_coarse_its_time_is(string line, double seconds)
    {
        var item = new FluentFTP.FtpListItem("a.txt", 12, FluentFTP.FtpObjectType.File, new DateTime(2026, 9, 24, 12, 34, 0, DateTimeKind.Utc))
        {
            Input = line
        };

        var precision = CL.Storage.Providers.Ftp.FtpStorageBackend.ListedPrecision(item);

        Assert.Equal(seconds < 0 ? null : TimeSpan.FromSeconds(seconds), precision);
    }
}
