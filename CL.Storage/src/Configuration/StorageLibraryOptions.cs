namespace CL.Storage.Configuration;

/// <summary>Options fixed when a <see cref="StorageLibrary"/> is constructed.</summary>
public sealed record StorageLibraryOptions
{
    /// <summary>
    /// Gets whether the library runs without configuration files: no <c>config.storage*.json</c> section is
    /// registered, read, or written, and the only connections are those added at runtime with
    /// <see cref="StorageLibrary.AddOrUpdateConnectionAsync{TConfig}"/> or <see cref="StorageLibrary.RegisterBackend"/>.
    /// <c>persist</c> arguments then update the in-memory copy only. No default connection is required.
    /// </summary>
    public bool RuntimeOnly { get; init; }

    /// <summary>Gets library settings (limits, health timeout) used in runtime-only mode; defaults when omitted.</summary>
    public StorageConfig? Settings { get; init; }
}
