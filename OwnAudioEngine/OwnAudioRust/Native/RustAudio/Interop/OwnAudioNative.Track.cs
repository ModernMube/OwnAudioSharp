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
    /// Sets the mixer's master stereo pan position (-1.0 = hard left, 0.0 = center,
    /// +1.0 = hard right), applied once over the fully summed mix under an equal-power
    /// law normalized to unity at center. Ramped on the audio thread.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="pan">Master pan position (clamped to [-1.0, +1.0]).</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_set_master_pan(IntPtr mixer, float pan);

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

    /// <summary>
    /// Starts capturing the mixer's master output into a ring buffer so the control
    /// thread can persist the rendered mix (e.g. record to a file). Overflow is
    /// dropped, so a slow drain never blocks rendering.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="capacitySamples">Ring capacity in interleaved samples.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_capture_start(IntPtr mixer, nuint capacitySamples);

    /// <summary>
    /// Reads up to <paramref name="len"/> captured samples into <paramref name="outBuffer"/>,
    /// reporting the count actually read through <paramref name="outRead"/>. Single-consumer:
    /// never call concurrently with <see cref="ownaudio_v1_mixer_capture_stop"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="outBuffer">Destination buffer for captured interleaved samples.</param>
    /// <param name="len">Maximum number of samples to read.</param>
    /// <param name="outRead">Receives the number of samples actually read.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static unsafe partial int ownaudio_v1_mixer_capture_read(
        IntPtr mixer,
        float* outBuffer,
        nuint len,
        out nuint outRead);

    /// <summary>
    /// Stops master-output capture and releases the ring's read side. Safe to call
    /// when capture is inactive. Must not run concurrently with
    /// <see cref="ownaudio_v1_mixer_capture_read"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_capture_stop(IntPtr mixer);

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
    /// Writes the number of <em>content</em> (source-timeline) frames the track has advanced
    /// through since the last position reset to <paramref name="outFrames"/>. Unlike
    /// <see cref="ownaudio_v1_track_get_rendered_frames"/> (output frames, wall-clock time),
    /// this integrates the per-block tempo, so it is the tempo-aware content-time position —
    /// the quantity a file source reports as its <c>Position</c>, matching the legacy chain.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="outFrames">Receives the content frame count on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_get_rendered_content_frames(IntPtr track, out double outFrames);

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

    /// <summary>
    /// Sets the track stereo pan position (-1.0 = hard left, 0.0 = center,
    /// +1.0 = hard right) under an equal-power law normalized to unity at center.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="pan">Pan position (clamped to [-1.0, +1.0]).</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_pan(IntPtr track, float pan);

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

    /// <summary>
    /// Pins the track's SoundTouch time-stretch stage on for its whole lifetime, so it routes
    /// through the stage from the first block (even at unity) and is never released back to the
    /// zero-latency bypass path. Set once by a tempo/pitch-capable source when it binds the track,
    /// so the first tempo/pitch change lands on a warm FIFO with constant, PDC-aligned latency
    /// instead of switching in from bypass (which clicks, comb-filters, and desyncs the track).
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="enabled">Nonzero to pin the stretch stage on; zero to leave it off.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_stretch_always_on(IntPtr track, int enabled);

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

    /// <summary>
    /// Installs a per-track output-channel routing map: source channel <c>i</c> is
    /// summed into physical output channel <c>map[i]</c> (for <c>i &lt; len</c>), and
    /// every output channel not named by the map receives no contribution from this
    /// track. Pass <paramref name="len"/> = 0 to clear any routing.
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <param name="map">Reference to the first zero-based output-channel index.</param>
    /// <param name="len">Number of source channels the map covers.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_output_channel_map(
        IntPtr track,
        in uint map,
        nuint len);

    /// <summary>
    /// Clears any per-track output-channel routing, returning the track to the
    /// straight identity mix (source channel <c>i</c> → output channel <c>i</c>).
    /// </summary>
    /// <param name="track">Valid track handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_clear_output_channel_map(IntPtr track);

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

    /// <summary>
    /// Copies an interleaved <c>f32</c> buffer, installs a memory-serving source on the track,
    /// and writes the control handle to <paramref name="outSource"/>.
    /// </summary>
    /// <remarks>
    /// The samples are copied once into native memory (a control-thread copy, never on the
    /// audio path), after which the audio thread owns them and the managed side is only a
    /// controller. Looping and end-of-stream are handled natively.
    /// </remarks>
    /// <param name="mixer">Valid mixer handle that owns the track.</param>
    /// <param name="track">Valid track handle whose source is installed.</param>
    /// <param name="samples">Reference to the first interleaved <c>f32</c> sample.</param>
    /// <param name="sampleCount">Number of samples (frames × channels).</param>
    /// <param name="channels">Interleaved channel count of the buffer.</param>
    /// <param name="loopEnabled">Non-zero to loop seamlessly at end-of-buffer.</param>
    /// <param name="outSource">Receives the memory-source control handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_open_memory(
        IntPtr mixer,
        IntPtr track,
        in float samples,
        nuint sampleCount,
        uint channels,
        byte loopEnabled,
        out IntPtr outSource);

    /// <summary>Enables or disables seamless looping for a memory source.</summary>
    /// <param name="source">Valid memory-source handle.</param>
    /// <param name="enabled">Non-zero to loop; zero to stop at end-of-buffer.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_memory_source_set_loop(IntPtr source, byte enabled);

    /// <summary>
    /// Writes whether the memory source has reached end-of-buffer (without looping) to
    /// <paramref name="outFinished"/> (1 = finished, 0 = still playing or looping).
    /// </summary>
    /// <param name="source">Valid memory-source handle.</param>
    /// <param name="outFinished">Receives the finished flag on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_memory_source_is_finished(IntPtr source, out byte outFinished);

    /// <summary>Requests a seek to an absolute output-frame position on a memory source.</summary>
    /// <param name="source">Valid memory-source handle.</param>
    /// <param name="framePosition">Target position in output sample frames.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_memory_source_seek(IntPtr source, ulong framePosition);

    /// <summary>Destroys a memory-source control handle.</summary>
    /// <param name="source">Handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_memory_source_destroy(IntPtr source);

    /// <summary>
    /// Opens a device input stream on the track, wiring native capture straight into the track's
    /// ring buffer (no managed callback), and writes the control handle to <paramref name="outInput"/>.
    /// </summary>
    /// <remarks>
    /// The capture callback runs on the native audio thread and pushes samples into the track's ring
    /// directly, so no audio data ever crosses into managed code. The stream starts paused.
    /// </remarks>
    /// <param name="engine">Valid engine handle owning the input device.</param>
    /// <param name="mixer">Valid mixer handle that owns the track.</param>
    /// <param name="track">Valid track handle whose source is installed.</param>
    /// <param name="deviceName">UTF-8 device name, or <see langword="null"/> for the default device.</param>
    /// <param name="sampleRate">Capture sample rate in Hz.</param>
    /// <param name="channels">Capture (and track) channel count.</param>
    /// <param name="bufferFrames">Device buffer size in frames; 0 lets the engine choose.</param>
    /// <param name="outInput">Receives the input-source control handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_v1_track_open_input(
        IntPtr engine,
        IntPtr mixer,
        IntPtr track,
        string? deviceName,
        uint sampleRate,
        ushort channels,
        uint bufferFrames,
        out IntPtr outInput);

    /// <summary>Starts (or resumes) device capture feeding the track.</summary>
    /// <param name="input">Valid input-source handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_source_play(IntPtr input);

    /// <summary>Pauses device capture.</summary>
    /// <param name="input">Valid input-source handle.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_source_pause(IntPtr input);

    /// <summary>Writes the most recent capture peak levels to the out parameters.</summary>
    /// <param name="input">Valid input-source handle.</param>
    /// <param name="outLeft">Receives the left-channel capture peak.</param>
    /// <param name="outRight">Receives the right-channel capture peak.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_source_get_peaks(
        IntPtr input,
        out float outLeft,
        out float outRight);

    /// <summary>Destroys an input-source control handle, stopping capture.</summary>
    /// <param name="input">Handle to destroy; passing <see cref="IntPtr.Zero"/> is safe.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_input_source_destroy(IntPtr input);

    #endregion
}
