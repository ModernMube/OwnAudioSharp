using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OwnaudioNET.NetworkSync;

/// <summary>
/// Network synchronization protocol with zero-GC guarantees.
/// Defines command types, packet structures, and serialization methods.
/// All hot-path operations are allocation-free using Span and stackalloc.
/// </summary>
public static class NetworkSyncProtocol
{
    /// <summary>
    /// Protocol version for compatibility checking.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Maximum packet size in bytes (256 bytes for all command types).
    /// </summary>
    public const int MaxPacketSize = 256;

    /// <summary>
    /// Magic number for packet validation (0x4F574E41 = "OWNA").
    /// </summary>
    public const uint MagicNumber = 0x4F574E41;

    /// <summary>
    /// Command types for network synchronization.
    /// </summary>
    public enum CommandType : int
    {
        /// <summary>
        /// Clock synchronization update (sent continuously at 100Hz).
        /// </summary>
        ClockSync = 0,

        /// <summary>
        /// Play command with optional start position.
        /// </summary>
        Play = 1,

        /// <summary>
        /// Pause command.
        /// </summary>
        Pause = 2,

        /// <summary>
        /// Stop command.
        /// </summary>
        Stop = 3,

        /// <summary>
        /// Seek to specific position.
        /// </summary>
        Seek = 4,

        /// <summary>
        /// Set tempo/pitch.
        /// </summary>
        Tempo = 5,

        /// <summary>
        /// Ping request for latency measurement.
        /// </summary>
        Ping = 6,

        /// <summary>
        /// Pong response for latency measurement.
        /// </summary>
        Pong = 7,

        /// <summary>
        /// Server announcement for discovery.
        /// </summary>
        ServerAnnouncement = 8,

        /// <summary>
        /// Client handshake request.
        /// </summary>
        ClientHandshake = 9,

        /// <summary>
        /// Server handshake response.
        /// </summary>
        ServerHandshake = 10
    }

