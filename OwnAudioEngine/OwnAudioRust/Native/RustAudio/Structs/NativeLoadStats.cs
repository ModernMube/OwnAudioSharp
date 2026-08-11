using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Structs;

/// <summary>
/// OwnAudioLoadStats from ownaudio_ffi.h. 40 bytes, no tail padding. Layout test guards this.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeLoadStats
{
    /// <summary>
    /// Callbacks since the last reset.
    /// </summary>
    public ulong BlockCount;

    /// <summary>Longest single callback, ns.</summary>
    public ulong PeakBlockNs;

    /// <summary>Mean callback duration, ns.</summary>
    public ulong AverageBlockNs;

    /// <summary>Silent frames from a dry ring, 0 in callback mode.</summary>
    public ulong UnderrunFrames;

    /// <summary>Mean share of the block period spent in the callback, 1.0 = late.</summary>
    public float AverageLoad;

    /// <summary>Worst single block by the same measure.</summary>
    public float PeakLoad;
}
