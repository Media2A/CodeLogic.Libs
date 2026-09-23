using CL.Storage.Configuration;
using CL.Storage.Models;
using Xunit;

namespace Storage.Integration.Tests;

/// <summary>Registrations with identical settings share warm sessions.</summary>
public sealed class SharedPoolLiveTests
{
    // Lingering is opt-in; without it the last registration's sessions close at once.
    private static void Lingering(SftpConnectionConfig config) => config.Session = new StorageSessionConfig { LingerSeconds = 60 };

    [SftpFact]
    public async Task Re_registering_the_same_sftp_settings_reuses_the_warm_session()
    {
        await using var live = await LiveLibrary.StartAsync(("first", LiveServers.Sftp(Lingering)));
        Assert.True((await live.Library.GetStorage("first").ListAsync("")).IsSuccess);
        var before = (await live.Library.GetConnectionDiagnosticsAsync("first")).Value!.Pool!;

        Assert.True((await live.Library.RemoveConnectionAsync("first")).IsSuccess);
        Assert.True((await live.Library.AddOrUpdateConnectionAsync("second", LiveServers.Sftp(Lingering))).IsSuccess);
        Assert.True((await live.Library.GetStorage("second").ListAsync("")).IsSuccess);
        var after = (await live.Library.GetConnectionDiagnosticsAsync("second")).Value!.Pool!;

        Assert.Equal(before.Opened, after.Opened);
        Assert.True(after.Idle >= 1);
    }
}
