using System.Text;
using CL.GameNetQuery;
using CL.GameNetQuery.Minecraft;
using CL.GameNetQuery.Valve;
using CodeLogic.Framework.Libraries; // HealthStatusLevel
using Xunit;

namespace GameNetQuery.Tests;

// Offline unit tests for CL.GameNetQuery. Everything here is pure: the status PARSERS
// operate on raw console strings, and the RCON packet types are value serialization.
// The live UDP/RCON query clients need a real game server and are intentionally NOT tested.

public class ValveStatusParserTests
{
    // A realistic Source-engine (CSS) `status` console dump. The fixture is derived directly
    // from the regexes in ValveStatusParser: player lines use the classic
    // `# userid "name" uniqueid connected ping loss state adr` shape with the name in quotes.
    private const string CssStatus =
        "hostname: My Awesome CSS Server\n" +
        "version : 1.0.0.34/24 6867 secure\n" +
        "udp/ip  : 192.168.1.50:27015  (public ip: 203.0.113.7)\n" +
        "map     : de_dust2 at: 0 x, 0 y, 0 z\n" +
        "tags    : alltalk,nocheats,classic\n" +
        "players : 2 humans, 1 bots (10/0 max)\n" +
        "# userid name uniqueid connected ping loss state adr\n" +
        "#  2 \"Alice\" STEAM_1:0:11101 01:23 45 0 active 198.51.100.2:27005\n" +
        "#  3 \"Bob\" STEAM_1:0:22202 12:00 67 1 active 198.51.100.3:27005\n" +
        "#  4 \"HALReplica\" BOT active\n";

    [Fact]
    public void GetHostname_extracts_name()
        => Assert.Equal("My Awesome CSS Server", ValveStatusParser.GetHostname(CssStatus));

    [Fact]
    public void GetMapName_extracts_map()
        => Assert.Equal("de_dust2", ValveStatusParser.GetMapName(CssStatus));

    [Fact]
    public void GetPlayerCount_returns_humans_and_bots()
    {
        var (humans, bots) = ValveStatusParser.GetPlayerCount(CssStatus);
        Assert.Equal(2, humans);
        Assert.Equal(1, bots);
    }

    [Fact]
    public void GetServerAddress_returns_ip_and_port()
    {
        var (ip, port) = ValveStatusParser.GetServerAddress(CssStatus);
        Assert.Equal("192.168.1.50", ip);
        Assert.Equal(27015, port);
    }

    [Fact]
    public void GetVersion_extracts_version()
        => Assert.Equal("1.0.0.34/24", ValveStatusParser.GetVersion(CssStatus));

    [Fact]
    public void GetTags_splits_on_comma()
    {
        var tags = ValveStatusParser.GetTags(CssStatus);
        Assert.Equal(new[] { "alltalk", "nocheats", "classic" }, tags);
    }

    [Fact]
    public void GetPlayerList_parses_names_and_bot_flag()
    {
        var players = ValveStatusParser.GetPlayerList(CssStatus);
        Assert.Equal(3, players.Count);

        var names = players.Select(p => p.Name).ToList();
        Assert.Contains("Alice", names);
        Assert.Contains("Bob", names);
        Assert.Contains("HALReplica", names);

        var alice = players.Single(p => p.Name == "Alice");
        Assert.Equal("STEAM_1:0:11101", alice.UniqueId);
        Assert.Equal(45, alice.Ping);
        Assert.False(alice.IsBot);

        var bot = players.Single(p => p.Name == "HALReplica");
        Assert.True(bot.IsBot);
        Assert.Equal("BOT", bot.UniqueId);
    }

    [Fact]
    public void ParseStatus_populates_server_info()
    {
        var info = ValveStatusParser.ParseStatus(CssStatus);
        Assert.Equal("My Awesome CSS Server", info.Hostname);
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal(2, info.PlayerCount);
        Assert.Equal(1, info.BotCount);
        Assert.Equal("192.168.1.50", info.Ip);
        Assert.Equal(27015, info.Port);
        Assert.True(info.IsOnline);
        Assert.Equal(CssStatus, info.RawResponse);
    }

    [Fact]
    public void ParseStatusWithPlayers_returns_info_and_players()
    {
        var (info, players) = ValveStatusParser.ParseStatusWithPlayers(CssStatus);
        Assert.Equal("de_dust2", info.Map);
        Assert.Equal(3, players.Count);
    }

    [Fact]
    public void Empty_input_yields_sane_defaults()
    {
        Assert.Equal(string.Empty, ValveStatusParser.GetHostname(""));
        Assert.Equal(string.Empty, ValveStatusParser.GetMapName(""));
        Assert.Equal((0, 0), ValveStatusParser.GetPlayerCount(""));
        Assert.Equal((string.Empty, 0), ValveStatusParser.GetServerAddress(""));
        Assert.Empty(ValveStatusParser.GetTags(""));
        Assert.Empty(ValveStatusParser.GetPlayerList(""));

        var info = ValveStatusParser.ParseStatus("");
        Assert.False(info.IsOnline);
    }
}

