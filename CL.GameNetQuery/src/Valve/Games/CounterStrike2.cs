using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve.Games;

/// <summary>
/// Counter-Strike 2 server interaction via RCON.
/// Uses CS2-specific status parsing (different player/map format from CSS).
/// </summary>
public sealed class CounterStrike2 : IDisposable
{
    private readonly ValveRconClient _rcon;

    public CounterStrike2(string ip, ushort port, string rconPassword)
    {
        _rcon = new ValveRconClient(ip, port, rconPassword);
    }

    public Task<bool> ConnectAsync(int timeoutMs = 5000) => _rcon.ConnectAsync(timeoutMs);

    public async Task<string> GetStatusRawAsync() => await _rcon.SendCommandAsync("status").ConfigureAwait(false);

    public async Task<(ServerInfo Info, List<PlayerInfo> Players)> GetStatusAsync()
    {
        var raw = await GetStatusRawAsync().ConfigureAwait(false);
        var hostname = ValveStatusParserCS2.GetHostname(raw);
        var map = ValveStatusParserCS2.GetMapName(raw);
        var (humanCount, botCount) = ValveStatusParserCS2.GetPlayerCount(raw);
        var players = ValveStatusParserCS2.GetPlayerList(raw);

        var info = new ServerInfo
        {
            Hostname = hostname,
            Map = map,
            PlayerCount = humanCount,
            BotCount = botCount,
            IsOnline = !string.IsNullOrEmpty(hostname),
            RawResponse = raw,
            QueriedUtc = DateTime.UtcNow
        };

        return (info, players);
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
    public Task<string> EnableCheatsAsync() => _rcon.SendCommandAsync("sv_cheats 1");
    public Task<string> DisableCheatsAsync() => _rcon.SendCommandAsync("sv_cheats 0");
    public Task<string> SetFriendlyFireAsync(bool enable) => _rcon.SendCommandAsync($"mp_friendlyfire {(enable ? 1 : 0)}");
    public Task<string> SetTeamBalanceAsync(bool enable) => _rcon.SendCommandAsync($"mp_autoteambalance {(enable ? 1 : 0)}");
    public Task<string> SlayPlayerAsync(string playerName) => _rcon.SendCommandAsync($"slay {playerName}");

    public void Disconnect() => _rcon.Disconnect();
    public void Dispose() => _rcon.Dispose();
}
