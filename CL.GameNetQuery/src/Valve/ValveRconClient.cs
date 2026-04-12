using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// RCON client for Valve Source Engine servers (CSS, CS2).
/// Handles authentication, command execution, and response parsing.
/// </summary>
public sealed class ValveRconClient : IDisposable
{
    private readonly IPAddress _serverIp;
    private readonly ushort _port;
    private readonly string _password;
    private readonly int _requestId = 1;
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;

    public ValveRconClient(string ip, ushort port, string password)
    {
        _serverIp = IPAddress.Parse(ip);
        _port = port;
        _password = password;
    }

    public ValveRconClient(IPAddress ip, ushort port, string password)
    {
        _serverIp = ip;
        _port = port;
        _password = password;
    }

    /// <summary>
    /// Connects to the RCON server and authenticates.
    /// </summary>
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

    /// <summary>
    /// Sends a command and returns the response.
    /// </summary>
    public async Task<string> SendCommandAsync(string command)
    {
        if (_networkStream is null)
            throw new InvalidOperationException("Not connected to RCON server.");

        try
        {
            var packet = RconPacket.Create(_requestId, RconPacketType.ExecCommand, command);
            var bytes = packet.ToBytes();
            await _networkStream.WriteAsync(bytes).ConfigureAwait(false);

            var responses = await ReadResponsesAsync().ConfigureAwait(false);
            foreach (var response in responses)
            {
                if (response.Type == RconPacketType.Response && response.Id == _requestId)
                    return response.Body;
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Disconnects from the RCON server.
    /// </summary>
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
            var packet = RconPacket.Create(_requestId, RconPacketType.Auth, _password);
            var bytes = packet.ToBytes();
            await _networkStream!.WriteAsync(bytes).ConfigureAwait(false);

            var responses = await ReadResponsesAsync().ConfigureAwait(false);
            return responses.Any(r => r.Type == RconPacketType.Response && r.Id == _requestId);
        }
        catch
        {
            return false;
        }
    }

    private async Task<RconPacket[]> ReadResponsesAsync()
    {
        var buffer = new byte[4096];
        var responses = new List<RconPacket>();
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
                    responses.Add(RconPacket.FromBytes(packetData));
                    offset += size + 4;
                }
            }

            return responses.ToArray();
        }
        catch
        {
            return [RconPacket.Dummy.Value];
        }
    }

    public void Dispose() => Disconnect();
}
