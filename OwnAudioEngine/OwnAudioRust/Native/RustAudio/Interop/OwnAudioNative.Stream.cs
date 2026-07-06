using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Structs;

namespace Ownaudio.Native.RustAudio.Interop;

internal static unsafe partial class OwnAudioNative
{
    #region Output stream

    /// <summary>
    /// Opens an output stream and writes its handle to <paramref name="outStream"/>.
    /// The stream starts in the paused state; call
    /// <see cref="ownaudio_v1_output_stream_play"/> to begin audio output.
    /// </summary>
    /// <param name="engine">Valid handle from <see cref="ownaudio_v1_engine_create"/>.</param>
    /// <param name="deviceName">
    /// Pointer to a null-terminated UTF-8 device name, or <see cref="IntPtr.Zero"/>
    /// to use the system default output device.
    /// </param>
    /// <param name="config">Pointer to a filled <see cref="NativeStreamConfig"/>; must not be null.</param>
    /// <param name="callback">
    /// Native function pointer called on the audio thread for every buffer.
    /// Obtain with <c>Marshal.GetFunctionPointerForDelegate</c> and keep the
    /// delegate alive (pinned via <c>GCHandle</c>) for the entire stream lifetime.
    /// </param>
    /// <param name="userData">
    /// Opaque pointer passed back to <paramref name="callback"/> unchanged; may be null.
    /// </param>
    /// <param name="outStream">Receives the new stream handle on success.</param>
    /// <returns>
    /// <see cref="NativeErrorCode.Success"/> (0) on success;
    /// a non-zero <see cref="NativeErrorCode"/> otherwise.
    /// </returns>
    /// <remarks>
    /// Mirrors:
    /// <c>ownaudio_v1_open_output_stream(engine, device_name, config, callback, user_data, out_stream) → i32</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_open_output_stream(
        IntPtr engine,
        IntPtr deviceName,
        in NativeStreamConfig config,
        IntPtr callback,
        IntPtr userData,
        out IntPtr outStream);

    /// <summary>
    /// Opens an output stream driven directly by a multi-track mixer and writes
    /// its handle to <paramref name="outStream"/>.
    /// </summary>
    /// <param name="engine">Valid handle from <see cref="ownaudio_v1_engine_create"/>.</param>
    /// <param name="mixer">
    /// Valid handle from <see cref="ownaudio_v1_mixer_create"/>.  Its sample rate
    /// and channel count must match <paramref name="config"/>.  The mixer is moved
    /// onto the audio thread and renders every buffer itself, so no managed
    /// callback is involved.
    /// </param>
    /// <param name="deviceName">
    /// Pointer to a null-terminated UTF-8 device name, or <see cref="IntPtr.Zero"/>
    /// to use the system default output device.
    /// </param>
    /// <param name="config">Pointer to a filled <see cref="NativeStreamConfig"/>; must not be null.</param>
    /// <param name="outStream">Receives the new stream handle on success.</param>
    /// <returns>
    /// <see cref="NativeErrorCode.Success"/> (0) on success;
    /// a non-zero <see cref="NativeErrorCode"/> otherwise.
    /// </returns>
    /// <remarks>
    /// The stream starts paused; call <see cref="ownaudio_v1_output_stream_play"/>
    /// to begin output.  Destroy the stream before destroying the mixer.
    /// Mirrors:
    /// <c>ownaudio_v1_mixer_open_output_stream(engine, mixer, device_name, config, out_stream) → i32</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_open_output_stream(
        IntPtr engine,
        IntPtr mixer,
        IntPtr deviceName,
        in NativeStreamConfig config,
        out IntPtr outStream);

    /// <summary>
    /// Starts (or resumes) audio output on the given stream.
    /// </summary>
    /// <param name="stream">Valid handle from <see cref="ownaudio_v1_open_output_stream"/>.</param>
    /// <returns><see cref="NativeErrorCode.Success"/> (0) on success.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_output_stream_play(OwnAudioOutputStreamHandle*) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_play(IntPtr stream);

    /// <summary>
    /// Pauses audio output without destroying the stream.
    /// </summary>
    /// <param name="stream">Valid handle from <see cref="ownaudio_v1_open_output_stream"/>.</param>
    /// <returns><see cref="NativeErrorCode.Success"/> (0) on success.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_output_stream_pause(OwnAudioOutputStreamHandle*) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_pause(IntPtr stream);

    /// <summary>
    /// Polls the output stream's error state: the most recent error kind
    /// (<see cref="NativeStreamErrorKind"/>) and the monotonic total error count.
    /// </summary>
    /// <param name="stream">Valid handle from <see cref="ownaudio_v1_open_output_stream"/>.</param>
    /// <param name="outKind">Receives the latest error kind discriminant; may be null to skip.</param>
    /// <param name="outCount">
    /// Receives the total error count since the stream opened. An increase between
    /// two polls signals a fresh error.
    /// </param>
    /// <returns><see cref="NativeErrorCode.Success"/> (0) on success.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_output_stream_get_error_state(stream, out_kind, out_count) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_output_stream_get_error_state(
        IntPtr stream,
        out uint outKind,
        out ulong outCount);

    /// <summary>
    /// Destroys an output stream and releases all associated resources.
    /// </summary>
    /// <param name="stream">The handle to destroy.  Passing <see cref="IntPtr.Zero"/> is safe.</param>
    /// <remarks>Mirrors: <c>ownaudio_v1_output_stream_destroy(OwnAudioOutputStreamHandle*) → void</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_output_stream_destroy(IntPtr stream);

    #endregion

    #region Input stream

    /// <summary>
    /// Opens an input stream and writes its handle to <paramref name="outStream"/>.
    /// The stream starts in the paused state; call
    /// <see cref="ownaudio_v1_input_stream_play"/> to begin capturing.
    /// </summary>
    /// <param name="engine">Valid handle from <see cref="ownaudio_v1_engine_create"/>.</param>
    /// <param name="deviceName">
    /// Pointer to a null-terminated UTF-8 device name, or <see cref="IntPtr.Zero"/>
    /// to use the system default input device.
    /// </param>
    /// <param name="config">Pointer to a filled <see cref="NativeStreamConfig"/>; must not be null.</param>
    /// <param name="callback">
    /// Native function pointer called on the audio thread with each captured buffer.
    /// Same lifetime and pinning requirements as the output callback.
    /// </param>
    /// <param name="userData">Opaque pointer passed back to <paramref name="callback"/>; may be null.</param>
    /// <param name="outStream">Receives the new stream handle on success.</param>
    /// <returns>
    /// <see cref="NativeErrorCode.Success"/> (0) on success;
    /// a non-zero <see cref="NativeErrorCode"/> otherwise.
    /// </returns>
    /// <remarks>
    /// Mirrors:
    /// <c>ownaudio_v1_open_input_stream(engine, device_name, config, callback, user_data, out_stream) → i32</c>
    /// </remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_open_input_stream(
        IntPtr engine,
        IntPtr deviceName,
        in NativeStreamConfig config,
        IntPtr callback,
        IntPtr userData,
        out IntPtr outStream);

    /// <summary>
    /// Starts (or resumes) audio capture on the given stream.
    /// </summary>
    /// <param name="stream">Valid handle from <see cref="ownaudio_v1_open_input_stream"/>.</param>
    /// <returns><see cref="NativeErrorCode.Success"/> (0) on success.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_input_stream_play(OwnAudioInputStreamHandle*) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_play(IntPtr stream);

    /// <summary>
    /// Pauses audio capture without destroying the stream.
    /// </summary>
    /// <param name="stream">Valid handle from <see cref="ownaudio_v1_open_input_stream"/>.</param>
    /// <returns><see cref="NativeErrorCode.Success"/> (0) on success.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_input_stream_pause(OwnAudioInputStreamHandle*) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_pause(IntPtr stream);

    /// <summary>
    /// Polls the input stream's error state. See
    /// <see cref="ownaudio_v1_output_stream_get_error_state"/> for semantics.
    /// </summary>
    /// <param name="stream">Valid handle from <see cref="ownaudio_v1_open_input_stream"/>.</param>
    /// <param name="outKind">Receives the latest error kind discriminant; may be null to skip.</param>
    /// <param name="outCount">Receives the total error count since the stream opened.</param>
    /// <returns><see cref="NativeErrorCode.Success"/> (0) on success.</returns>
    /// <remarks>Mirrors: <c>ownaudio_v1_input_stream_get_error_state(stream, out_kind, out_count) → i32</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_stream_get_error_state(
        IntPtr stream,
        out uint outKind,
        out ulong outCount);

    /// <summary>
    /// Destroys an input stream and releases all associated resources.
    /// </summary>
    /// <param name="stream">The handle to destroy.  Passing <see cref="IntPtr.Zero"/> is safe.</param>
    /// <remarks>Mirrors: <c>ownaudio_v1_input_stream_destroy(OwnAudioInputStreamHandle*) → void</c></remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_input_stream_destroy(IntPtr stream);

    #endregion
}
