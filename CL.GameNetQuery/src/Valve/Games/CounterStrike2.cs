using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve.Games;

/// <summary>
/// Counter-Strike 2 server interaction via RCON.
/// Uses CS2-specific status parsing (different player/map format from CSS).
/// </summary>
public sealed class CounterStrike2 : IDisposable
{
    private readonly ValveRconClient _rcon;

    /// <summary>Initializes a new CS2 server client with the specified connection details.</summary>
    /// <param name="ip">The server IP address.</param>
    /// <param name="port">The RCON port number.</param>
    /// <param name="rconPassword">The RCON password.</param>
    public CounterStrike2(string ip, ushort port, string rconPassword)
    {
        _rcon = new ValveRconClient(ip, port, rconPassword);
    }

    /// <summary>Connects to the CS2 RCON server and authenticates.</summary>
    /// <param name="timeoutMs">Connection timeout in milliseconds.</param>
    /// <returns><c>true</c> if connection and authentication succeeded.</returns>
    public Task<bool> ConnectAsync(int timeoutMs = 5000) => _rcon.ConnectAsync(timeoutMs);

    /// <summary>Gets the raw status command output from the server.</summary>
    /// <returns>The raw status response string.</returns>
    public async Task<string> GetStatusRawAsync() => await _rcon.SendCommandAsync("status").ConfigureAwait(false);

    /// <summary>Gets parsed server info and player list from the status command.</summary>
    /// <returns>A tuple containing server information and the player list.</returns>
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

    /// <summary>Changes the current map on the server.</summary>
    /// <param name="mapName">The name of the map to change to.</param>
    /// <returns>The server response.</returns>
    public Task<string> ChangeMapAsync(string mapName) => _rcon.SendCommandAsync($"changelevel {mapName}");
    /// <summary>Kicks a player from the server.</summary>
    /// <param name="playerName">The name of the player to kick.</param>
    /// <returns>The server response.</returns>
    public Task<string> KickPlayerAsync(string playerName) => _rcon.SendCommandAsync($"kick {playerName}");
    /// <summary>Permanently bans a player from the server.</summary>
    /// <param name="playerName">The name of the player to ban.</param>
    /// <returns>The server response.</returns>
    public Task<string> BanPlayerAsync(string playerName) => _rcon.SendCommandAsync($"banid 0 {playerName}");
    /// <summary>Removes a ban for a player.</summary>
    /// <param name="playerName">The name of the player to unban.</param>
    /// <returns>The server response.</returns>
    public Task<string> UnbanPlayerAsync(string playerName) => _rcon.SendCommandAsync($"removeid {playerName}");
    /// <summary>Sets the server hostname.</summary>
    /// <param name="name">The new hostname.</param>
    /// <returns>The server response.</returns>
    public Task<string> SetHostnameAsync(string name) => _rcon.SendCommandAsync($"hostname \"{name}\"");
    /// <summary>Executes a server config file.</summary>
    /// <param name="configName">The config file name to execute.</param>
    /// <returns>The server response.</returns>
    public Task<string> ExecConfigAsync(string configName) => _rcon.SendCommandAsync($"exec {configName}");
    /// <summary>Sets a console variable to the specified value.</summary>
    /// <param name="cvar">The console variable name.</param>
    /// <param name="value">The value to set.</param>
    /// <returns>The server response.</returns>
    public Task<string> SetCvarAsync(string cvar, string value) => _rcon.SendCommandAsync($"{cvar} {value}");
    /// <summary>Gets the current value of a console variable.</summary>
    /// <param name="cvar">The console variable name.</param>
    /// <returns>The server response containing the variable value.</returns>
    public Task<string> GetCvarAsync(string cvar) => _rcon.SendCommandAsync(cvar);
    /// <summary>Restarts the current map.</summary>
    /// <returns>The server response.</returns>
    public Task<string> RestartAsync() => _rcon.SendCommandAsync("restart");
    /// <summary>Sends a chat message to all players on the server.</summary>
    /// <param name="message">The message to send.</param>
    /// <returns>The server response.</returns>
    public Task<string> SayAsync(string message) => _rcon.SendCommandAsync($"say {message}");
    /// <summary>Enables cheats on the server.</summary>
    /// <returns>The server response.</returns>
    public Task<string> EnableCheatsAsync() => _rcon.SendCommandAsync("sv_cheats 1");
    /// <summary>Disables cheats on the server.</summary>
    /// <returns>The server response.</returns>
    public Task<string> DisableCheatsAsync() => _rcon.SendCommandAsync("sv_cheats 0");
    /// <summary>Enables or disables friendly fire.</summary>
    /// <param name="enable">Whether to enable friendly fire.</param>
    /// <returns>The server response.</returns>
    public Task<string> SetFriendlyFireAsync(bool enable) => _rcon.SendCommandAsync($"mp_friendlyfire {(enable ? 1 : 0)}");
    /// <summary>Enables or disables automatic team balancing.</summary>
    /// <param name="enable">Whether to enable team balancing.</param>
    /// <returns>The server response.</returns>
    public Task<string> SetTeamBalanceAsync(bool enable) => _rcon.SendCommandAsync($"mp_autoteambalance {(enable ? 1 : 0)}");
    /// <summary>Slays (kills) a player on the server.</summary>
    /// <param name="playerName">The name of the player to slay.</param>
    /// <returns>The server response.</returns>
    public Task<string> SlayPlayerAsync(string playerName) => _rcon.SendCommandAsync($"slay {playerName}");

    /// <summary>Disconnects from the RCON server.</summary>
    public void Disconnect() => _rcon.Disconnect();
    /// <summary>Disposes the client and disconnects from the server.</summary>
    public void Dispose() => _rcon.Dispose();
}
