using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

/// <summary>Specifies how an Azure Blob Storage connection authenticates.</summary>
public enum AzureBlobAuthenticationMode
{
    /// <summary>Uses an Azure Storage connection string.</summary>
    ConnectionString,
    /// <summary>Uses the Azure Identity default credential chain.</summary>
    DefaultCredential,
    /// <summary>Uses an account name and shared key.</summary>
    SharedKey,
    /// <summary>Uses a shared access signature token.</summary>
    SasToken
}

/// <summary>Defines named Azure Blob Storage connections.</summary>
[ConfigSection("storage.azure")]
public sealed class AzureStorageConfig : ProviderStorageConfigBase<AzureBlobConnectionConfig> { }

/// <summary>Defines one Azure Blob Storage container connection.</summary>
public sealed class AzureBlobConnectionConfig : StorageConnectionConfigBase
{
    /// <summary>Gets or sets the blob container name.</summary>
    [ConfigField(Label = "Container", Required = true, Group = "Connection", Order = 10)]
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the blob prefix mounted as the connection root.</summary>
    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Gets or sets the authentication mode.</summary>
    public AzureBlobAuthenticationMode AuthenticationMode { get; set; } = AzureBlobAuthenticationMode.ConnectionString;

    /// <summary>Gets or sets the absolute Blob service URI.</summary>
    [ConfigField(Label = "Service URI", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? ServiceUri { get; set; }

    /// <summary>Gets or sets the Azure Storage connection string.</summary>
    [ConfigField(Label = "Connection string", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 20)]
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets the storage account name.</summary>
    [ConfigField(Label = "Account name", Group = "Credentials", Order = 21)]
    public string? AccountName { get; set; }

    /// <summary>Gets or sets the storage account shared key.</summary>
    [ConfigField(Label = "Account key", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? AccountKey { get; set; }

    /// <summary>Gets or sets the shared access signature token.</summary>
    [ConfigField(Label = "SAS token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? SasToken { get; set; }

    /// <summary>Gets or sets the request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>Gets or sets the maximum SDK retry count.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <inheritdoc />
    public override string MountRoot => Prefix;

    internal override IEnumerable<string> GetValidationErrors()
    {
        if (string.IsNullOrWhiteSpace(Container))
            yield return "Container is required";
        if (StoragePath.Normalize(Prefix ?? string.Empty).IsFailure)
            yield return "Prefix is invalid";
        if (TimeoutSeconds <= 0)
            yield return "TimeoutSeconds must be greater than zero";
        if (MaxRetries < 0)
            yield return "MaxRetries cannot be negative";
        if (AuthenticationMode == AzureBlobAuthenticationMode.ConnectionString && string.IsNullOrWhiteSpace(ConnectionString))
            yield return "ConnectionString authentication requires ConnectionString";
        if (AuthenticationMode is AzureBlobAuthenticationMode.DefaultCredential or AzureBlobAuthenticationMode.SharedKey or AzureBlobAuthenticationMode.SasToken)
        {
            if (!Uri.TryCreate(ServiceUri, UriKind.Absolute, out var serviceUri) || serviceUri.Scheme != Uri.UriSchemeHttps)
                yield return "This authentication mode requires an absolute HTTPS ServiceUri";
        }
        if (AuthenticationMode == AzureBlobAuthenticationMode.SharedKey &&
            (string.IsNullOrWhiteSpace(AccountName) || string.IsNullOrWhiteSpace(AccountKey)))
            yield return "SharedKey authentication requires AccountName and AccountKey";
        if (AuthenticationMode == AzureBlobAuthenticationMode.SasToken && string.IsNullOrWhiteSpace(SasToken))
            yield return "SasToken authentication requires SasToken";
    }
}
