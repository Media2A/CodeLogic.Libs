using System.Text.Json;
using System.Text.Json.Serialization;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Forward-compatible connection data used until a provider adapter supplies its typed model.</summary>
public sealed class ProviderConnectionConfig
{
    public bool Enabled { get; set; } = true;
    public string Root { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = [];
}

public abstract class ProviderStorageConfigBase : ConfigModelBase
{
    public Dictionary<string, ProviderConnectionConfig> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override ConfigValidationResult Validate()
    {
        var errors = Connections.Keys
            .Where(string.IsNullOrWhiteSpace)
            .Select(_ => "Connection IDs cannot be blank")
            .ToArray();
        return errors.Length == 0 ? ConfigValidationResult.Valid() : ConfigValidationResult.Invalid(errors);
    }
}

[ConfigSection("storage.s3")]
public sealed class S3StorageConfig : ProviderStorageConfigBase { }

[ConfigSection("storage.ftp")]
public sealed class FtpStorageConfig : ProviderStorageConfigBase { }

[ConfigSection("storage.sftp")]
public sealed class SftpStorageConfig : ProviderStorageConfigBase { }

[ConfigSection("storage.webdav")]
public sealed class WebDavStorageConfig : ProviderStorageConfigBase { }

[ConfigSection("storage.azure")]
public sealed class AzureStorageConfig : ProviderStorageConfigBase { }

[ConfigSection("storage.gcs")]
public sealed class GoogleCloudStorageConfig : ProviderStorageConfigBase { }

[ConfigSection("storage.swift")]
public sealed class SwiftStorageConfig : ProviderStorageConfigBase { }
