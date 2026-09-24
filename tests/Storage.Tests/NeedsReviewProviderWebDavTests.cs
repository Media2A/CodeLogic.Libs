using System.Net;
using System.Reflection;
using CL.Storage.Errors;
using CL.Storage.Models;
using CL.Storage.Providers.WebDav;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;
using Xunit;

namespace Storage.Tests;

/// <summary>Needs-review round 3, providers: WebDAV collections, 207 Multi-Status, and hrefs in the server's spelling.</summary>
public sealed class NeedsReviewProviderWebDavTests
{
    /// <summary>A WebDAV client whose PROPFIND answers come from a table of hrefs.</summary>
    public class FakeDavClient : DispatchProxy
    {
        public Dictionary<string, Item[]> Listings { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Item> Resources { get; } = new(StringComparer.Ordinal);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var path = args is { Length: > 0 } ? args[0] as string : null;
            return targetMethod!.Name switch
            {
                nameof(IClient.List) => Task.FromResult<IEnumerable<Item>>(Listings.TryGetValue(path!.TrimEnd('/') is { Length: > 0 } key ? key : "/", out var items)
                    ? items
                    : throw new WebDAVException(404, "not found")),
                nameof(IClient.GetFolder) or nameof(IClient.GetFile) => Resources.TryGetValue(path!.TrimEnd('/'), out var item)
                    ? Task.FromResult(item)
                    : Task.FromException<Item>(new WebDAVException(404, "not found")),
                "get_CustomHeaders" => null,
                nameof(IDisposable.Dispose) => null,
                _ => throw new NotSupportedException(targetMethod.Name)
            };
        }
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static Item Folder(string href) => new() { Href = href, IsCollection = true };
    private static Item File(string href) => new() { Href = href, IsCollection = false, ContentLength = 1 };

    private static (WebDavStorageBackend Backend, FakeDavClient Client, RecordingHandler Http) Create(HttpStatusCode status, string? root = null)
    {
        var client = (FakeDavClient)(object)DispatchProxy.Create<IClient, FakeDavClient>();
        var handler = new RecordingHandler(status);
        var backend = new WebDavStorageBackend("dav", (IClient)(object)client, root, "/", ownsClient: false, 1 << 20, retry: null, observer: null,
            new HttpClient(handler), new Uri("http://dav.test"));
        return (backend, client, handler);
    }

    // needs-review B18: a 207 Multi-Status answer to MOVE is a partial failure, not a completed move
    [Fact]
    public async Task A_collection_move_answered_with_207_is_a_partial_failure()
    {
        var (backend, client, http) = Create(HttpStatusCode.MultiStatus);
        client.Listings["/"] = [Folder("/"), Folder("/from/")];

        var moved = await backend.MoveAsync("from", "to");

        Assert.Equal(StorageErrors.PartialFailureCode, moved.Error?.Code);
        var request = Assert.Single(http.Requests);
        Assert.Equal("MOVE", request.Method.Method);
        // The destination did not exist, so the server is told not to replace one that appears meanwhile.
        Assert.Equal("F", request.Headers.GetValues("Overwrite").Single());
        Assert.False(backend.Capabilities.Supports(StorageFeature.AtomicMove));
    }

    // needs-review A3: a collection at the destination is never replaced (Overwrite: T would delete it first)
    [Fact]
    public async Task A_move_or_copy_onto_an_existing_collection_is_refused_without_a_request()
    {
        var (backend, client, http) = Create(HttpStatusCode.Created);
        client.Listings["/"] = [Folder("/"), Folder("/from/"), Folder("/to/"), File("/file.txt")];

        var folder = await backend.MoveAsync("from", "to", new StorageTransferOptions { Overwrite = true });
        var file = await backend.CopyAsync("file.txt", "to", new StorageTransferOptions { Overwrite = true });

        Assert.Equal(StorageErrors.ConflictCode, folder.Error?.Code);
        Assert.Equal(StorageErrors.ConflictCode, file.Error?.Code);
        Assert.Empty(http.Requests);
    }

    // needs-review B69: a root requested as "docs" against a server spelling it "Docs" still lists
    [Fact]
    public async Task Hrefs_in_the_servers_spelling_stay_under_the_requested_root()
    {
        var (backend, client, _) = Create(HttpStatusCode.OK, root: "docs");
        client.Listings["/docs"] = [Folder("/Docs/"), File("/Docs/a.txt"), Folder("/Docs/Sub/")];
        client.Listings["/docs/Sub"] = [Folder("/Docs/Sub/"), File("/Docs/Sub/b.txt")];
        client.Listings["/"] = [Folder("/"), Folder("/Docs/")];
        client.Resources["/docs"] = Folder("/Docs/");

        var listed = await backend.ListAsync("", new StorageListOptions { Recursive = true });

        Assert.True(listed.IsSuccess, listed.Error?.ToString());
        Assert.Equal(["Sub", "Sub/b.txt", "a.txt"], listed.Value!.Items.Select(item => item.Path).Order(StringComparer.Ordinal));
    }

    // needs-review B69: an item asked for in another spelling is found where the server says it exists
    [Fact]
    public async Task An_item_in_the_servers_spelling_is_found_when_the_server_confirms_the_requested_spelling()
    {
        var (backend, client, _) = Create(HttpStatusCode.OK);
        client.Listings["/"] = [Folder("/"), Folder("/Docs/"), Folder("/Other/")];
        client.Resources["/docs"] = Folder("/Docs/");

        var found = await backend.GetInfoAsync("docs");
        var sensitive = await backend.GetInfoAsync("other");

        Assert.True(found.IsSuccess, found.Error?.ToString());
        Assert.Equal("docs", found.Value!.Path);
        Assert.Equal(StorageItemType.Directory, found.Value.ItemType);
        Assert.Equal(StorageErrors.NotFoundCode, sensitive.Error?.Code);
    }
}
