using System.Collections.ObjectModel;

namespace CL.Storage.Models;

/// <summary>Describes a file, directory, or link at a provider-neutral path.</summary>
public sealed record StorageItem
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private IReadOnlyDictionary<string, string> _metadata = EmptyMetadata;

    public required string Path { get; init; }
    public required string Name { get; init; }
    public required StorageItemType ItemType { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public string? ContentType { get; init; }
    public string? ETag { get; init; }

    /// <summary>Gets an immutable snapshot of provider metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = value is null || value.Count == 0
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }
}
