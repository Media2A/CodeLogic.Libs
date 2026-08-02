namespace CL.Storage.Models;

/// <summary>A page of storage items and its provider-opaque continuation token.</summary>
public sealed class StoragePage
{
    /// <summary>Initializes an immutable provider page.</summary>
    /// <param name="items">Items returned for the current page.</param>
    /// <param name="continuationToken">Opaque token for the next page.</param>
    public StoragePage(IEnumerable<StorageItem> items, string? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = Array.AsReadOnly(items.ToArray());
        ContinuationToken = continuationToken;
    }

    /// <summary>Gets the immutable items in this page.</summary>
    public IReadOnlyList<StorageItem> Items { get; }
    /// <summary>Gets the opaque next-page token, or <see langword="null"/> when enumeration is complete.</summary>
    public string? ContinuationToken { get; }
}
