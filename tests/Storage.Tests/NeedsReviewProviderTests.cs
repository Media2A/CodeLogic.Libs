using System.Diagnostics;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.Local;
using Xunit;

namespace Storage.Tests;

/// <summary>Needs-review round 3, providers: the Local backend.</summary>
public sealed class NeedsReviewProviderTests
{
    private static LocalStorageBackend Local(string root, bool followLinks = false) =>
        new("local", new LocalConnectionConfig { RootPath = root, FollowLinks = followLinks });

    // needs-review A24: only symbolic links and junctions are links; other reparse points are files
    [Fact]
    public async Task An_app_execution_alias_is_a_file_not_a_link()
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        var alias = OperatingSystem.IsWindows() && Directory.Exists(apps)
            ? Directory.EnumerateFiles(apps, "*.exe").FirstOrDefault(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 && new FileInfo(file).LinkTarget is null)
            : null;
        if (alias is null) return; // Needs a Windows reparse point that is not a link (an app execution alias).
        await using var storage = Local(apps);

        var info = await storage.GetInfoAsync(Path.GetFileName(alias));

        Assert.True(info.IsSuccess, info.Error?.ToString());
        Assert.Equal(StorageItemType.File, info.Value!.ItemType);
        Assert.False(LocalPathResolver.IsLink(alias, File.GetAttributes(alias)));
    }

    // needs-review A24 / B58 (rest): a directory link is reported as a link and not descended into
    [Fact]
    public async Task A_recursive_listing_does_not_descend_into_a_directory_link()
    {
        using var root = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, "real"));
        File.WriteAllText(Path.Combine(root.Path, "real", "inside.txt"), "x");
        var link = Path.Combine(root.Path, "linked");
        if (!TryCreateDirectoryLink(link, Path.Combine(root.Path, "real"))) return;
        await using var storage = Local(root.Path, followLinks: true);

        var listed = await storage.ListAsync("", new StorageListOptions { Recursive = true });

        Assert.True(listed.IsSuccess, listed.Error?.ToString());
        var items = listed.Value!.Items.ToDictionary(item => item.Path, item => item.ItemType);
        Assert.Equal(StorageItemType.Link, items["linked"]);
        Assert.Equal(StorageItemType.File, items["real/inside.txt"]);
        Assert.DoesNotContain("linked/inside.txt", items.Keys);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            // A junction needs no privilege, unlike a symbolic link.
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // needs-review B63 / E (Local ETag at coarse precision): the synthetic ETag is weak, so no caller takes it for a
    // content version: two versions of equal length written in the same coarse tick (FAT keeps 2 s) share the write
    // time, and NTFS gives a file recreated within 15 s the old creation time, so they can share the ETag.
    [Fact]
    public async Task The_local_ETag_is_a_weak_validator_of_write_time_creation_time_and_length()
    {
        using var root = new TestDirectory();
        var file = Path.Combine(root.Path, "a.txt");
        var coarse = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(file, "one");
        File.SetLastWriteTimeUtc(file, coarse);
        await using var storage = Local(root.Path);

        var item = (await storage.GetInfoAsync("a.txt")).Value!;
        var info = new FileInfo(file);

        Assert.StartsWith("W/\"", item.ETag, StringComparison.Ordinal);
        Assert.Equal($"W/\"{coarse.Ticks:x}-{info.CreationTimeUtc.Ticks:x}-3\"", item.ETag);
        var listed = (await storage.ListAsync("")).Value!.Items.Single();
        Assert.Equal(item.ETag, listed.ETag);
    }

    // needs-review B67: case sensitivity is probed per root, not decided by the operating system
    [Fact]
    public void Case_sensitivity_is_decided_by_the_root()
    {
        using var root = new TestDirectory();
        File.WriteAllText(Path.Combine(root.Path, "Sample.txt"), "x");
        var insensitive = LocalStorageBackend.DetectCaseInsensitive(root.Path);
        Assert.Equal(File.Exists(Path.Combine(root.Path, "sAMPLE.TXT")), insensitive);
        Assert.Equal(insensitive, Local(root.Path).Capabilities.Supports(StorageFeature.CaseInsensitivePaths));

        if (!OperatingSystem.IsWindows()) return;
        var sensitive = Path.Combine(root.Path, "sensitive");
        Directory.CreateDirectory(sensitive);
        using var fsutil = Process.Start(new ProcessStartInfo("fsutil.exe", $"file setCaseSensitiveInfo \"{sensitive}\" enable") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        fsutil.WaitForExit();
        if (fsutil.ExitCode != 0) return; // Per-folder case sensitivity is not available here.
        File.WriteAllText(Path.Combine(sensitive, "Sample.txt"), "x");

        Assert.False(LocalStorageBackend.DetectCaseInsensitive(sensitive));
        Assert.False(Local(sensitive).Capabilities.Supports(StorageFeature.CaseInsensitivePaths));
    }

    // needs-review B23 / A2 (contract C2): a local copy or move honours or refuses source pins
    [Fact]
    public async Task A_local_copy_or_move_honours_the_expected_source_ETag_and_refuses_versions()
    {
        using var root = new TestDirectory();
        File.WriteAllText(Path.Combine(root.Path, "a.txt"), "one");
        await using var storage = Local(root.Path);
        var etag = (await storage.GetInfoAsync("a.txt")).Value!.ETag;

        var stale = await storage.MoveAsync("a.txt", "b.txt", new StorageTransferOptions { ExpectedSourceETag = "W/\"0-0-0\"" });
        var version = await storage.CopyAsync("a.txt", "c.txt", new StorageTransferOptions { SourceVersionId = "v1" });
        var condition = await storage.CopyAsync("a.txt", "c.txt", new StorageTransferOptions { DestinationCondition = new StorageMutationCondition { ExpectedETag = etag } });
        var pinned = await storage.MoveAsync("a.txt", "d.txt", new StorageTransferOptions { ExpectedSourceETag = etag });

        Assert.Equal(StorageErrors.ConflictCode, stale.Error?.Code);
        Assert.Equal(StorageErrors.UnsupportedCode, version.Error?.Code);
        Assert.Equal(StorageErrors.UnsupportedCode, condition.Error?.Code);
        Assert.True(pinned.IsSuccess, pinned.Error?.ToString());
        Assert.Equal("one", File.ReadAllText(Path.Combine(root.Path, "d.txt")));
    }
}
