using System.Collections.ObjectModel;

namespace CL.Storage.Models;

/// <summary>Describes a file, directory, or link at a provider-neutral path.</summary>
public sealed record StorageItem
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    private IReadOnlyDictionary<string, string> _metadata = EmptyMetadata;

    /// <summary>Gets the normalized path relative to the mounted connection root.</summary>
    public required string Path { get; init; }
    /// <summary>Gets the final path component displayed to callers.</summary>
    public required string Name { get; init; }
    /// <summary>Gets whether the item is a file, directory, or link.</summary>
    public required StorageItemType ItemType { get; init; }
    /// <summary>Gets the file content length; directories and links may return <see langword="null"/>.</summary>
    public long? Size { get; init; }
    /// <summary>Gets the provider's last-modified timestamp when available.</summary>
    public DateTimeOffset? LastModified { get; init; }
    /// <summary>Gets the provider content type for a file.</summary>
    public string? ContentType { get; init; }
    /// <summary>Gets the provider entity tag without assuming it is a content hash.</summary>
    public string? ETag { get; init; }
    /// <summary>Provider version, generation, or mutation identifier when one is available.</summary>
    public string? VersionId { get; init; }
    /// <summary>Gets the Unix permission bits (for example octal 0755, including setuid/setgid/sticky) when the provider reports them.</summary>
    public int? UnixMode { get; init; }
    /// <summary>Gets <see cref="UnixMode"/> as <c>rwxr-xr-x</c> text, or <see langword="null"/> when unknown.</summary>
    public string? Permissions => UnixMode is { } mode ? UnixPermissions.Format(mode) : null;
    /// <summary>Gets the owner name when the provider reports one (FTP listings).</summary>
    public string? Owner { get; init; }
    /// <summary>Gets the group name when the provider reports one (FTP listings).</summary>
    public string? Group { get; init; }
    /// <summary>Gets the numeric owner ID when the provider reports one (SFTP).</summary>
    public long? OwnerId { get; init; }
    /// <summary>Gets the numeric group ID when the provider reports one (SFTP).</summary>
    public long? GroupId { get; init; }
    /// <summary>Gets the raw target of a symbolic link when the provider reports it.</summary>
    public string? LinkTarget { get; init; }
    /// <summary>Gets the creation time when the provider reports it.</summary>
    public DateTimeOffset? Created { get; init; }
    /// <summary>Gets the last access time when the provider reports it.</summary>
    public DateTimeOffset? LastAccessed { get; init; }
    /// <summary>Gets whether the item is hidden: a dot-file, or marked hidden on Windows.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Gets an immutable snapshot of provider metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = value is null || value.Count == 0
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }
}