    /// <summary>
    /// Connection state for clients.
    /// </summary>
    public enum ConnectionState : int
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Synced = 3
    }

    /// <summary>
    /// Network synchronization command (value type for zero-GC).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Command
    {
        /// <summary>
        /// Command type.
        /// </summary>
        public CommandType Type;

        /// <summary>
        /// NTP timestamp when command was sent (ticks).
        /// </summary>
        public long NtpTimestamp;

        /// <summary>
        /// Scheduled execution time (NTP ticks) - for latency compensation.
        /// </summary>
        public long ScheduledExecutionTime;

        /// <summary>
        /// Master clock timestamp in seconds.
        /// </summary>
        public double MasterClockTimestamp;

        /// <summary>
        /// Master clock sample position.
        /// </summary>
        public long MasterClockSamplePosition;

        /// <summary>
        /// Sample rate (for clock sync).
        /// </summary>
        public int SampleRate;

        /// <summary>
        /// Target position in seconds (for Seek command).
        /// </summary>
        public double TargetPosition;

        /// <summary>
        /// Tempo value (for Tempo command).
        /// </summary>
        public float TempoValue;

        /// <summary>
        /// Use smooth tempo change (for Tempo command).
        /// </summary>
        public bool UseSmooth;

        /// <summary>
        /// Sequence number for ping/pong.
        /// </summary>
        public int SequenceNumber;

        /// <summary>
        /// Client send timestamp for latency measurement.
        /// </summary>
        public long ClientSendTime;
    }

    /// <summary>
    /// Serializes a command to a byte buffer (zero-allocation).
    /// </summary>
    /// <param name="cmd">Command to serialize (passed by reference).</param>
    /// <param name="buffer">Pre-allocated buffer (must be at least MaxPacketSize bytes).</param>
    /// <returns>Number of bytes written.</returns>
    public static int SerializeCommand(ref Command cmd, Span<byte> buffer)
    {
        if (buffer.Length < MaxPacketSize)
            throw new ArgumentException($"Buffer must be at least {MaxPacketSize} bytes", nameof(buffer));

        int offset = 0;

        // Magic number (4 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(offset), MagicNumber);
        offset += 4;

        // Protocol version (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), ProtocolVersion);
        offset += 4;

        // Command type (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), (int)cmd.Type);
        offset += 4;

        // NTP timestamp (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.NtpTimestamp);
        offset += 8;

        // Scheduled execution time (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.ScheduledExecutionTime);
        offset += 8;

        // Master clock timestamp (8 bytes)
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), cmd.MasterClockTimestamp);
        offset += 8;

        // Master clock sample position (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.MasterClockSamplePosition);
        offset += 8;

        // Sample rate (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), cmd.SampleRate);
        offset += 4;

        // Target position (8 bytes)
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), cmd.TargetPosition);
        offset += 8;

        // Tempo value (4 bytes)
        BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset), cmd.TempoValue);
        offset += 4;

        // Use smooth (1 byte)
        buffer[offset] = cmd.UseSmooth ? (byte)1 : (byte)0;
        offset += 1;

        // Sequence number (4 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), cmd.SequenceNumber);
        offset += 4;

        // Client send time (8 bytes)
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), cmd.ClientSendTime);
        offset += 8;

        return offset;
    }

    /// <summary>
    /// Deserializes a command from a byte buffer (zero-allocation).
    /// </summary>
    /// <param name="buffer">Buffer containing serialized command.</param>
    /// <param name="cmd">Output command (passed by reference).</param>
    /// <returns>True if deserialization succeeded, false otherwise.</returns>
    public static bool DeserializeCommand(ReadOnlySpan<byte> buffer, ref Command cmd)
    {
        if (buffer.Length < MaxPacketSize)
            return false;

        int offset = 0;

        // Validate magic number
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(offset));
        if (magic != MagicNumber)
            return false;
        offset += 4;

        // Validate protocol version
        int version = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        if (version != ProtocolVersion)
            return false;
        offset += 4;

        // Command type
        cmd.Type = (CommandType)BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        // NTP timestamp
        cmd.NtpTimestamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        // Scheduled execution time
        cmd.ScheduledExecutionTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        // Master clock timestamp
        cmd.MasterClockTimestamp = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;

        // Master clock sample position
        cmd.MasterClockSamplePosition = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        // Sample rate
        cmd.SampleRate = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        // Target position
        cmd.TargetPosition = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;

        // Tempo value
        cmd.TempoValue = BinaryPrimitives.ReadSingleLittleEndian(buffer.Slice(offset));
        offset += 4;

        // Use smooth
        cmd.UseSmooth = buffer[offset] != 0;
        offset += 1;

        // Sequence number
        cmd.SequenceNumber = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;

        // Client send time
        cmd.ClientSendTime = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        return true;
    }

    /// <summary>
    /// Creates a clock sync command.
    /// </summary>
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
    /// Creates a play command.
    /// </summary>
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
    /// Creates a pause command.
    /// </summary>
    public static Command CreatePauseCommand(
        long ntpTimestamp,
        long scheduledExecutionTime)
    {
        return new Command
        {
            Type = CommandType.Pause,
            NtpTimestamp = ntpTimestamp,
            ScheduledExecutionTime = scheduledExecutionTime
        };
    }

    /// <summary>
    /// Creates a stop command.
    /// </summary>
    public static Command CreateStopCommand(long ntpTimestamp)
    {
        return new Command
        {
            Type = CommandType.Stop,
            NtpTimestamp = ntpTimestamp
        };
    }

    /// <summary>
    /// Creates a seek command.
    /// </summary>
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
    /// Creates a tempo command.
    /// </summary>
    public static Command CreateTempoCommand(
        long ntpTimestamp,
        float tempoValue,
        bool useSmooth)
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
    /// Creates a ping command.
    /// </summary>
    public static Command CreatePingCommand(
        long clientSendTime,
        int sequenceNumber)
    {
        return new Command
        {
            Type = CommandType.Ping,
            ClientSendTime = clientSendTime,
            SequenceNumber = sequenceNumber
        };
    }

    /// <summary>
    /// Creates a pong command.
    /// </summary>
    public static Command CreatePongCommand(
        long clientSendTime,
        int sequenceNumber)
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
