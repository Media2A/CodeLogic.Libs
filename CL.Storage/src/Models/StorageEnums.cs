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
