using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invoke declarations for the native BPM detector API.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    /// <summary>
    /// Creates a BPM detector and writes its handle to <paramref name="outDetector"/>.
    /// </summary>
    /// <param name="channels">Interleaved channel count of the fed samples (clamped to at least 1).</param>
    /// <param name="sampleRate">Input sample rate in Hz.</param>
    /// <param name="outDetector">Receives the new detector handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_bpm_create(channels, sample_rate, out_detector) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_bpm_create(
        uint channels,
        uint sampleRate,
        out IntPtr outDetector);

    /// <summary>
    /// Feeds interleaved frames into the detector.
    /// </summary>
    /// <param name="detector">Valid detector handle.</param>
    /// <param name="samples">Reference to the first interleaved <c>f32</c> sample.</param>
    /// <param name="numSamples">Number of frames (not samples) to feed.</param>
    /// <param name="sampleCount">Total number of <c>f32</c> elements available at <paramref name="samples"/>.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_bpm_input_samples(detector, samples, num_samples, sample_count) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_bpm_input_samples(
        IntPtr detector,
        ref float samples,
        nuint numSamples,
        nuint sampleCount);

    /// <summary>
    /// Writes the current estimated tempo in BPM to <paramref name="outBpm"/> (0 when not yet reliable).
    /// </summary>
    /// <param name="detector">Valid detector handle.</param>
    /// <param name="outBpm">Receives the estimated BPM.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_bpm_get_bpm(detector, out_bpm) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_bpm_get_bpm(
        IntPtr detector,
        out float outBpm);

    /// <summary>
    /// Destroys a BPM detector handle. Passing <see cref="IntPtr.Zero"/> is safe.
    /// </summary>
    /// <param name="detector">Detector handle to destroy.</param>
    /// <remarks>Mirrors: <c>ownaudio_v1_bpm_destroy(OwnAudioBpmHandle*) → void</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_bpm_destroy(IntPtr detector);
}
