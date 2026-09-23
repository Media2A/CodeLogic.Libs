using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.Sftp;
using CL.Storage.Providers.WebDav;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>
/// Reads live-server settings from <c>CL_STORAGE_TEST_*</c> environment variables. Every live test
/// self-skips when its provider is not configured, so the suite is safe to run without servers.
/// </summary>
internal static class LiveServers
{
    public static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public static bool SftpConfigured => Env("CL_STORAGE_TEST_SFTP_HOST") is not null;
    public static bool FtpConfigured => Env("CL_STORAGE_TEST_FTP_HOST") is not null;
    public static bool WebDavConfigured => Env("CL_STORAGE_TEST_WEBDAV_URL") is not null;

    public static SftpConnectionConfig Sftp(Action<SftpConnectionConfig>? configure = null)
    {
        var config = new SftpConnectionConfig
        {
            Host = Env("CL_STORAGE_TEST_SFTP_HOST")!,
            Port = int.Parse(Env("CL_STORAGE_TEST_SFTP_PORT") ?? "22"),
            Username = Env("CL_STORAGE_TEST_SFTP_USER")!,
            Password = Env("CL_STORAGE_TEST_SFTP_PASS"),
            Root = Env("CL_STORAGE_TEST_SFTP_ROOT") ?? "upload",
            AutoAcceptHostKey = true,
            TimeoutSeconds = 15
        };
        configure?.Invoke(config);
        return config;
    }

    public static FtpConnectionConfig Ftp(Action<FtpConnectionConfig>? configure = null)
    {
        var config = new FtpConnectionConfig
        {
            Host = Env("CL_STORAGE_TEST_FTP_HOST")!,
            Port = int.Parse(Env("CL_STORAGE_TEST_FTP_PORT") ?? "21"),
            Username = Env("CL_STORAGE_TEST_FTP_USER")!,
            Password = Env("CL_STORAGE_TEST_FTP_PASS")!,
            EncryptionMode = Enum.Parse<StorageFtpEncryptionMode>(Env("CL_STORAGE_TEST_FTP_ENCRYPTION") ?? "None"),
            Root = Env("CL_STORAGE_TEST_FTP_ROOT") ?? string.Empty,
            TimeoutSeconds = 15
        };
        configure?.Invoke(config);
        return config;
    }

    public static WebDavConnectionConfig WebDav(Action<WebDavConnectionConfig>? configure = null)
    {
        var url = Env("CL_STORAGE_TEST_WEBDAV_URL")!;
        var config = new WebDavConnectionConfig
        {
            Endpoint = url,
            AllowInsecureHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            AuthenticationMode = WebDavAuthenticationMode.Basic,
            Username = Env("CL_STORAGE_TEST_WEBDAV_USER"),
            Password = Env("CL_STORAGE_TEST_WEBDAV_PASS"),
            TimeoutSeconds = 15
        };
        configure?.Invoke(config);
        return config;
    }

    public static IStorageBackend Create(object config) => config switch
    {
        SftpConnectionConfig sftp => new SftpStorageBackendFactory().Create("live-sftp", Validated(sftp), 64L << 20),
        FtpConnectionConfig ftp => new FtpStorageBackendFactory().Create("live-ftp", Validated(ftp), 64L << 20),
        WebDavConnectionConfig webDav => new WebDavStorageBackendFactory().Create("live-webdav", Validated(webDav), 64L << 20),
        _ => throw new ArgumentOutOfRangeException(nameof(config))
    };

    private static T Validated<T>(T config) where T : StorageConnectionConfigBase
    {
        var validation = config.Validate();
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        return config;
    }
}

/// <summary>Skips unless a live SFTP server is configured.</summary>
public sealed class SftpFactAttribute : FactAttribute
{
    public SftpFactAttribute()
    {
        if (!LiveServers.SftpConfigured) Skip = "Set CL_STORAGE_TEST_SFTP_* to run live SFTP tests.";
    }
}

/// <summary>Skips unless a live FTP server is configured.</summary>
public sealed class FtpFactAttribute : FactAttribute
{
    public FtpFactAttribute()
    {
        if (!LiveServers.FtpConfigured) Skip = "Set CL_STORAGE_TEST_FTP_* to run live FTP tests.";
    }
}

/// <summary>Skips unless a live WebDAV server is configured.</summary>
public sealed class WebDavFactAttribute : FactAttribute
{
    public WebDavFactAttribute()
    {
        if (!LiveServers.WebDavConfigured) Skip = "Set CL_STORAGE_TEST_WEBDAV_* to run live WebDAV tests.";
    }
}
