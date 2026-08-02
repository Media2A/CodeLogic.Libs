namespace CL.Storage.Models;

/// <summary>Identifies a built-in storage provider.</summary>
public enum StorageProvider
{
    Local,
    S3,
    Ftp,
    Sftp,
    WebDav,
    AzureBlob,
    GoogleCloudStorage,
    OpenStackSwift
}

/// <summary>Identifies the kind of a storage item.</summary>
public enum StorageItemType
{
    File,
    Directory,
    Link
}

/// <summary>Controls user-metadata behavior when relaying between unlike providers.</summary>
public enum StorageMetadataPreservation
{
    /// <summary>Preserve metadata when the destination advertises support; otherwise copy content only.</summary>
    BestEffort,
    /// <summary>Fail before upload when source metadata cannot be preserved.</summary>
    Require,
    /// <summary>Never copy source user metadata.</summary>
    Discard
}
