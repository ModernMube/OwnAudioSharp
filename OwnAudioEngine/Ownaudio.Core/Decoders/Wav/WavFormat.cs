using System.Runtime.InteropServices;

namespace Ownaudio.Decoders.Wav;

/// <summary>
/// RIFF chunk header structure.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RiffChunk
{
    /// <summary>
    /// Chunk ID - should be "RIFF" (0x46464952).
    /// </summary>
    public uint ChunkID;

    /// <summary>
    /// Size of the chunk data (file size - 8).
    /// </summary>
    public uint ChunkSize;

    /// <summary>
    /// Format - should be "WAVE" (0x45564157).
    /// </summary>
    public uint Format;

    public const uint RIFF_ID = 0x46464952; // "RIFF"
    public const uint WAVE_ID = 0x45564157; // "WAVE"
}

/// <summary>
/// WAV format chunk structure (WAVEFORMATEX).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WavFormatChunk
{
    /// <summary>
    /// Audio format code.
    /// 1 = PCM, 3 = IEEE_FLOAT, 2 = ADPCM, etc.
    /// </summary>
    public ushort AudioFormat;

    /// <summary>
    /// Number of audio channels (1 = mono, 2 = stereo, etc.).
    /// </summary>
    public ushort Channels;

    /// <summary>
    /// Sample rate in Hz (e.g., 44100, 48000).
    /// </summary>
    public uint SampleRate;

    /// <summary>
    /// Byte rate (SampleRate * Channels * BitsPerSample / 8).
    /// </summary>
    public uint ByteRate;

    /// <summary>
    /// Block align (Channels * BitsPerSample / 8).
    /// </summary>
    public ushort BlockAlign;

    /// <summary>
    /// Bits per sample (8, 16, 24, 32, etc.).
    /// </summary>
    public ushort BitsPerSample;

    // Note: ExtraSize field is optional and handled separately

    public const ushort WAVE_FORMAT_PCM = 1;
    public const ushort WAVE_FORMAT_ADPCM = 2;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 3;
    public const ushort WAVE_FORMAT_ALAW = 6;
    public const ushort WAVE_FORMAT_MULAW = 7;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
}

/// <summary>
/// Data chunk header structure.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct DataChunk
{
    /// <summary>
    /// Chunk ID - should be "data" (0x61746164).
    /// </summary>
    public uint ChunkID;

    /// <summary>
    /// Size of the audio data in bytes.
    /// </summary>
    public uint ChunkSize;

    public const uint DATA_ID = 0x61746164; // "data"
}

/// <summary>
/// Generic chunk header structure for parsing WAV chunks.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ChunkHeader
{
    /// <summary>
    /// Four-character chunk identifier.
    /// </summary>
    public uint ChunkID;

    /// <summary>
    /// Size of the chunk data.
    /// </summary>
    public uint ChunkSize;

    public const uint FMT_ID = 0x20746D66;  // "fmt "
    public const uint FACT_ID = 0x74636166; // "fact"
    public const uint LIST_ID = 0x5453494C; // "LIST"
}