public class ValveStatusParserCS2Tests
{
    // CS2 `status` output. Player rows match the CS2 regex:
    //   \s+(\d+)\s+(BOT|\d+)\s+(\d+)\s+(\d+)\s+(\w+)\s+(\d+)\s+'(.+?)'
    //   => id, steamid-or-BOT, ping, loss, state(\w+), <num>, 'name'
    // Map/spawngroup rows match the "loaded spawngroup(...): SV: [N: name | main lump" shape.
    private const string Cs2Status =
        "hostname: CS2 Dedicated Server\n" +
        "players : 1 humans, 1 bots (12/0 max)\n" +
        "loaded spawngroup(  1): SV:  [1: de_inferno | main lump ]\n" +
        "id              steamid                 name\n" +
        "  0 1234567890 25 0 active 0 'Carol'\n" +
        "  1 BOT 0 0 active 0 'Dave'\n";

    [Fact]
    public void GetHostname_works()
        => Assert.Equal("CS2 Dedicated Server", ValveStatusParserCS2.GetHostname(Cs2Status));

    [Fact]
    public void GetMapName_reads_spawngroup()
        => Assert.Equal("de_inferno", ValveStatusParserCS2.GetMapName(Cs2Status));

    [Fact]
    public void GetPlayerCount_works()
    {
        var (humans, bots) = ValveStatusParserCS2.GetPlayerCount(Cs2Status);
        Assert.Equal(1, humans);
        Assert.Equal(1, bots);
    }

    [Fact]
    public void GetSpawngroups_lists_loaded_groups()
    {
        var groups = ValveStatusParserCS2.GetSpawngroups(Cs2Status);
        Assert.Contains("de_inferno", groups);
    }

    [Fact]
    public void GetPlayerList_parses_cs2_rows()
    {
        var players = ValveStatusParserCS2.GetPlayerList(Cs2Status);
        var names = players.Select(p => p.Name).ToList();
        Assert.Contains("Carol", names);
        Assert.Contains("Dave", names);

        var dave = players.Single(p => p.Name == "Dave");
        Assert.True(dave.IsBot);

        var carol = players.Single(p => p.Name == "Carol");
        Assert.False(carol.IsBot);
        Assert.Equal(25, carol.Ping);
    }

    [Fact]
    public void Empty_input_does_not_throw_and_returns_defaults()
    {
        Assert.Equal(string.Empty, ValveStatusParserCS2.GetHostname(""));
        Assert.Equal(string.Empty, ValveStatusParserCS2.GetMapName(""));
        Assert.Empty(ValveStatusParserCS2.GetSpawngroups(""));
        Assert.Empty(ValveStatusParserCS2.GetPlayerList(""));
    }
}

public class RconPacketTests
{
    [Fact]
    public void ToBytes_FromBytes_round_trips()
    {
        var packet = RconPacket.Create(42, RconPacketType.ExecCommand, "status");
        var bytes = packet.ToBytes();
        var back = RconPacket.FromBytes(bytes);

        Assert.Equal(42, back.Id);
        Assert.Equal(RconPacketType.ExecCommand, back.Type);
        Assert.Equal("status", back.Body);
    }

    [Fact]
    public void Round_trip_with_explicit_encoding()
    {
        var packet = RconPacket.Create(7, RconPacketType.Auth, "p@ssw0rd");
        var back = RconPacket.FromBytes(packet.ToBytes(Encoding.ASCII), Encoding.ASCII);

        Assert.Equal(7, back.Id);
        Assert.Equal(RconPacketType.Auth, back.Type);
        Assert.Equal("p@ssw0rd", back.Body);
    }

    [Fact]
    public void First_four_bytes_encode_remaining_length()
    {
        var packet = RconPacket.Create(1, RconPacketType.Response, "hi");
        var bytes = packet.ToBytes();
        var declaredSize = BitConverter.ToInt32(bytes, 0);
        // Size field is the byte count following itself: total length minus the 4 size bytes.
        Assert.Equal(bytes.Length - 4, declaredSize);
    }

    [Fact]
    public void Empty_content_is_substituted_with_null_terminator()
    {
        var packet = RconPacket.Create(5, RconPacketType.ExecCommand, "");
        // Empty content becomes "\0"; after FromBytes strips nulls the body is empty again.
        var back = RconPacket.FromBytes(packet.ToBytes());
        Assert.Equal(5, back.Id);
        Assert.Equal(string.Empty, back.Body);
    }
}

public class MinecraftRconPacketTests
{
    [Fact]
    public void ToBytes_FromBytes_round_trips()
    {
        var packet = MinecraftRconPacket.Create(99, MinecraftPacketType.ExecCommand, "list");
        var back = MinecraftRconPacket.FromBytes(packet.ToBytes());

        Assert.Equal(99, back.Id);
        Assert.Equal(MinecraftPacketType.ExecCommand, back.Type);
        Assert.Equal("list", back.Body);
    }

    [Fact]
    public void Auth_packet_round_trips()
    {
        var packet = MinecraftRconPacket.Create(3, MinecraftPacketType.Auth, "secret");
        var back = MinecraftRconPacket.FromBytes(packet.ToBytes());

        Assert.Equal(3, back.Id);
        Assert.Equal(MinecraftPacketType.Auth, back.Type);
        Assert.Equal("secret", back.Body);
    }
}

public class GameNetQueryLibraryTests
{
    [Fact]
    public async Task HealthCheck_is_healthy()
    {
        var lib = new GameNetQueryLibrary();
        var health = await lib.HealthCheckAsync();
        Assert.Equal(HealthStatusLevel.Healthy, health.Status);
    }

    [Fact]
    public void Manifest_identifies_the_library()
    {
        var lib = new GameNetQueryLibrary();
        Assert.Equal("cl.gamenetquery", lib.Manifest.Id);
        Assert.Equal("CL.GameNetQuery", lib.Manifest.Name);
    }
}
