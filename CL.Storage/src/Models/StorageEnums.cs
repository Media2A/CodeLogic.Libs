namespace CL.Storage.Models;

/// <summary>Identifies a built-in storage provider.</summary>
public enum StorageProvider
{
    /// <summary>Local filesystem or UNC share.</summary>
    Local = 0,
    /// <summary>Amazon S3 or an S3-compatible object store.</summary>
    S3 = 1,
    /// <summary>FTP or FTPS server.</summary>
    Ftp = 2,
    /// <summary>SSH File Transfer Protocol server.</summary>
    Sftp = 3,
    /// <summary>WebDAV endpoint.</summary>
    WebDav = 4,
    /// <summary>Azure Blob Storage container.</summary>
    AzureBlob = 5,
    /// <summary>Google Cloud Storage bucket.</summary>
    GoogleCloudStorage = 6,
    /// <summary>OpenStack Swift container.</summary>
    OpenStackSwift = 7
}

/// <summary>Identifies the kind of a storage item.</summary>
public enum StorageItemType
{
    /// <summary>Byte content addressable by a path.</summary>
    File = 0,
    /// <summary>Physical directory or virtual object-key prefix.</summary>
    Directory = 1,
    /// <summary>Symbolic link or provider reference.</summary>
    Link = 2
}

/// <summary>Controls user-metadata behavior when relaying between unlike providers.</summary>
public enum StorageMetadataPreservation
{
    /// <summary>Preserve metadata when the destination advertises support; otherwise copy content only.</summary>
    BestEffort = 0,
    /// <summary>Fail before upload when source metadata cannot be preserved.</summary>
    Require = 1,
    /// <summary>Never copy source user metadata.</summary>
    Discard = 2
}
