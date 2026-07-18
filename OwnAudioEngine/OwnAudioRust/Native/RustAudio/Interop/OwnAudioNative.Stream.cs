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
    /// Kills the output stream. Zero handle is fine.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_output_stream_destroy(IntPtr stream);

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
    /// Kills the input stream. Zero handle is fine.
    /// </summary>
    /// <param name="stream"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_input_stream_destroy(IntPtr stream);

    #endregion
}
