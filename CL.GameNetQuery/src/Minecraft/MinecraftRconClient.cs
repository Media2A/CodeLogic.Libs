using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CL.GameNetQuery.Minecraft;

/// <summary>
/// RCON client for Minecraft servers.
/// Uses the same RCON protocol as Valve but with Minecraft-specific packet handling.
/// </summary>
public sealed class MinecraftRconClient : IDisposable
{
    private readonly IPAddress _serverIp;
    private readonly ushort _port;
    private readonly string _password;
    private readonly int _requestId = 1;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;

    /// <summary>Initializes a new Minecraft RCON client with the specified connection details.</summary>
    /// <param name="ip">The server IP address as a string.</param>
    /// <param name="port">The RCON port number.</param>
    /// <param name="password">The RCON password.</param>
    public MinecraftRconClient(string ip, ushort port, string password)
    {
        _serverIp = IPAddress.Parse(ip);
        _port = port;
        _password = password;
    }

    /// <summary>Connects to the Minecraft RCON server and authenticates.</summary>
    /// <param name="timeoutMs">Connection timeout in milliseconds.</param>
    /// <returns><c>true</c> if connection and authentication succeeded.</returns>
    public async Task<bool> ConnectAsync(int timeoutMs = 5000)
    {
        try
        {
            _tcpClient = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await _tcpClient.ConnectAsync(_serverIp, _port, cts.Token).ConfigureAwait(false);
            _networkStream = _tcpClient.GetStream();
            return await AuthenticateAsync().ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sends a command to the Minecraft server and returns the response.</summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>The server response string.</returns>
    public async Task<string> SendCommandAsync(string command)
    {
        if (_networkStream is null)
            throw new InvalidOperationException("Not connected to RCON server.");

        try
        {
            var packet = MinecraftRconPacket.Create(_requestId, MinecraftPacketType.ExecCommand, command);
            var bytes = packet.ToBytes();
            await _networkStream.WriteAsync(bytes).ConfigureAwait(false);

            var responses = await ReadResponsesAsync().ConfigureAwait(false);
            return responses
                .Where(r => r.Type == MinecraftPacketType.Response && r.Id == _requestId)
                .Select(r => r.Body)
                .FirstOrDefault() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Disconnects from the Minecraft RCON server.</summary>
    public void Disconnect()
    {
        _networkStream?.Close();
        _tcpClient?.Close();
        _networkStream = null;
        _tcpClient = null;
    }

    private async Task<bool> AuthenticateAsync()
    {
        try
        {
            var packet = MinecraftRconPacket.Create(_requestId, MinecraftPacketType.Auth, _password);
            var bytes = packet.ToBytes();
            await _networkStream!.WriteAsync(bytes).ConfigureAwait(false);

            var responses = await ReadResponsesAsync().ConfigureAwait(false);
            return responses.Any(r => r.Type == MinecraftPacketType.AuthResponse && r.Id == _requestId);
        }
        catch
        {
            return false;
        }
    }

    private async Task<MinecraftRconPacket[]> ReadResponsesAsync()
    {
        var buffer = new byte[4096];
        var responses = new List<MinecraftRconPacket>();
        var timeoutCounter = 0;

        try
        {
            while (_networkStream!.DataAvailable || responses.Count == 0)
            {
                if (!_networkStream.DataAvailable)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    if (++timeoutCounter > 100) break;
                    continue;
                }

                var bytesRead = await _networkStream.ReadAsync(buffer).ConfigureAwait(false);
                if (bytesRead == 0) break;

                var offset = 0;
                while (offset < bytesRead)
                {
                    if (bytesRead - offset < 4) break;
                    var size = BitConverter.ToInt32(buffer, offset);
                    if (size < 10 || size > buffer.Length || bytesRead - offset < size + 4) break;

                    var packetData = new byte[size + 4];
                    Array.Copy(buffer, offset, packetData, 0, size + 4);
                    responses.Add(MinecraftRconPacket.FromBytes(packetData));
                    offset += size + 4;
                }
            }

            return responses.ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Disposes the client by disconnecting from the server.</summary>
    public void Dispose() => Disconnect();
}

/// <summary>
/// Packet types used in Minecraft RCON communication.
/// </summary>
public enum MinecraftPacketType
{
    /// <summary>A response packet from the server.</summary>
    Response = 0,
    /// <summary>An authentication response packet.</summary>
    AuthResponse = 2,
    /// <summary>A command execution request packet.</summary>
    ExecCommand = 2,
    /// <summary>An authentication request packet.</summary>
    Auth = 3
}

/// <summary>
/// Represents a packet in the Minecraft RCON protocol.
/// </summary>
public sealed class MinecraftRconPacket
{
    private MinecraftRconPacket() { }

    /// <summary>Gets the packet identifier.</summary>
    public int Id { get; private init; }
    /// <summary>Gets the packet type.</summary>
    public MinecraftPacketType Type { get; private init; }
    /// <summary>Gets the packet body content.</summary>
    public string Body { get; private init; } = string.Empty;

    /// <summary>Creates a new Minecraft RCON packet with the specified id, type, and content.</summary>
    /// <param name="id">The packet identifier.</param>
    /// <param name="type">The packet type.</param>
    /// <param name="content">The packet body content.</param>
    /// <returns>A new <see cref="MinecraftRconPacket"/> instance.</returns>
    public static MinecraftRconPacket Create(int id, MinecraftPacketType type, string content) => new()
    {
        Type = type,
        Body = string.IsNullOrEmpty(content) ? "\0" : content,
        Id = id
    };

    /// <summary>Serializes this packet to a byte array for transmission.</summary>
    /// <returns>The serialized packet bytes.</returns>
    public byte[] ToBytes()
    {
        var body = Encoding.ASCII.GetBytes(Body + "\0");
        var buffer = new byte[12 + body.Length + 1];
        var span = buffer.AsSpan();
        body.CopyTo(span[12..]);
        BitConverter.GetBytes(body.Length + 9).CopyTo(span[0..4]);
        BitConverter.GetBytes(Id).CopyTo(span[4..8]);
        BitConverter.GetBytes((int)Type).CopyTo(span[8..12]);
        return buffer;
    }

    /// <summary>Deserializes a Minecraft RCON packet from a byte array.</summary>
    /// <param name="buffer">The raw packet bytes.</param>
    /// <returns>The deserialized <see cref="MinecraftRconPacket"/>.</returns>
    public static MinecraftRconPacket FromBytes(byte[] buffer) => new()
    {
        Type = (MinecraftPacketType)BitConverter.ToInt32(buffer[8..12]),
        Body = Encoding.ASCII.GetString(buffer[12..]).Replace("\0", ""),
        Id = BitConverter.ToInt32(buffer[4..8])
    };
}
