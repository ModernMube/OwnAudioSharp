using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Wire format for the network sync. Command types, the packet layout and its
/// serialize/deserialize. Hot path stays allocation-free via Span/stackalloc.
/// </summary>
public static class NetworkSyncProtocol
{
    /// <summary>
    /// Bumped whenever the packet layout changes; mismatched versions get dropped.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Fixed packet size, every command pads to this.
    /// </summary>
    public const int MaxPacketSize = 256;

    /// <summary>
    /// "OWNA" as a uint, first thing we check on a packet.
    /// </summary>
    public const uint MagicNumber = 0x4F574E41;

    /// <summary>
    /// What kind of command a packet carries.
    /// </summary>
    public enum CommandType : int
    {
        ClockSync = 0,
        Play = 1,
        Pause = 2,
        Stop = 3,
        Seek = 4,
        Tempo = 5,
        Ping = 6,
        Pong = 7,
        ServerAnnouncement = 8,
        ClientHandshake = 9,
        ServerHandshake = 10
    }

    /// <summary>
    /// Where a client sits in the connect/sync lifecycle.
    /// </summary>
    public enum ConnectionState : int
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Synced = 3
    }

    /// <summary>
    /// The command payload. Plain value type so it never touches the heap.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Command
    {
        public CommandType Type;
        public long NtpTimestamp;
        public long ScheduledExecutionTime;
        public double MasterClockTimestamp;
        public long MasterClockSamplePosition;
        public int SampleRate;
        public double TargetPosition;
        public float TempoValue;
        public bool UseSmooth;
        public int SequenceNumber;
        public long ClientSendTime;
    }

    /// <summary>
    /// Packs a command into buffer (needs to be at least MaxPacketSize), returns bytes written.
    /// </summary>
    /// <param name="cmd"></param>
    /// <param name="buffer"></param>
    /// <returns></returns>
    public static int SerializeCommand(ref Command cmd, Span<byte> buffer)
    {
        if (buffer.Length < MaxPacketSize)
            throw new ArgumentException($"Buffer must be at least {MaxPacketSize} bytes", nameof(buffer));

        int offset = 0;

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), MagicNumber);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), ProtocolVersion);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), (int)cmd.Type);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.NtpTimestamp);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.ScheduledExecutionTime);
        offset += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), cmd.MasterClockTimestamp);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.MasterClockSamplePosition);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), cmd.SampleRate);
        offset += 4;
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), cmd.TargetPosition);
        offset += 8;
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset), cmd.TempoValue);
        offset += 4;
        buffer[offset] = cmd.UseSmooth ? (byte)1 : (byte)0;
        offset += 1;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), cmd.SequenceNumber);
        offset += 4;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.ClientSendTime);
        offset += 8;

        return offset;
    }

    /// <summary>
    /// Reads a command back out. False if the magic or version don't line up.
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="cmd"></param>
    /// <returns></returns>
    public static bool DeserializeCommand(ReadOnlySpan<byte> buffer, ref Command cmd)
    {
        if (buffer.Length < MaxPacketSize)
            return false;

        int offset = 0;

        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset)) != MagicNumber)
            return false;
        offset += 4;
        if (BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset)) != ProtocolVersion)
            return false;
        offset += 4;

        cmd.Type = (CommandType)BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        cmd.NtpTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        cmd.ScheduledExecutionTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        cmd.MasterClockTimestamp = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;
        cmd.MasterClockSamplePosition = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        cmd.SampleRate = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        cmd.TargetPosition = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;
        cmd.TempoValue = BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(offset));
        offset += 4;
        cmd.UseSmooth = buffer[offset] != 0;
        offset += 1;
        cmd.SequenceNumber = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        cmd.ClientSendTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));

        return true;
    }

    /// <summary>
    /// Clock tick the server keeps firing at the clients.
    /// </summary>
    /// <param name="ntpTimestamp"></param>
    /// <param name="masterClockTimestamp"></param>
    /// <param name="masterClockSamplePosition"></param>
    /// <param name="sampleRate"></param>
    /// <returns></returns>
    public static Command CreateClockSyncCommand(
        long ntpTimestamp,
        double masterClockTimestamp,
        long masterClockSamplePosition,
        int sampleRate)
    {
        return new Command
        {
            Type = CommandType.ClockSync,
            NtpTimestamp = ntpTimestamp,
            MasterClockTimestamp = masterClockTimestamp,
            MasterClockSamplePosition = masterClockSamplePosition,
            SampleRate = sampleRate
        };
    }

    /// <summary>
    /// Play, optionally from a start position.
    /// </summary>
    /// <param name="ntpTimestamp"></param>
    /// <param name="scheduledExecutionTime"></param>
    /// <param name="startPosition"></param>
    /// <returns></returns>
    public static Command CreatePlayCommand(
        long ntpTimestamp,
        long scheduledExecutionTime,
        double startPosition = 0.0)
    {
        return new Command
        {
            Type = CommandType.Play,
            NtpTimestamp = ntpTimestamp,
            ScheduledExecutionTime = scheduledExecutionTime,
            TargetPosition = startPosition
        };
    }

    /// <summary>
    /// Pause.
    /// </summary>
    /// <param name="ntpTimestamp"></param>
    /// <param name="scheduledExecutionTime"></param>
    /// <returns></returns>
    public static Command CreatePauseCommand(long ntpTimestamp, long scheduledExecutionTime)
    {
        return new Command
        {
            Type = CommandType.Pause,
            NtpTimestamp = ntpTimestamp,
            ScheduledExecutionTime = scheduledExecutionTime
        };
    }

    /// <summary>
    /// Stop.
    /// </summary>
    /// <param name="ntpTimestamp"></param>
    /// <returns></returns>
    public static Command CreateStopCommand(long ntpTimestamp)
    {
        return new Command { Type = CommandType.Stop, NtpTimestamp = ntpTimestamp };
    }

    /// <summary>
    /// Seek to a target position.
    /// </summary>
    /// <param name="ntpTimestamp"></param>
    /// <param name="scheduledExecutionTime"></param>
    /// <param name="targetPosition"></param>
    /// <returns></returns>
    public static Command CreateSeekCommand(
        long ntpTimestamp,
        long scheduledExecutionTime,
        double targetPosition)
    {
        return new Command
        {
            Type = CommandType.Seek,
            NtpTimestamp = ntpTimestamp,
            ScheduledExecutionTime = scheduledExecutionTime,
            TargetPosition = targetPosition
        };
    }

    /// <summary>
    /// Tempo change, smooth flag decides if it ramps.
    /// </summary>
    /// <param name="ntpTimestamp"></param>
    /// <param name="tempoValue"></param>
    /// <param name="useSmooth"></param>
    /// <returns></returns>
    public static Command CreateTempoCommand(long ntpTimestamp, float tempoValue, bool useSmooth)
    {
        return new Command
        {
            Type = CommandType.Tempo,
            NtpTimestamp = ntpTimestamp,
            TempoValue = tempoValue,
            UseSmooth = useSmooth
        };
    }

    /// <summary>
    /// Ping for measuring latency, carries the client send time.
    /// </summary>
    /// <param name="clientSendTime"></param>
    /// <param name="sequenceNumber"></param>
    /// <returns></returns>
    public static Command CreatePingCommand(long clientSendTime, int sequenceNumber)
    {
        return new Command
        {
            Type = CommandType.Ping,
            ClientSendTime = clientSendTime,
            SequenceNumber = sequenceNumber
        };
    }

    /// <summary>
    /// Pong bounced back at the client, echoes the ping's send time.
    /// </summary>
    /// <param name="clientSendTime"></param>
    /// <param name="sequenceNumber"></param>
    /// <returns></returns>
    public static Command CreatePongCommand(long clientSendTime, int sequenceNumber)
    {
        return new Command
        {
            Type = CommandType.Pong,
            ClientSendTime = clientSendTime,
            SequenceNumber = sequenceNumber,
            NtpTimestamp = DateTime.UtcNow.Ticks
        };
    }
}
