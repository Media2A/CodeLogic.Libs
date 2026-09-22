using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Providers.Ftp;
using CL.Storage.Providers.Sftp;
using Renci.SshNet;
using Xunit;

namespace Storage.Integration.Tests;

public sealed class SftpLiveTests
{
    [SftpFact]
    public async Task Round_trip_through_a_real_server()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        await StorageContract.RoundTripAsync(storage);
    }

    [SftpFact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }

    [SftpFact]
    public async Task Wrong_password_is_an_authentication_failure()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c => { c.Password = "wrong"; c.Retry.RetryCount = 0; }));
        await StorageContract.WrongCredentialsAreAuthenticationFailuresAsync(storage);
    }

    [SftpFact]
    public async Task Closed_port_is_a_connection_failure()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c => { c.Port = 1; c.Retry.RetryCount = 0; }));
        await StorageContract.ClosedPortIsConnectionFailureAsync(storage);
    }

    [SftpFact]
    public async Task Untrusted_host_key_is_rejected_with_the_presented_fingerprint()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c =>
        {
            c.AutoAcceptHostKey = false;
            c.HostKeyFingerprints = ["SHA256:" + Convert.ToBase64String(new byte[32]).TrimEnd('=')];
            c.Retry.RetryCount = 0;
        }));

        var health = await storage.CheckHealthAsync();

        Assert.Equal(StorageErrors.HostKeyRejectedCode, health.Error?.Code);
        Assert.True(StorageErrorInfo.TryGetDetail(health.Error, "presentedFingerprint", out var presented));
        Assert.StartsWith("SHA256:", presented);
    }

    [SftpFact]
    public async Task Writing_outside_the_writable_directory_is_permission_denied()
    {
        // The test server chroots the user into a root-owned directory; only "upload" is writable.
        await using var storage = LiveServers.Create(LiveServers.Sftp(c => c.Root = string.Empty));

        var upload = await storage.UploadBytesAsync($"denied-{Guid.NewGuid():N}.txt", [1, 2, 3]);

        Assert.Equal(StorageErrors.PermissionDeniedCode, upload.Error?.Code);
    }

    [SftpFact]
    public async Task Sequential_operations_reuse_one_session()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());
        for (var i = 0; i < 10; i++)
            Assert.True((await storage.ExistsAsync("missing.txt")).IsSuccess);

        Assert.Equal(1, ((SftpStorageBackend)storage).PoolStats.Opened);
    }

    [SftpFact]
    public async Task Session_limit_makes_excess_callers_fail_as_busy()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp(c =>
        {
            c.Session = new StorageSessionConfig { MaxSessions = 1, MaxIdleSessions = 1, AcquireTimeoutSeconds = 1 };
            c.Retry.RetryCount = 0;
        }));
        var lease = await storage.OpenNativeConnectionAsync<SftpClient>();
        Assert.True(lease.IsSuccess, lease.Error?.ToString());

        await using (lease.Value!)
        {
            var blocked = await storage.ExistsAsync("anything.txt");
            Assert.Equal(StorageErrors.ServerBusyCode, blocked.Error?.Code);
        }

        Assert.True((await storage.ExistsAsync("anything.txt")).IsSuccess);
    }
}

public sealed class FtpLiveTests
{
    [FtpFact]
    public async Task Round_trip_through_a_real_server()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        await StorageContract.RoundTripAsync(storage);
    }

    [FtpFact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }

    [FtpFact]
    public async Task Wrong_password_is_an_authentication_failure()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp(c => { c.Password = "wrong"; c.Retry.RetryCount = 0; }));
        await StorageContract.WrongCredentialsAreAuthenticationFailuresAsync(storage);
    }

    [FtpFact]
    public async Task Closed_port_is_a_connection_failure()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp(c => { c.Port = 1; c.Retry.RetryCount = 0; }));
        await StorageContract.ClosedPortIsConnectionFailureAsync(storage);
    }

    [FtpFact]
    public async Task Writing_where_the_account_has_no_rights_is_permission_denied()
    {
        // The FTP account is not chrooted; the filesystem root is not writable for it.
        await using var storage = LiveServers.Create(LiveServers.Ftp(c => c.Root = string.Empty));

        var upload = await storage.UploadBytesAsync($"denied-{Guid.NewGuid():N}.txt", [1, 2, 3]);

        Assert.Equal(StorageErrors.PermissionDeniedCode, upload.Error?.Code);
    }

    [FtpFact]
    public async Task Sequential_operations_reuse_one_session()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());
        for (var i = 0; i < 10; i++)
            Assert.True((await storage.ExistsAsync("missing.txt")).IsSuccess);

        Assert.Equal(1, ((FtpStorageBackend)storage).PoolStats.Opened);
    }

    [FtpFact]
    public async Task Session_limit_makes_excess_callers_fail_as_busy()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp(c =>
        {
            c.Session = new StorageSessionConfig { MaxSessions = 1, MaxIdleSessions = 1, AcquireTimeoutSeconds = 1 };
            c.Retry.RetryCount = 0;
        }));
        var lease = await storage.OpenNativeConnectionAsync<FluentFTP.AsyncFtpClient>();
        Assert.True(lease.IsSuccess, lease.Error?.ToString());

        await using (lease.Value!)
        {
            var blocked = await storage.ExistsAsync("anything.txt");
            Assert.Equal(StorageErrors.ServerBusyCode, blocked.Error?.Code);
        }

        Assert.True((await storage.ExistsAsync("anything.txt")).IsSuccess);
    }
}

public sealed class WebDavLiveTests
{
    [WebDavFact]
    public async Task Round_trip_through_a_real_server()
    {
        await using var storage = LiveServers.Create(LiveServers.WebDav());
        await StorageContract.RoundTripAsync(storage);
    }

    [WebDavFact]
    public async Task Missing_items_are_not_found()
    {
        await using var storage = LiveServers.Create(LiveServers.WebDav());
        await StorageContract.MissingItemIsNotFoundAsync(storage);
    }

    [WebDavFact]
    public async Task Wrong_password_is_an_authentication_failure()
    {
        await using var storage = LiveServers.Create(LiveServers.WebDav(c => { c.Password = "wrong"; c.Retry.RetryCount = 0; }));
        await StorageContract.WrongCredentialsAreAuthenticationFailuresAsync(storage);
    }

    [WebDavFact]
    public async Task Closed_port_is_a_connection_failure()
    {
        await using var storage = LiveServers.Create(LiveServers.WebDav(c => { c.Endpoint = "http://127.0.0.1:1/"; c.Retry.RetryCount = 0; }));
        await StorageContract.ClosedPortIsConnectionFailureAsync(storage);
    }
}
