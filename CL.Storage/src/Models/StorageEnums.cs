namespace CL.Storage.Models;

/// <summary>Identifies a built-in storage provider.</summary>
public enum StorageProvider
{
    /// <summary>Local filesystem or UNC share.</summary>
    Local,
    /// <summary>Amazon S3 or an S3-compatible object store.</summary>
    S3,
    /// <summary>FTP or FTPS server.</summary>
    Ftp,
    /// <summary>SSH File Transfer Protocol server.</summary>
    Sftp,
    /// <summary>WebDAV endpoint.</summary>
    WebDav,
    /// <summary>Azure Blob Storage container.</summary>
    AzureBlob,
    /// <summary>Google Cloud Storage bucket.</summary>
    GoogleCloudStorage,
    /// <summary>OpenStack Swift container.</summary>
    OpenStackSwift
}

/// <summary>Identifies the kind of a storage item.</summary>
public enum StorageItemType
{
    /// <summary>Byte content addressable by a path.</summary>
    File,
    /// <summary>Physical directory or virtual object-key prefix.</summary>
    Directory,
    /// <summary>Symbolic link or provider reference.</summary>
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
