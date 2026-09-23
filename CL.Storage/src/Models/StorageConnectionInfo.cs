namespace CL.Storage.Models;

/// <summary>Sanitized, immutable information about a configured storage connection.</summary>
/// <param name="Id">Connection ID.</param>
/// <param name="Provider">Provider kind.</param>
/// <param name="Root">Mounted root.</param>
/// <param name="Enabled">Whether the connection is enabled.</param>
public sealed record StorageConnectionInfo(
    string Id,
    StorageProvider Provider,
    string Root,
    bool Enabled)
{
    /// <summary>Gets the server host name from the configuration; never includes credentials.</summary>
    public string? Host { get; init; }
    /// <summary>Gets the server port from the configuration.</summary>
    public int? Port { get; init; }
    /// <summary>Gets how the configuration protects the connection.</summary>
    public StorageTransportSecurity Security { get; init; }
    /// <summary>Gets the most recent health check, if one has run.</summary>
    public StorageConnectionHealth? LastHealth { get; init; }
}
