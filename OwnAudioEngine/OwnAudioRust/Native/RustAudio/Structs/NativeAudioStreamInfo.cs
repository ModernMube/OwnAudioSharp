using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// Mirrors <c>OwnAudioStreamInfo</c> from <c>ownaudio_ffi.h</c>.
/// Field order and sizes must match exactly — verified by the FFI struct-layout
/// parity test against the cbindgen-generated layout.
/// </summary>
/// <remarks>
/// Layout (natural C alignment, 8-byte aligned because of the <c>u64</c>):
/// <code>
/// Offset  0 : Channels    (4 bytes — u32)
/// Offset  4 : SampleRate  (4 bytes — u32)
/// Offset  8 : DurationMs  (8 bytes — u64)
/// Offset 16 : BitDepth    (4 bytes — u32)
/// Offset 20 : [4 bytes tail padding]
/// Total  : 24 bytes
/// </code>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioStreamInfo
{
    /// <summary>Number of interleaved channels in the decoded output.</summary>
    public uint Channels;

    /// <summary>Output sample rate in Hz.</summary>
    public uint SampleRate;

    /// <summary>Total duration in milliseconds; <see cref="ulong.MaxValue"/> if unknown.</summary>
    public ulong DurationMs;

    /// <summary>Source bit depth, or 0 for float/compressed formats.</summary>
    public uint BitDepth;
}
