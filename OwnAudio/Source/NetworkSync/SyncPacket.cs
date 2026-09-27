using System.Buffers.Binary;

namespace OwnaudioNET.NetworkSync;

internal enum SyncPacketKind : byte
{
    Ping     = 1,
    Pong     = 2,
    Position = 3,
    Announce = 4,
}

/// <summary>
/// The one datagram of the sync channel, fixed 64 bytes, little endian. The time fields mean
/// different things per kind:
/// Ping — ClientSent. Pong — ClientSent echoed, ServerReceived, ServerSent.
/// Position — ServerSent is when Position was heard on the server, Rate its speed.
/// Announce — Port is the server's sync port, the rest is empty.
/// </summary>
internal struct SyncPacket
{
    public const int Size = 64;

    private const ushort Magic = 0x5953;
    private const byte Version = 1;

    public SyncPacketKind Kind;

    /// <summary>
    /// Random per server start, so a packet from an earlier run never passes. A client that
    /// doesn't know it yet pings with 0 and takes it from the pong.
    /// </summary>
    public uint Session;

    public uint Sequence;

    /// <summary>
    /// Bumped by every jump on the server. Readings of an older epoch are thrown away.
    /// </summary>
    public uint Epoch;

    public bool Playing;

    public double ClientSent;
    public double ServerReceived;
    public double ServerSent;
    public double Position;
    public double Rate;
    public int Port;

    public readonly void Write(Span<byte> to)
    {
        to.Slice(0, Size).Clear();

        BinaryPrimitives.WriteUInt16LittleEndian(to, Magic);
        to[2] = Version;
        to[3] = (byte)Kind;
        BinaryPrimitives.WriteUInt32LittleEndian(to.Slice(4), Session);
        BinaryPrimitives.WriteUInt32LittleEndian(to.Slice(8), Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(to.Slice(12), Epoch);
        to[16] = Playing ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(to.Slice(20), ClientSent);
        BinaryPrimitives.WriteDoubleLittleEndian(to.Slice(28), ServerReceived);
        BinaryPrimitives.WriteDoubleLittleEndian(to.Slice(36), ServerSent);
        BinaryPrimitives.WriteDoubleLittleEndian(to.Slice(44), Position);
        BinaryPrimitives.WriteDoubleLittleEndian(to.Slice(52), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(to.Slice(60), Port);
    }

    /// <summary>
    /// False for anything that isn't ours or is from another protocol version — the command
    /// packets on the same socket included.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> from, out SyncPacket packet)
    {
        packet = default;
        if (from.Length < Size) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(from) != Magic || from[2] != Version) return false;

        var _kind = (SyncPacketKind)from[3];
        if (_kind is not (SyncPacketKind.Ping or SyncPacketKind.Pong or SyncPacketKind.Position or SyncPacketKind.Announce)) return false;

        packet.Kind           = _kind;
        packet.Session        = BinaryPrimitives.ReadUInt32LittleEndian(from.Slice(4));
        packet.Sequence       = BinaryPrimitives.ReadUInt32LittleEndian(from.Slice(8));
        packet.Epoch          = BinaryPrimitives.ReadUInt32LittleEndian(from.Slice(12));
        packet.Playing        = from[16] != 0;
        packet.ClientSent     = BinaryPrimitives.ReadDoubleLittleEndian(from.Slice(20));
        packet.ServerReceived = BinaryPrimitives.ReadDoubleLittleEndian(from.Slice(28));
        packet.ServerSent     = BinaryPrimitives.ReadDoubleLittleEndian(from.Slice(36));
        packet.Position       = BinaryPrimitives.ReadDoubleLittleEndian(from.Slice(44));
        packet.Rate           = BinaryPrimitives.ReadDoubleLittleEndian(from.Slice(52));
        packet.Port           = BinaryPrimitives.ReadInt32LittleEndian(from.Slice(60));
        return true;
    }
}
