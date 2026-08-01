namespace CL.Storage.Models;

/// <summary>A page of storage items and its provider-opaque continuation token.</summary>
public sealed class StoragePage
{
    public StoragePage(IEnumerable<StorageItem> items, string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
        ContinuationToken = continuationToken;
    }

    public IReadOnlyList<StorageItem> Items { get; }
    public string? ContinuationToken { get; }
}
