using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// OwnAudioSampleFormat from ownaudio_ffi.h. int-backed to match the repr(C) width.
/// </summary>
internal enum NativeSampleFormat : int
{
    /// <summary>32-bit float, what we want for DSP.</summary>
    F32 = 0,

    /// <summary>Signed 16-bit.</summary>
    I16 = 1,

    /// <summary>Unsigned 16-bit.</summary>
    U16 = 2,

    /// <summary>
    /// Signed 32-bit — most ASIO drivers talk this natively.
    /// </summary>
    I32 = 3,
}

/// <summary>
/// OwnAudioStreamConfig from ownaudio_ffi.h — 16 bytes, with 2 bytes of padding
/// after Channels so the format enum lands 4-byte aligned. Layout test guards it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeStreamConfig
{
    /// <summary>
    /// Wanted rate in Hz (44100, 48000, ...).
    /// </summary>
    public uint SampleRate;

    /// <summary>1 = mono, 2 = stereo.</summary>
    public ushort Channels;

    private ushort _pad;

    /// <summary>Sample data format.</summary>
    public NativeSampleFormat SampleFormat;

    /// <summary>
    /// Buffer size in frames; 0 means "whatever the platform likes".
    /// </summary>
    public uint BufferSizeFrames;
}
