using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum GoogleCloudAuthenticationMode
{
    ApplicationDefaultCredentials,
    ServiceAccountFile,
    ServiceAccountJson
}

[ConfigSection("storage.gcs")]
public sealed class GoogleCloudStorageConfig : ProviderStorageConfigBase<GoogleCloudConnectionConfig> { }

public sealed class GoogleCloudConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Bucket", Required = true, Group = "Connection", Order = 10)]
    public string Bucket { get; set; } = string.Empty;

    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    public GoogleCloudAuthenticationMode AuthenticationMode { get; set; } = GoogleCloudAuthenticationMode.ApplicationDefaultCredentials;

    [ConfigField(Label = "Service-account JSON file", Group = "Credentials", Order = 20)]
    public string? CredentialsJsonPath { get; set; }

    [ConfigField(Label = "Service-account JSON", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 21)]
    public string? CredentialsJson { get; set; }

    public int UploadChunkSizeBytes { get; set; } = 10 * 1024 * 1024;

    public override string MountRoot => Prefix;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
            yield return "Bucket is required";
        if (StoragePath.Normalize(Prefix ?? string.Empty).IsFailure)
            yield return "Prefix is invalid";
        if (UploadChunkSizeBytes <= 0 || UploadChunkSizeBytes % (256 * 1024) != 0)
            yield return "UploadChunkSizeBytes must be a positive multiple of 256 KiB";
        if (AuthenticationMode == GoogleCloudAuthenticationMode.ServiceAccountFile)
        {
            if (string.IsNullOrWhiteSpace(CredentialsJsonPath))
                yield return "ServiceAccountFile authentication requires CredentialsJsonPath";
            else if (!Path.IsPathFullyQualified(CredentialsJsonPath))
                yield return "CredentialsJsonPath must be an absolute path";
        }
        if (AuthenticationMode == GoogleCloudAuthenticationMode.ServiceAccountJson && string.IsNullOrWhiteSpace(CredentialsJson))
            yield return "ServiceAccountJson authentication requires CredentialsJson";
    }
}
