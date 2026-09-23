using CL.Storage.Abstractions;
using CL.Storage.Configuration;
using CL.Storage.Errors;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Raw FTP and SSH commands, and free-space queries, against real servers.</summary>
public sealed class CommandLiveTests
{
    [FtpFact]
    public async Task Ftp_raw_commands_need_explicit_opt_in()
    {
        await using var locked = LiveServers.Create(LiveServers.Ftp());
        Assert.False(locked.Capabilities.Supports(StorageFeature.RawCommands));
        Assert.Equal(StorageErrors.UnsupportedCode, (await locked.ExecuteCommandAsync("SYST")).Error?.Code);

        await using var open = LiveServers.Create(LiveServers.Ftp(c => c.AllowRawCommands = true));
        var syst = await open.ExecuteCommandAsync("SYST");

        Assert.True(open.Capabilities.Supports(StorageFeature.RawCommands));
        Assert.True(syst.IsSuccess, syst.Error?.ToString());
        Assert.True(syst.Value!.Succeeded);
        Assert.Equal("215", syst.Value.Code);
        Assert.Contains("UNIX", syst.Value.Output, StringComparison.OrdinalIgnoreCase);

        var rejected = await open.ExecuteCommandAsync("SITE NOSUCHTHING");
        Assert.True(rejected.IsSuccess);
        Assert.False(rejected.Value!.Succeeded);
        Assert.StartsWith("5", rejected.Value.Code);
    }

    [SftpFact]
    public async Task Ssh_commands_run_in_a_shell_session_when_the_server_allows_it()
    {
        // The jump container is a plain OpenSSH server; the atmoz SFTP server forbids shells.
        await using var shell = LiveServers.Create(new SftpConnectionConfig
        {
            Host = LiveServers.Env("CL_STORAGE_TEST_SSH_JUMP_HOST") ?? "127.0.0.1",
            Port = int.Parse(LiveServers.Env("CL_STORAGE_TEST_SSH_JUMP_PORT") ?? "2023"),
            Username = "jump",
            Password = "jump-pw",
            AutoAcceptHostKey = true,
            AllowRawCommands = true
        });

        var echo = await shell.ExecuteCommandAsync("echo hello; echo oops 1>&2; exit 3");

        Assert.True(echo.IsSuccess, echo.Error?.ToString());
        Assert.False(echo.Value!.Succeeded);
        Assert.Equal("3", echo.Value.Code);
        Assert.Equal("hello", echo.Value.Output.Trim());
        Assert.Equal("oops", echo.Value.ErrorOutput.Trim());
    }

    [SftpFact]
    public async Task Sftp_reports_free_space_through_statvfs()
    {
        await using var storage = LiveServers.Create(LiveServers.Sftp());

        var space = await storage.GetSpaceAsync();

        Assert.True(space.IsSuccess, space.Error?.ToString());
        Assert.True(space.Value!.TotalBytes > 0);
        Assert.InRange(space.Value.AvailableBytes!.Value, 1, space.Value.TotalBytes!.Value);
    }

    [FtpFact]
    public async Task Ftp_without_avbl_reports_space_as_unsupported()
    {
        await using var storage = LiveServers.Create(LiveServers.Ftp());

        var space = await storage.GetSpaceAsync();

        Assert.Equal(StorageErrors.UnsupportedCode, space.Error?.Code);
    }
}
