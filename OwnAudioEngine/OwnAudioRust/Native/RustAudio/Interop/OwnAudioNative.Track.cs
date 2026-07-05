using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invoke declarations for the native multi-track mixer and track lifecycle API.
/// </summary>
internal static partial class OwnAudioNative
{
    #region Mixer lifecycle

    /// <summary>
    /// Creates a new multi-track mixer and writes its handle to <paramref name="outMixer"/>.
    /// </summary>
    /// <param name="sampleRate">Output sample rate in Hz.</param>
    /// <param name="channels">Number of output channels (1 = mono, 2 = stereo).</param>
    /// <param name="outMixer">Receives the new mixer handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_create(
        float sampleRate,
        ushort channels,
        out IntPtr outMixer);

    /// <summary>
    /// Destroys a mixer handle and releases all associated resources.
    /// </summary>
    /// <param name="mixer">Mixer handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_mixer_destroy(IntPtr mixer);

    /// <summary>
    /// Starts every track in the mixer in a single call against the shared clock.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_play_all(IntPtr mixer);

    /// <summary>
    /// Pauses every track in the mixer in a single call against the shared clock.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_pause_all(IntPtr mixer);

    /// <summary>
    /// Stops every track in the mixer in a single call against the shared clock.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_stop_all(IntPtr mixer);

    /// <summary>
    /// Sets the mixer's master output gain (linear amplitude applied once over the
    /// fully summed mix; 1.0 = unity, 0.0 = silence). Ramped on the audio thread.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="gain">Master gain (≥ 0).</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_set_master_gain(IntPtr mixer, float gain);

    /// <summary>
    /// Writes the mixer's most recently measured master output peak levels
    /// (absolute, post master gain) to <paramref name="outLeft"/> and
    /// <paramref name="outRight"/>. A mono mixer reports the same value on both.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="outLeft">Receives the left-channel peak on success.</param>
    /// <param name="outRight">Receives the right-channel peak on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_get_master_peaks(
        IntPtr mixer,
        out float outLeft,
        out float outRight);

    #endregion

    #region Track lifecycle

    /// <summary>
    /// Adds a new track to the mixer and writes its handle to <paramref name="outTrack"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="outTrack">Receives the new track handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_create(
        IntPtr mixer,
        out IntPtr outTrack);

    /// <summary>
    /// Destroys a track handle without removing the track from the mixer.
    /// </summary>
    /// <param name="track">Track handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_track_destroy(IntPtr track);

    /// <summary>
    /// Removes the track from the mixer.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="track">Valid track handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_remove(IntPtr mixer, IntPtr track);

    #endregion

    #region Track transport

    /// <summary>Starts or resumes playback of the track.</summary>
    /// <param name="track">Valid track handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_play(IntPtr track);

    /// <summary>Pauses the track without resetting its position.</summary>
    /// <param name="track">Valid track handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_pause(IntPtr track);

    /// <summary>Stops the track and resets its position to zero.</summary>
    /// <param name="track">Valid track handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_stop(IntPtr track);

    /// <summary>Seeks the track to an absolute sample position.</summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="samplePosition">Target sample position.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_seek(IntPtr track, ulong samplePosition);

    #endregion

    #region Track position

    /// <summary>
    /// Writes the number of output frames the track has rendered since the last
    /// position reset to <paramref name="outFrames"/>.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="outFrames">Receives the rendered frame count on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_get_rendered_frames(IntPtr track, out ulong outFrames);

    /// <summary>
    /// Writes the track's most recently measured output peak levels (absolute, of
    /// the track's own post-effect, post-gain contribution) to
    /// <paramref name="outLeft"/> and <paramref name="outRight"/>. A mono track
    /// reports the same value on both channels.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="outLeft">Receives the left-channel peak on success.</param>
    /// <param name="outRight">Receives the right-channel peak on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_get_peaks(
        IntPtr track,
        out float outLeft,
        out float outRight);

    /// <summary>
    /// Resets the track's rendered-frame position counter to zero.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_reset_position(IntPtr track);

    #endregion

    #region Track parameters

    /// <summary>Sets the track gain (linear amplitude; 1.0 = unity).</summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="gain">Gain value (≥ 0).</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_gain(IntPtr track, float gain);

    /// <summary>Sets the track tempo ratio (1.0 = normal speed).</summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="ratio">Tempo ratio (0.25–4.0).</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_tempo(IntPtr track, float ratio);

    /// <summary>Sets the track pitch shift in semitones.</summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="semitones">Pitch shift in semitones (−24 to +24).</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_pitch(IntPtr track, float semitones);

    /// <summary>Sets the track mute state.</summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="muted">0.0 = unmuted; 1.0 = muted.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_mute(IntPtr track, float muted);

    /// <summary>
    /// Sets the track's start-offset silence: the number of output frames the track
    /// emits as silence (without reading its source) before it begins contributing,
    /// delaying its entry against the shared clock sample-accurately. Pass 0 to clear.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="frames">Start-offset silence length in output frames.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_start_delay_frames(IntPtr track, ulong frames);

    #endregion

    #region Track source feed

    /// <summary>
    /// Creates a lock-free ring buffer feeding the track and writes the write-side
    /// handle to <paramref name="outSource"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle that owns the track.</param>
    /// <param name="track">Valid track handle whose source is (re)installed.</param>
    /// <param name="capacitySamples">
    /// Ring-buffer capacity in interleaved <c>f32</c> samples; sized for the desired
    /// buffering latency (<c>sampleRate × channels × latencySeconds</c>).
    /// </param>
    /// <param name="outSource">Receives the write-side source handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_ring_source(
        IntPtr mixer,
        IntPtr track,
        nuint capacitySamples,
        out IntPtr outSource);

    /// <summary>
    /// Pushes up to <paramref name="sampleCount"/> interleaved <c>f32</c> samples
    /// into the track feed and writes the number actually accepted to
    /// <paramref name="outWritten"/>.
    /// </summary>
    /// <param name="source">Valid source handle.</param>
    /// <param name="samples">Reference to the first sample to push.</param>
    /// <param name="sampleCount">Number of samples available at <paramref name="samples"/>.</param>
    /// <param name="outWritten">Receives the number of samples accepted.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    /// <remarks>Real-time safe and non-blocking; a full buffer accepts fewer samples.</remarks>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_source_write(
        IntPtr source,
        in float samples,
        nuint sampleCount,
        out nuint outWritten);

    /// <summary>
    /// Writes the number of samples that can currently be written without overflow
    /// to <paramref name="outFree"/>.
    /// </summary>
    /// <param name="source">Valid source handle.</param>
    /// <param name="outFree">Receives the free-sample count.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_source_free_samples(
        IntPtr source,
        out nuint outFree);

    /// <summary>
    /// Clears a track's audio source, silencing it.
    /// </summary>
    /// <param name="mixer">Valid mixer handle that owns the track.</param>
    /// <param name="track">Valid track handle whose source is cleared.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_clear_source(IntPtr mixer, IntPtr track);

    /// <summary>
    /// Destroys a track-source write handle and releases its ring-buffer producer.
    /// </summary>
    /// <param name="source">Source handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_track_source_destroy(IntPtr source);

    #endregion

    #region File-backed track source

    /// <summary>
    /// Opens an audio file, installs a decoding source on the track, and writes the
    /// control handle to <paramref name="outSource"/>.
    /// </summary>
    /// <remarks>
    /// The file is decoded and resampled to <paramref name="targetSampleRate"/> /
    /// <paramref name="targetChannels"/> on a native prefetch thread and fed straight into
    /// the track on the audio thread — no managed pump is involved. Looping and
    /// end-of-stream are handled natively; observe them through the returned handle.
    /// </remarks>
    /// <param name="mixer">Valid mixer handle that owns the track.</param>
    /// <param name="track">Valid track handle whose source is installed.</param>
    /// <param name="path">UTF-8 file path (marshaled as a null-terminated string).</param>
    /// <param name="targetSampleRate">Desired output sample rate in Hz; 0 keeps source.</param>
    /// <param name="targetChannels">Desired output channel count; 0 keeps source.</param>
    /// <param name="prefetchFrames">Ring-buffer capacity in frames; 0 uses a default.</param>
    /// <param name="outSource">Receives the file-source control handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_v1_track_open_file(
        IntPtr mixer,
        IntPtr track,
        string path,
        uint targetSampleRate,
        uint targetChannels,
        nuint prefetchFrames,
        out IntPtr outSource);

    /// <summary>Enables or disables seamless looping for a file source.</summary>
    /// <param name="source">Valid file-source handle.</param>
    /// <param name="enabled">Non-zero to loop; zero to stop at end-of-stream.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_file_source_set_loop(IntPtr source, byte enabled);

    /// <summary>
    /// Writes whether the file source has reached end-of-stream (without looping) to
    /// <paramref name="outFinished"/> (1 = finished, 0 = still playing or looping).
    /// </summary>
    /// <param name="source">Valid file-source handle.</param>
    /// <param name="outFinished">Receives the finished flag on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_file_source_is_finished(IntPtr source, out byte outFinished);

    /// <summary>Requests a seek to an absolute output-frame position on a file source.</summary>
    /// <param name="source">Valid file-source handle.</param>
    /// <param name="framePosition">Target position in output sample frames.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_file_source_seek(IntPtr source, ulong framePosition);

    /// <summary>Destroys a file-source control handle.</summary>
    /// <param name="source">Handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_file_source_destroy(IntPtr source);

    #endregion
}
