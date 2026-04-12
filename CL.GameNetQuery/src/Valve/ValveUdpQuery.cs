using System.Net;
using System.Net.Sockets;
using System.Text;
using CL.GameNetQuery.Models;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// UDP-based query for Valve Source Engine servers.
/// Implements A2S_INFO and A2S_PLAYER protocols — no RCON password needed.
/// </summary>
public static class ValveUdpQuery
{
    private const int DefaultTimeout = 3000;

    /// <summary>
    /// Sends an A2S_INFO query to get basic server info.
    /// </summary>
    public static async Task<ServerInfo?> GetServerInfoAsync(string ip, ushort port, int timeoutMs = DefaultTimeout)
    {
        try
        {
            using var client = new UdpClient { Client = { ReceiveTimeout = timeoutMs } };
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

            var request = BuildInfoRequest();
            await client.SendAsync(request, request.Length, endpoint).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(timeoutMs);
            var result = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return ParseInfoResponse(result.Buffer, ip, port);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends an A2S_PLAYER query to get the player list with scores and durations.
    /// </summary>
    public static async Task<List<PlayerInfo>> GetPlayerListAsync(string ip, ushort port, int timeoutMs = DefaultTimeout)
    {
        try
        {
            using var client = new UdpClient { Client = { ReceiveTimeout = timeoutMs } };
            var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

            var challengeRequest = BuildPlayerRequest(-1);
            await client.SendAsync(challengeRequest, challengeRequest.Length, endpoint).ConfigureAwait(false);

            using var cts1 = new CancellationTokenSource(timeoutMs);
            var challengeResp = await client.ReceiveAsync(cts1.Token).ConfigureAwait(false);
            var challengeNumber = BitConverter.ToInt32(challengeResp.Buffer, 5);

            var playerRequest = BuildPlayerRequest(challengeNumber);
            await client.SendAsync(playerRequest, playerRequest.Length, endpoint).ConfigureAwait(false);

            using var cts2 = new CancellationTokenSource(timeoutMs);
            var playerResp = await client.ReceiveAsync(cts2.Token).ConfigureAwait(false);
            return ParsePlayerResponse(playerResp.Buffer);
        }
        catch
        {
            return [];
        }
    }

    private static byte[] BuildInfoRequest()
    {
        var payload = new List<byte>();
        payload.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        payload.Add(0x54); // A2S_INFO
        payload.AddRange(Encoding.UTF8.GetBytes("Source Engine Query\0"));
        return payload.ToArray();
    }

    private static byte[] BuildPlayerRequest(int challenge)
    {
        var payload = new List<byte>();
        payload.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        payload.Add(0x55); // A2S_PLAYER
        payload.AddRange(BitConverter.GetBytes(challenge));
        return payload.ToArray();
    }

    private static ServerInfo ParseInfoResponse(byte[] data, string ip, int port)
    {
        var index = 6; // Skip header (4) + type (1) + protocol (1)
        var name = ReadNullTerminatedString(data, ref index);
        var map = ReadNullTerminatedString(data, ref index);
        var folder = ReadNullTerminatedString(data, ref index);
        var game = ReadNullTerminatedString(data, ref index);

        var steamAppId = data.Length > index + 1 ? BitConverter.ToInt16(data, index) : (short)0;
        index += 2;

        var playerCount = data.Length > index ? data[index++] : 0;
        var maxPlayers = data.Length > index ? data[index++] : 0;
        var botCount = data.Length > index ? data[index++] : 0;

        return new ServerInfo
        {
            Hostname = name,
            Map = map,
            PlayerCount = playerCount,
            MaxPlayers = maxPlayers,
            BotCount = botCount,
            Ip = ip,
            Port = port,
            IsOnline = true,
            QueriedUtc = DateTime.UtcNow
        };
    }

    private static List<PlayerInfo> ParsePlayerResponse(byte[] data)
    {
        var players = new List<PlayerInfo>();
        var index = 5; // Skip header + type
        var playerCount = data[index++];

        for (var i = 0; i < playerCount && index < data.Length; i++)
        {
            index++; // Skip player index byte
            var name = ReadNullTerminatedString(data, ref index);

            var score = index + 4 <= data.Length ? BitConverter.ToInt32(data, index) : 0;
            index += 4;

            var duration = index + 4 <= data.Length ? BitConverter.ToSingle(data, index) : 0f;
            index += 4;

            players.Add(new PlayerInfo
            {
                Name = name,
                Score = score,
                Duration = duration
            });
        }

        return players;
    }

    private static string ReadNullTerminatedString(byte[] data, ref int index)
    {
        var start = index;
        while (index < data.Length && data[index] != 0x00)
            index++;

        var result = Encoding.UTF8.GetString(data, start, index - start);
        index++; // Skip null byte
        return result;
    }
}
