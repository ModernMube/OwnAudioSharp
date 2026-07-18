using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// OwnAudioStreamInfo from ownaudio_ffi.h. 24 bytes, u64 forces 8-byte align,
/// so there is 4 bytes of tail padding after BitDepth. Layout test guards this.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioStreamInfo
{
    /// <summary>
    /// Interleaved channel count of the decoded output.
    /// </summary>
    public uint Channels;

    /// <summary>Output rate in Hz.</summary>
    public uint SampleRate;

    /// <summary>
    /// Length in ms, ulong.MaxValue when the decoder can't tell.
    /// </summary>
    public ulong DurationMs;

    /// <summary>Source bit depth, 0 for float/compressed.</summary>
    public uint BitDepth;
}
