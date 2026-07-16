using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// Mirrors the <c>OwnAudioSampleFormat</c> enum from <c>ownaudio_ffi.h</c>.
/// Underlying type is <c>int</c> to match the Rust <c>#[repr(C)]</c> enum width.
/// </summary>
internal enum NativeSampleFormat : int
{
    /// <summary>32-bit IEEE float — recommended for DSP work.</summary>
    F32 = 0,

    /// <summary>Signed 16-bit integer.</summary>
    I16 = 1,

    /// <summary>Unsigned 16-bit integer.</summary>
    U16 = 2,

    /// <summary>Signed 32-bit integer — the native wire format of many ASIO drivers.</summary>
    I32 = 3,
}

/// <summary>
/// Mirrors <c>OwnAudioStreamConfig</c> from <c>ownaudio_ffi.h</c>.
/// Field order and sizes must match exactly — verified by unit test against
/// the cbindgen-generated layout.
/// </summary>
/// <remarks>
/// Layout (natural C alignment):
/// <code>
/// Offset  0 : SampleRate       (4 bytes — u32)
/// Offset  4 : Channels         (2 bytes — u16)
/// Offset  6 : [2 bytes padding] (enum alignment)
/// Offset  8 : SampleFormat     (4 bytes — i32 enum)
/// Offset 12 : BufferSizeFrames (4 bytes — u32)
/// Total  : 16 bytes
/// </code>
/// A value of zero for <see cref="BufferSizeFrames"/> tells the engine to
/// use the platform default buffer size.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeStreamConfig
{
    /// <summary>Target sample rate in Hz (e.g. 44100, 48000).</summary>
    public uint SampleRate;

    /// <summary>Number of channels (1 = mono, 2 = stereo).</summary>
    public ushort Channels;

    /// <summary>
    /// Two bytes of implicit padding inserted by the C ABI to align
    /// <see cref="SampleFormat"/> on a 4-byte boundary.
    /// </summary>
    private ushort _pad;

    /// <summary>Sample data format.</summary>
    public NativeSampleFormat SampleFormat;

    /// <summary>
    /// Requested buffer size in audio frames; 0 uses the platform default.
    /// </summary>
    public uint BufferSizeFrames;
}
