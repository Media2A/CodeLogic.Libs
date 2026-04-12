using System.Text;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// Packet types used in Valve RCON communication.
/// </summary>
public enum RconPacketType
{
    Response = 0,
    AuthResponse = 2,
    ExecCommand = 2,
    Auth = 3
}

/// <summary>
/// Represents a packet in the Valve RCON protocol.
/// </summary>
public sealed class RconPacket
{
    private static int _nextId;

    private RconPacket() { }

    public int Size => Math.Max(10, Body.Length + 9);
    public int Id { get; private init; }
    public RconPacketType Type { get; private init; }
    public string Body { get; private init; } = string.Empty;
    public bool IsDummy => Body.Equals("\u0001") && Id == 0;

    public static RconPacket Create(int id, RconPacketType type, string content) => new()
    {
        Type = type,
        Body = string.IsNullOrEmpty(content) ? "\0" : content,
        Id = id
    };

    public static RconPacket Create(RconPacketType type, string content) => new()
    {
        Type = type,
        Body = string.IsNullOrEmpty(content) ? "\0" : content,
        Id = Interlocked.Increment(ref _nextId)
    };

    public static readonly Lazy<RconPacket> Dummy = new(() => new RconPacket { Id = 0, Type = RconPacketType.Response });

    public byte[] ToBytes(Encoding? encoding = null)
    {
        encoding ??= Encoding.ASCII;
        var body = encoding.GetBytes(Body + "\0");
        var buffer = new byte[12 + body.Length + 1];
        var span = buffer.AsSpan();
        body.CopyTo(span[12..]);
        BitConverter.GetBytes(body.Length + 9).CopyTo(span[0..4]);
        BitConverter.GetBytes(Id).CopyTo(span[4..8]);
        BitConverter.GetBytes((int)Type).CopyTo(span[8..12]);
        return buffer;
    }

    public static RconPacket FromBytes(byte[] buffer, Encoding? encoding = null)
    {
        encoding ??= Encoding.Default;
        return new RconPacket
        {
            Type = (RconPacketType)BitConverter.ToInt32(buffer[8..12]),
            Body = encoding.GetString(buffer[12..]).Replace("\0", ""),
            Id = BitConverter.ToInt32(buffer[4..8])
        };
    }
}
