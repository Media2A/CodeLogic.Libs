using CL.Storage.Configuration;
using CL.Storage.Models;
using CL.Storage.Providers;
using CL.Storage.Providers.Local;
using Xunit;

namespace Storage.Tests;

/// <summary>Covers hiding staging items, hidden files, and name patterns in listings.</summary>
public sealed class ListFilterTests
{
    [Theory]
    [InlineData(".cl-storage-upload-abc.tmp", true)]
    [InlineData(".clstorage-upload-abc", true)]
    [InlineData("dir/.cl-storage-directory-upload-x/inner.txt", true)]
    [InlineData("cl-storage-upload.txt", false)]
    [InlineData(".config", false)]
    public void Library_staging_items_and_their_contents_are_internal(string path, bool expected)
    {
        Assert.Equal(expected, StorageListFilter.IsInternal(path));
    }

    [Theory]
    [InlineData("*.csv", "Report.CSV", true)]
    [InlineData("*.csv", "report.csv.bak", false)]
    [InlineData("data-??.json", "data-01.json", true)]
    [InlineData("data-??.json", "data-1.json", false)]
    [InlineData("a+b(1).txt", "a+b(1).txt", true)]
    public void Name_patterns_use_case_insensitive_wildcards(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, StorageListFilter.Glob(pattern).IsMatch(name));
    }

    [Fact]
    public async Task Listings_hide_staging_items_by_default_and_can_filter_hidden_and_names()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        foreach (var name in new[] { "a.csv", "b.txt", ".env", ".cl-storage-upload-1.tmp", "sub/c.csv" })
            await storage.UploadBytesAsync(name, [1]);

        static string[] Names(StoragePage page) => [.. page.Items.Select(item => item.Path).Order()];

        Assert.Equal([".env", "a.csv", "b.txt", "sub"], Names((await storage.ListAsync("")).Value!));
        Assert.Contains(".cl-storage-upload-1.tmp", Names((await storage.ListAsync("", new StorageListOptions { IncludeInternal = true })).Value!));
        Assert.DoesNotContain(".env", Names((await storage.ListAsync("", new StorageListOptions { IncludeHidden = false })).Value!));
        Assert.Equal(["a.csv", "sub/c.csv"], Names((await storage.ListAsync("", new StorageListOptions { Recursive = true, NamePattern = "*.csv" })).Value!));
    }

    [Fact]
    public async Task Filtered_listings_still_page_through_every_match()
    {
        using var directory = new TestDirectory();
        var storage = new LocalStorageBackend("local", new LocalConnectionConfig { RootPath = directory.Path });
        for (var i = 0; i < 12; i++)
        {
            await storage.UploadBytesAsync($"f{i:D2}.log", [1]);
            await storage.UploadBytesAsync($"f{i:D2}.txt", [1]);
        }

        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = (await storage.ListAsync("", new StorageListOptions { PageSize = 5, NamePattern = "*.log", ContinuationToken = token })).Value!;
            Assert.True(page.Items.Count <= 5);
            seen.AddRange(page.Items.Select(item => item.Name));
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.Equal(12, seen.Count);
        Assert.All(seen, name => Assert.EndsWith(".log", name));
    }
}
