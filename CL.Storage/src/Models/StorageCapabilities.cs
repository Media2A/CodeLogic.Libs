namespace CL.Storage.Models;

/// <summary>Granular, capability-gated behavior exposed by a storage connection.</summary>
[Flags]
public enum StorageFeature : ulong
{
    None = 0,
    PhysicalDirectories = 1UL << 0,
    VirtualDirectories = 1UL << 1,
    Links = 1UL << 2,
    FileCopy = 1UL << 3,
    DirectoryCopy = 1UL << 4,
    FileMove = 1UL << 5,
    DirectoryMove = 1UL << 6,
    ServerSideCopy = 1UL << 7,
    ServerSideMove = 1UL << 8,
    RelayedCopy = 1UL << 9,
    AtomicMove = 1UL << 10,
    AtomicReplace = 1UL << 11,
    RangeReads = 1UL << 12,
    MetadataRead = 1UL << 13,
    MetadataWrite = 1UL << 14,
    ConditionalCreate = 1UL << 15,
    ConditionalUpdate = 1UL << 16,
    ConditionalDelete = 1UL << 17,
    ServerPagination = 1UL << 18,
    Checksums = 1UL << 19,
    MultipartUpload = 1UL << 20,
    ResumableUpload = 1UL << 21,
    SignedReadUrls = 1UL << 22,
    SignedWriteUrls = 1UL << 23,
    Versioning = 1UL << 24,
    Tags = 1UL << 25,
    AccessControlLists = 1UL << 26,
    Leases = 1UL << 27,
    Append = 1UL << 28,
    ChangeNotifications = 1UL << 29
}

/// <summary>Optional provider limits. A null value means the provider did not expose a reliable limit.</summary>
public sealed record StorageLimits
{
    public int? MaxPageSize { get; init; }
    public long? MaxObjectBytes { get; init; }
    public long? MaxSingleUploadBytes { get; init; }
    public int? MaxMetadataBytes { get; init; }
    public int? MaxTags { get; init; }
    public int? MaxBatchItems { get; init; }
    public int? PreferredUploadPartBytes { get; init; }
}

/// <summary>
/// Describes behavior implemented by a storage connection. The six legacy convenience properties are
/// retained as projections while callers migrate to <see cref="Features"/>.
/// </summary>
public sealed record StorageCapabilities
{
    public StorageCapabilities(StorageFeature features, StorageLimits? limits = null)
    {
        Features = features;
        Limits = limits ?? new StorageLimits();
    }

    /// <summary>Compatibility constructor for the original six capability booleans.</summary>
    public StorageCapabilities(
        bool Directories,
        bool NativeCopy,
        bool NativeMove,
        bool RangeReads,
        bool Metadata,
        bool ServerPagination)
        : this(
            (Directories ? StorageFeature.PhysicalDirectories : StorageFeature.None) |
            (NativeCopy ? StorageFeature.FileCopy | StorageFeature.ServerSideCopy : StorageFeature.None) |
            (NativeMove
                ? StorageFeature.FileMove | StorageFeature.DirectoryMove | StorageFeature.ServerSideMove
                : StorageFeature.None) |
            (RangeReads ? StorageFeature.RangeReads : StorageFeature.None) |
            (Metadata ? StorageFeature.MetadataRead : StorageFeature.None) |
            (ServerPagination ? StorageFeature.ServerPagination : StorageFeature.None))
    {
    }

    public StorageFeature Features { get; }
    public StorageLimits Limits { get; }

    public bool Directories => SupportsAny(
        StorageFeature.PhysicalDirectories | StorageFeature.VirtualDirectories);
    public bool NativeCopy => Supports(StorageFeature.ServerSideCopy);
    public bool NativeMove => Supports(StorageFeature.ServerSideMove);
    public bool RangeReads => Supports(StorageFeature.RangeReads);
    public bool Metadata => Supports(StorageFeature.MetadataRead);
    public bool ServerPagination => Supports(StorageFeature.ServerPagination);

    public bool Supports(StorageFeature feature) => (Features & feature) == feature;
    public bool SupportsAny(StorageFeature features) => (Features & features) != 0;

    public void Deconstruct(
        out bool directories,
        out bool nativeCopy,
        out bool nativeMove,
        out bool rangeReads,
        out bool metadata,
        out bool serverPagination)
    {
        directories = Directories;
        nativeCopy = NativeCopy;
        nativeMove = NativeMove;
        rangeReads = RangeReads;
        metadata = Metadata;
        serverPagination = ServerPagination;
    }
}
