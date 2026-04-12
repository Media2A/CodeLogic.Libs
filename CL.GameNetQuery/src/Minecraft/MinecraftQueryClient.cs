using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CL.GameNetQuery.Minecraft;

/// <summary>
/// UDP query client for Minecraft servers.
/// Implements the Minecraft GameSpy4 query protocol.
/// </summary>
public static class MinecraftQueryClient
{
    private const int TimeoutMs = 3000;
    private const int BufferSize = 8192;

    /// <summary>
    /// Queries a Minecraft server and returns the parsed key-value response.
    /// </summary>
    /// <param name="ip">The server IP address as a string.</param>
    /// <param name="queryPort">The query port number.</param>
    /// <returns>The parsed server response as key-value lines.</returns>
    public static string QueryServer(string ip, int queryPort)
    {
        return QueryServer(IPAddress.Parse(ip), queryPort);
    }

    /// <summary>
    /// Queries a Minecraft server and returns the parsed key-value response.
    /// </summary>
    /// <param name="serverIp">The server IP address.</param>
    /// <param name="queryPort">The query port number.</param>
    /// <returns>The parsed server response as key-value lines.</returns>
    public static string QueryServer(IPAddress serverIp, int queryPort)
    {
        using var udpClient = new UdpClient();
        udpClient.Connect(serverIp, queryPort);
        udpClient.Client.ReceiveTimeout = TimeoutMs;
        udpClient.Client.ReceiveBufferSize = BufferSize;

        try
        {
            var handshake = CreateHandshakeRequest();
            udpClient.Send(handshake, handshake.Length);

            var endPoint = new IPEndPoint(IPAddress.Any, queryPort);
            var challengeResponse = udpClient.Receive(ref endPoint);

            if (challengeResponse.Length < 5)
                return string.Empty;

            var challengeToken = ParseChallengeToken(challengeResponse);
            var queryRequest = CreateQueryRequest(challengeToken);
            udpClient.Send(queryRequest, queryRequest.Length);

            var serverResponse = udpClient.Receive(ref endPoint);
            var response = Encoding.GetEncoding("ISO-8859-1").GetString(serverResponse);

            return ParseServerResponse(response);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets a specific value from a parsed query response.
    /// </summary>
    /// <param name="response">The parsed query response string.</param>
    /// <param name="key">The key to look up.</param>
    /// <returns>The value associated with the key, or empty string if not found.</returns>
    public static string GetStatusValue(string response, string key)
    {
        var cleanResponse = RemoveMinecraftFormatting(response);
        var lines = cleanResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2)
                dict[parts[0].Trim()] = parts[1].Trim();
        }

        return dict.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static byte[] CreateHandshakeRequest() =>
        [0xFE, 0xFD, 0x09, 0x12, 0x34, 0x56, 0x78];

    private static byte[] ParseChallengeToken(byte[] response)
    {
        var tokenString = Encoding.ASCII.GetString(response, 5, response.Length - 5).TrimEnd('\0');
        var challengeInt = int.Parse(tokenString);
        return BitConverter.GetBytes(challengeInt).Reverse().ToArray();
    }

    private static byte[] CreateQueryRequest(byte[] challengeToken)
    {
        var request = new byte[11 + challengeToken.Length];
        request[0] = 0xFE;
        request[1] = 0xFD;
        request[2] = 0x00;
        request[3] = 0x12;
        request[4] = 0x34;
        request[5] = 0x56;
        request[6] = 0x78;
        Array.Copy(challengeToken, 0, request, 7, challengeToken.Length);
        return request;
    }

    private static string ParseServerResponse(string response)
    {
        var clean = RemoveMinecraftFormatting(response);
        var parts = clean.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var dict = new Dictionary<string, string>();
        string? prevKey = null;

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (IsValidKey(trimmed))
            {
                if (prevKey is not null)
                    dict[prevKey] = "(empty)";
                prevKey = trimmed;
            }
            else if (prevKey is not null)
            {
                dict[prevKey] = trimmed;
                prevKey = null;
            }
        }

        if (prevKey is not null)
            dict[prevKey] = "(empty)";

        return string.Join(Environment.NewLine, dict.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    private static bool IsValidKey(string part) =>
        !string.IsNullOrWhiteSpace(part) &&
        char.IsLetter(part[0]) &&
        part.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string RemoveMinecraftFormatting(string input) =>
        Regex.Replace(input, @"[§?&][0-9a-fklmor]", string.Empty, RegexOptions.IgnoreCase);
}
