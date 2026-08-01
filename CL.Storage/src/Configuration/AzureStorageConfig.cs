using CL.Storage.Abstractions;
using CodeLogic.Core.Configuration;

namespace CL.Storage.Configuration;

public enum AzureBlobAuthenticationMode
{
    ConnectionString,
    DefaultCredential,
    SharedKey,
    SasToken
}

[ConfigSection("storage.azure")]
public sealed class AzureStorageConfig : ProviderStorageConfigBase<AzureBlobConnectionConfig> { }

public sealed class AzureBlobConnectionConfig : StorageConnectionConfigBase
{
    [ConfigField(Label = "Container", Required = true, Group = "Connection", Order = 10)]
    public string Container { get; set; } = string.Empty;

    [ConfigField(Label = "Prefix", Group = "Connection", Order = 11)]
    public string Prefix { get; set; } = string.Empty;

    public AzureBlobAuthenticationMode AuthenticationMode { get; set; } = AzureBlobAuthenticationMode.ConnectionString;

    [ConfigField(Label = "Service URI", InputType = ConfigInputType.Url, Group = "Connection", Order = 12)]
    public string? ServiceUri { get; set; }

    [ConfigField(Label = "Connection string", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 20)]
    public string? ConnectionString { get; set; }

    [ConfigField(Label = "Account name", Group = "Credentials", Order = 21)]
    public string? AccountName { get; set; }

    [ConfigField(Label = "Account key", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 22)]
    public string? AccountKey { get; set; }

    [ConfigField(Label = "SAS token", Secret = true, InputType = ConfigInputType.Password, Group = "Credentials", Order = 23)]
    public string? SasToken { get; set; }

    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 3;

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
