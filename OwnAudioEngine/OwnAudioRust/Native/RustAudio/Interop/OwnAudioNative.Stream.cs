using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Output and input stream P/Invokes. Streams always come back paused, play() gets them going.
/// </summary>
internal static unsafe partial class OwnAudioNative
{
    #region Output stream

    /// <summary>
    /// Opens an output stream, handle in outStream. Starts paused.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="deviceName">utf8 name, zero means system default</param>
    /// <param name="config"></param>
    /// <param name="callback">
    /// From Marshal.GetFunctionPointerForDelegate. Keep the delegate pinned for the whole stream life
    /// or the rt thread will call into collected memory.
    /// </param>
    /// <param name="userData">passed back to the callback untouched, may be null</param>
    /// <param name="outStream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_open_output_stream(
        IntPtr engine,
        IntPtr deviceName,
        in NativeStreamConfig config,
        IntPtr callback,
        IntPtr userData,
        out IntPtr outStream);

    /// <summary>
    /// Same, but renderRingFrames picks how deep the buffered-mode render ring is. Zero keeps the
    /// ~100ms default. The native side pulls it up to three device buffers if it's shallower than
    /// that, so read it back with get_ring_frames. Ignored when callback is non-zero.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="deviceName">utf8 name, zero means system default</param>
    /// <param name="config"></param>
    /// <param name="callback"></param>
    /// <param name="userData"></param>
    /// <param name="renderRingFrames"></param>
    /// <param name="outStream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_open_output_stream_ex(
        IntPtr engine,
        IntPtr deviceName,
        in NativeStreamConfig config,
        IntPtr callback,
        IntPtr userData,
        uint renderRingFrames,
        out IntPtr outStream);

    /// <summary>
    /// Output stream driven by a mixer instead of a managed callback. The mixer moves onto the audio
    /// thread and renders every buffer itself, so its rate/channels have to match config.
    /// Destroy the stream before the mixer.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="mixer"></param>
    /// <param name="deviceName">utf8 name, zero means system default</param>
    /// <param name="config"></param>
    /// <param name="outStream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_open_output_stream(
        IntPtr engine,
        IntPtr mixer,
        IntPtr deviceName,
        in NativeStreamConfig config,
        out IntPtr outStream);

    /// <summary>
    /// Starts or resumes output.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_play(IntPtr stream);

    /// <summary>
    /// Pauses, stream stays alive.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_pause(IntPtr stream);

    /// <summary>
    /// Polls the error state. The count only ever grows, so if it moved between two polls something fresh broke.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outKind">latest NativeStreamErrorKind discriminant</param>
    /// <param name="outCount"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_error_state(
        IntPtr stream,
        out uint outKind,
        out ulong outCount);

    /// <summary>
    /// Hardware playback latency in frames — how far ahead of the DAC the callback runs.
    /// Zero until the stream has actually played a buffer, or when the backend stays quiet about it.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outFrames">receives the latency in frames</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_latency_frames(
        IntPtr stream,
        out uint outFrames);

    /// <summary>
    /// Kills the output stream. Zero handle is fine.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_output_stream_destroy(IntPtr stream);

    /// <summary>
    /// Pushes samples into a buffered stream (one opened with a zero callback). Whole frames only,
    /// never blocks — a short write means the ring is full.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="src"></param>
    /// <param name="srcLen"></param>
    /// <param name="outWritten">receives the sample count actually taken</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static unsafe partial int ownaudio_v1_output_stream_write(
        IntPtr stream,
        float* src,
        nuint srcLen,
        out nuint outWritten);

    /// <summary>
    /// Samples still queued for playback, i.e. how far ahead of the DAC the host has pushed.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outSamples"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_queued_samples(
        IntPtr stream,
        out nuint outSamples);

    /// <summary>
    /// Depth of the render ring in frames, after the clamp. Zero on callback driven streams.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outFrames"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_ring_frames(
        IntPtr stream,
        out uint outFrames);

    /// <summary>
    /// Asks the render callback to drop whatever is queued. Takes effect on its next run.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_clear(IntPtr stream);

    /// <summary>
    /// Frames the render callback had to fill with silence because the ring ran dry. Cumulative,
    /// always 0 on a callback mode stream.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outFrames"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_underrun_frames(
        IntPtr stream,
        out ulong outFrames);

    /// <summary>
    /// How long the audio callback is taking against the time its frame count buys.
    /// Five relaxed atomic loads, safe to poll from a UI timer.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outStats"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_load_stats(
        IntPtr stream,
        out NativeLoadStats outStats);

    /// <summary>
    /// Zeroes the load tallies. Leaves the underrun count alone, that one is a fault log.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_reset_load_stats(IntPtr stream);

    #endregion

    #region Input stream

    /// <summary>
    /// Opens a capture stream, starts paused. Callback pinning works like the output side.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="deviceName">utf8 name, zero means system default</param>
    /// <param name="config"></param>
    /// <param name="callback"></param>
    /// <param name="userData"></param>
    /// <param name="outStream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_open_input_stream(
        IntPtr engine,
        IntPtr deviceName,
        in NativeStreamConfig config,
        IntPtr callback,
        IntPtr userData,
        out IntPtr outStream);

    /// <summary>
    /// Starts or resumes capture.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_play(IntPtr stream);

    /// <summary>
    /// Pauses capture.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_pause(IntPtr stream);

    /// <summary>
    /// Same error polling as on the output stream.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outKind"></param>
    /// <param name="outCount"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_get_error_state(
        IntPtr stream,
        out uint outKind,
        out ulong outCount);

    /// <summary>
    /// Hardware capture latency in frames — how long ago the samples in the buffer hit the ADC.
    /// Zero until the stream has actually captured a buffer, or when the backend stays quiet about it.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outFrames">receives the latency in frames</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_get_latency_frames(
        IntPtr stream,
        out uint outFrames);

    /// <summary>
    /// Drains a buffered-mode stream (one opened with a zero callback) into dst. Whole frames only,
    /// returns 0 samples on an empty ring rather than blocking.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="dst"></param>
    /// <param name="dstLen"></param>
    /// <param name="outRead">receives the sample count actually taken</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static unsafe partial int ownaudio_v1_input_stream_read(
        IntPtr stream,
        float* dst,
        nuint dstLen,
        out nuint outRead);

    /// <summary>
    /// Drops whatever sits in a buffered stream's ring. Call it while capture is paused.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_clear(IntPtr stream);

    /// <summary>
    /// Capture frames the native ring had to drop because nobody read it in time. Cumulative,
    /// always 0 on a callback mode stream.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="outFrames"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_get_dropped_frames(
        IntPtr stream,
        out ulong outFrames);

    /// <summary>
    /// Kills the input stream. Zero handle is fine.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_input_stream_destroy(IntPtr stream);

    #endregion
}
