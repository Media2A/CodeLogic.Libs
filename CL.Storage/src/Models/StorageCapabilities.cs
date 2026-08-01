namespace CL.Storage.Models;

/// <summary>Describes optional behavior implemented by a storage connection.</summary>
public sealed record StorageCapabilities(
    bool Directories,
    bool NativeCopy,
    bool NativeMove,
    bool RangeReads,
    bool Metadata,
    bool ServerPagination);
