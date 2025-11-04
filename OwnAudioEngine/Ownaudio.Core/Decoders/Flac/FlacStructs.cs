using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Decoders.Flac;

/// <summary>
/// FLAC file marker (4 bytes): "fLaC"
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FlacMarker
{
    public uint Signature; // "fLaC" = 0x664C6143

    public const uint FLAC_SIGNATURE = 0x43614C66; // "fLaC" in little-endian

    public bool IsValid => Signature == FLAC_SIGNATURE;
}

/// <summary>
/// FLAC metadata block header (4 bytes)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FlacMetadataBlockHeader
{
    public byte TypeAndLastFlag; // Bit 7: Last-metadata-block flag, Bits 6-0: Block type
    public byte Length0;         // Length in bytes (24-bit big-endian)
    public byte Length1;
    public byte Length2;

    public bool IsLast => (TypeAndLastFlag & 0x80) != 0;
    public FlacMetadataBlockType Type => (FlacMetadataBlockType)(TypeAndLastFlag & 0x7F);
    public int Length => (Length0 << 16) | (Length1 << 8) | Length2;
}

/// <summary>
/// FLAC metadata block types
/// </summary>
internal enum FlacMetadataBlockType : byte
{
    StreamInfo = 0,
    Padding = 1,
    Application = 2,
    SeekTable = 3,
    VorbisComment = 4,
    CueSheet = 5,
    Picture = 6,
    Invalid = 127
}

/// <summary>
/// FLAC STREAMINFO metadata block (34 bytes)
/// Contains essential stream properties
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FlacStreamInfo
{
    public ushort MinBlockSize;    // Big-endian
    public ushort MaxBlockSize;    // Big-endian
    public byte FrameSize0;        // 24-bit big-endian min frame size
    public byte FrameSize1;
    public byte FrameSize2;
    public byte MaxFrameSize0;     // 24-bit big-endian max frame size
    public byte MaxFrameSize1;
    public byte MaxFrameSize2;

    // 20 bits: Sample rate (Hz)
    // 3 bits: Channels - 1
    // 5 bits: Bits per sample - 1
    // 36 bits: Total samples
    // All packed into 8 bytes
    public ulong PackedData1;      // Big-endian packed data

    // MD5 signature (16 bytes)
    public ulong MD5_High;
    public ulong MD5_Low;

    public int MinBlockSizeValue => (MinBlockSize >> 8) | ((MinBlockSize & 0xFF) << 8);
    public int MaxBlockSizeValue => (MaxBlockSize >> 8) | ((MaxBlockSize & 0xFF) << 8);
    public int MinFrameSize => (FrameSize0 << 16) | (FrameSize1 << 8) | FrameSize2;
    public int MaxFrameSize => (MaxFrameSize0 << 16) | (MaxFrameSize1 << 8) | MaxFrameSize2;

    // Extract sample rate (20 bits, big-endian)
    public int SampleRate
    {
        get
        {
            ulong packed = ReverseBytes(PackedData1);
            return (int)((packed >> 44) & 0xFFFFF);
        }
    }

    // Extract channels (3 bits)
    public int Channels
    {
        get
        {
            ulong packed = ReverseBytes(PackedData1);
            return (int)(((packed >> 41) & 0x7) + 1);
        }
    }

    // Extract bits per sample (5 bits)
    public int BitsPerSample
    {
        get
        {
            ulong packed = ReverseBytes(PackedData1);
            return (int)(((packed >> 36) & 0x1F) + 1);
        }
    }

    // Extract total samples (36 bits)
    public long TotalSamples
    {
        get
        {
            ulong packed = ReverseBytes(PackedData1);
            return (long)(packed & 0xFFFFFFFFF);
        }
    }

    private static ulong ReverseBytes(ulong value)
    {
        return ((value & 0x00000000000000FFUL) << 56) |
               ((value & 0x000000000000FF00UL) << 40) |
               ((value & 0x0000000000FF0000UL) << 24) |
               ((value & 0x00000000FF000000UL) << 8) |
               ((value & 0x000000FF00000000UL) >> 8) |
               ((value & 0x0000FF0000000000UL) >> 24) |
               ((value & 0x00FF000000000000UL) >> 40) |
               ((value & 0xFF00000000000000UL) >> 56);
    }
}

/// <summary>
/// FLAC frame header structure (variable size)
/// </summary>
internal struct FlacFrameHeader
{
    public ushort Sync;            // Sync code: 0x3FFE (14 bits) + reserved (1 bit) + blocking strategy (1 bit)
    public byte BlockSizeSpec;     // 4 bits: block size, 4 bits: sample rate
    public byte ChannelSpec;       // 4 bits: channel assignment, 3 bits: sample size, 1 bit: reserved

    public long SampleOrFrameNumber; // Variable length
    public int BlockSize;            // Calculated from spec
    public int SampleRate;           // Calculated from spec
    public int Channels;             // Calculated from spec
    public int BitsPerSample;        // Calculated from spec
    public byte CRC8;                // Header CRC

    public FlacChannelAssignment ChannelAssignment;
}

/// <summary>
/// FLAC channel assignment types
/// </summary>
internal enum FlacChannelAssignment
{
    Independent = 0,    // All channels independent
    LeftSide = 8,       // Left + Side (difference)
    RightSide = 9,      // Right + Side (difference)
    MidSide = 10        // Mid + Side (average and difference)
}

/// <summary>
/// FLAC subframe header
/// </summary>
internal struct FlacSubframeHeader
{
    public byte Header;           // 1 bit: zero, 6 bits: subframe type, 1 bit: wasted bits flag

    public FlacSubframeType Type;
    public int WastedBits;
    public int Order;             // For LPC/Fixed predictors
}

/// <summary>
/// FLAC subframe types
/// </summary>
internal enum FlacSubframeType
{
    Constant = 0,
    Verbatim = 1,
    Fixed = 2,
    LPC = 3
}

/// <summary>
/// FLAC residual coding method
/// </summary>
internal enum FlacResidualCodingMethod
{
    Rice = 0,
    Rice2 = 1
}

/// <summary>
/// FLAC SEEKTABLE seekpoint entry (18 bytes)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FlacSeekPoint
{
    public ulong SampleNumber;     // Big-endian: first sample in target frame
    public ulong StreamOffset;     // Big-endian: byte offset from first frame
    public ushort FrameSamples;    // Big-endian: number of samples in target frame

    // Placeholder seekpoint marker (indicates invalid entry)
    public const ulong PLACEHOLDER_POINT = 0xFFFFFFFFFFFFFFFF;

    public bool IsPlaceholder => SampleNumber == PLACEHOLDER_POINT;

    public long GetSampleNumber()
    {
        return (long)ReverseBytes(SampleNumber);
    }

    public long GetStreamOffset()
    {
        return (long)ReverseBytes(StreamOffset);
    }

    public int GetFrameSamples()
    {
        return (int)((FrameSamples >> 8) | ((FrameSamples & 0xFF) << 8));
    }

    private static ulong ReverseBytes(ulong value)
    {
        return ((value & 0x00000000000000FFUL) << 56) |
               ((value & 0x000000000000FF00UL) << 40) |
               ((value & 0x0000000000FF0000UL) << 24) |
               ((value & 0x00000000FF000000UL) << 8) |
               ((value & 0x000000FF00000000UL) >> 8) |
               ((value & 0x0000FF0000000000UL) >> 24) |
               ((value & 0x00FF000000000000UL) >> 40) |
               ((value & 0xFF00000000000000UL) >> 56);
    }
}
