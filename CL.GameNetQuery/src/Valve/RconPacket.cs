using System.Text;

namespace CL.GameNetQuery.Valve;

/// <summary>
/// Packet types used in Valve RCON communication.
/// </summary>
public enum RconPacketType
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
/// Represents a packet in the Valve RCON protocol.
/// </summary>
public sealed class RconPacket
{
    private static int _nextId;

    private RconPacket() { }

    /// <summary>Gets the total packet size in bytes.</summary>
    public int Size => Math.Max(10, Body.Length + 9);
    /// <summary>Gets the packet identifier.</summary>
    public int Id { get; private init; }
    /// <summary>Gets the packet type.</summary>
    public RconPacketType Type { get; private init; }
    /// <summary>Gets the packet body content.</summary>
    public string Body { get; private init; } = string.Empty;
    /// <summary>Gets whether this is a dummy/sentinel packet.</summary>
    public bool IsDummy => Body.Equals("\u0001") && Id == 0;

    /// <summary>Creates a new RCON packet with the specified id, type, and content.</summary>
    /// <param name="id">The packet identifier.</param>
    /// <param name="type">The packet type.</param>
    /// <param name="content">The packet body content.</param>
    /// <returns>A new <see cref="RconPacket"/> instance.</returns>
    public static RconPacket Create(int id, RconPacketType type, string content) => new()
    {
        Type = type,
        Body = string.IsNullOrEmpty(content) ? "\0" : content,
        Id = id
    };

    /// <summary>Creates a new RCON packet with an auto-incremented id.</summary>
    /// <param name="type">The packet type.</param>
    /// <param name="content">The packet body content.</param>
    /// <returns>A new <see cref="RconPacket"/> instance.</returns>
    public static RconPacket Create(RconPacketType type, string content) => new()
    {
        Type = type,
        Body = string.IsNullOrEmpty(content) ? "\0" : content,
        Id = Interlocked.Increment(ref _nextId)
    };

    /// <summary>Lazy-initialized dummy packet used as a sentinel value.</summary>
    public static readonly Lazy<RconPacket> Dummy = new(() => new RconPacket { Id = 0, Type = RconPacketType.Response });

    /// <summary>Serializes this packet to a byte array for transmission.</summary>
    /// <param name="encoding">The text encoding to use; defaults to ASCII.</param>
    /// <returns>The serialized packet bytes.</returns>
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

    /// <summary>Deserializes an RCON packet from a byte array.</summary>
    /// <param name="buffer">The raw packet bytes.</param>
    /// <param name="encoding">The text encoding to use; defaults to system default.</param>
    /// <returns>The deserialized <see cref="RconPacket"/>.</returns>
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
