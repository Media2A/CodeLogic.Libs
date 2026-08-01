namespace CL.Storage.Models;

/// <summary>Sanitized, immutable information about a configured storage connection.</summary>
public sealed record StorageConnectionInfo(
    string Id,
    StorageProvider Provider,
    string Root,
    bool Enabled);
