using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invokes for the native bpm detector. Every call gives back 0 on success, error code otherwise.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    /// <summary>
    /// Makes a detector, handle comes back in outDetector.
    /// </summary>
    /// <param name="channels">interleaved channel count of what we feed, at least 1</param>
    /// <param name="sampleRate"></param>
    /// <param name="outDetector"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_bpm_create(
        uint channels,
        uint sampleRate,
        out IntPtr outDetector);

    /// <summary>
    /// Pushes interleaved f32 frames into the detector.
    /// </summary>
    /// <param name="detector"></param>
    /// <param name="samples">first sample of the interleaved block</param>
    /// <param name="numSamples">frames to feed, not samples</param>
    /// <param name="sampleCount">how many f32 elements are actually there</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_bpm_input_samples(
        IntPtr detector,
        ref float samples,
        nuint numSamples,
        nuint sampleCount);

    /// <summary>
    /// Current tempo guess. Still 0 while it hasn't got enough to say anything.
    /// </summary>
    /// <param name="detector"></param>
    /// <param name="outBpm"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_bpm_get_bpm(
        IntPtr detector,
        out float outBpm);

    /// <summary>
    /// Throws away the detector. Zero handle is fine.
    /// </summary>
    /// <param name="detector"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_bpm_destroy(IntPtr detector);
}
