using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve.Games;

/// <summary>
/// Counter-Strike: Source server interaction via RCON.
/// </summary>
public sealed class CounterStrikeSource : IDisposable
{
    private readonly ValveRconClient _rcon;

    public CounterStrikeSource(string ip, ushort port, string rconPassword)
    {
        _rcon = new ValveRconClient(ip, port, rconPassword);
    }

    public Task<bool> ConnectAsync(int timeoutMs = 5000) => _rcon.ConnectAsync(timeoutMs);

    public async Task<string> GetStatusRawAsync() => await _rcon.SendCommandAsync("status").ConfigureAwait(false);

    public async Task<(ServerInfo Info, List<PlayerInfo> Players)> GetStatusAsync()
    {
        var raw = await GetStatusRawAsync().ConfigureAwait(false);
        return ValveStatusParser.ParseStatusWithPlayers(raw);
    }

    public Task<string> ChangeMapAsync(string mapName) => _rcon.SendCommandAsync($"changelevel {mapName}");
    public Task<string> KickPlayerAsync(string playerName) => _rcon.SendCommandAsync($"kick {playerName}");
    public Task<string> BanPlayerAsync(string playerName) => _rcon.SendCommandAsync($"banid 0 {playerName}");
    public Task<string> UnbanPlayerAsync(string playerName) => _rcon.SendCommandAsync($"removeid {playerName}");
    public Task<string> SetHostnameAsync(string name) => _rcon.SendCommandAsync($"hostname \"{name}\"");
    public Task<string> ExecConfigAsync(string configName) => _rcon.SendCommandAsync($"exec {configName}");
    public Task<string> SetCvarAsync(string cvar, string value) => _rcon.SendCommandAsync($"{cvar} {value}");
    public Task<string> GetCvarAsync(string cvar) => _rcon.SendCommandAsync(cvar);
    public Task<string> RestartAsync() => _rcon.SendCommandAsync("restart");
    public Task<string> SayAsync(string message) => _rcon.SendCommandAsync($"say {message}");

    public void Disconnect() => _rcon.Disconnect();
    public void Dispose() => _rcon.Dispose();
}
