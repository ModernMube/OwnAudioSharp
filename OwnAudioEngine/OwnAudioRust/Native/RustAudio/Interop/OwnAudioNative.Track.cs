using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Mixer and track P/Invokes: lifecycle, transport, params and the various source kinds.
/// 0 is ok, anything else is an error code.
/// </summary>
internal static partial class OwnAudioNative
{
    #region Mixer lifecycle

    /// <summary>
    /// Makes a multi track mixer, handle in outMixer.
    /// </summary>
    /// <param name="sampleRate">Hz</param>
    /// <param name="channels">1 mono, 2 stereo</param>
    /// <param name="outMixer"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_create(
        float sampleRate,
        ushort channels,
        out IntPtr outMixer);

    /// <summary>
    /// Drops the mixer and everything it owns. Zero handle is fine.
    /// </summary>
    /// <param name="mixer"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_mixer_destroy(IntPtr mixer);

    /// <summary>
    /// Starts every track at once against the shared clock.
    /// </summary>
    /// <param name="mixer"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_play_all(IntPtr mixer);

    /// <summary>
    /// Pauses every track at once.
    /// </summary>
    /// <param name="mixer"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_pause_all(IntPtr mixer);

    /// <summary>
    /// Stops every track at once.
    /// </summary>
    /// <param name="mixer"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_stop_all(IntPtr mixer);

    /// <summary>
    /// Master gain over the summed mix, linear, 1.0 unity and 0.0 silence. Ramped on the audio thread.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="gain"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_set_master_gain(IntPtr mixer, float gain);

    /// <summary>
    /// Master pan, -1 hard left, 0 center, +1 hard right. Equal power law normalized to unity in the
    /// middle, clamped and ramped.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="pan"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_set_master_pan(IntPtr mixer, float pan);

    /// <summary>
    /// Last measured master peaks, absolute and post gain. Mono mixer reports the same on both.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="outLeft"></param>
    /// <param name="outRight"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_get_master_peaks(
        IntPtr mixer,
        out float outLeft,
        out float outRight);

    /// <summary>
    /// Starts tapping the master output into a ring so the control thread can write it out somewhere.
    /// Overflow just gets dropped, a lazy reader never stalls the render.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="capacitySamples">ring size in interleaved samples</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_capture_start(IntPtr mixer, nuint capacitySamples);

    /// <summary>
    /// Drains up to len samples out of the capture ring. Single consumer, so never run it next to
    /// capture_stop on another thread.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="outBuffer"></param>
    /// <param name="len">max samples to take</param>
    /// <param name="outRead">how many we actually got</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static unsafe partial int ownaudio_v1_mixer_capture_read(
        IntPtr mixer,
        float* outBuffer,
        nuint len,
        out nuint outRead);

    /// <summary>
    /// Stops the capture and drops the read side. Fine to call when nothing is running.
    /// </summary>
    /// <param name="mixer"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_capture_stop(IntPtr mixer);

    #endregion

    #region Track lifecycle

    /// <summary>
    /// New track on the mixer, handle in outTrack.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="outTrack"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_create(
        IntPtr mixer,
        out IntPtr outTrack);

    /// <summary>
    /// Handle only, the track stays in the mixer. Zero handle is fine.
    /// </summary>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_track_destroy(IntPtr track);

    /// <summary>
    /// Takes the track out of the mixer.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_remove(IntPtr mixer, IntPtr track);

    #endregion

    #region Track transport

    /// <summary>Play or resume.</summary>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_play(IntPtr track);

    /// <summary>Pause, position stays where it was.</summary>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_pause(IntPtr track);

    /// <summary>Stop and rewind to zero.</summary>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_stop(IntPtr track);

    /// <summary>Jumps to an absolute sample position.</summary>
    /// <param name="track"></param>
    /// <param name="samplePosition"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_seek(IntPtr track, ulong samplePosition);

    #endregion

    #region Track position

    /// <summary>
    /// Output frames rendered since the last position reset, so wall clock time.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="outFrames"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_get_rendered_frames(IntPtr track, out ulong outFrames);

    /// <summary>
    /// Content frames on the source timeline instead, this one integrates the per block tempo.
    /// That's what a file source reports as its Position, same as the old chain did.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="outFrames"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_get_rendered_content_frames(IntPtr track, out double outFrames);

    /// <summary>
    /// Last measured peaks of this track's own post effect, post gain contribution. Mono gives the
    /// same on both channels.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="outLeft"></param>
    /// <param name="outRight"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_get_peaks(
        IntPtr track,
        out float outLeft,
        out float outRight);

    /// <summary>
    /// Zeroes the rendered frame counter.
    /// </summary>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_reset_position(IntPtr track);

    #endregion

    #region Track parameters

    /// <summary>Track gain, linear amplitude, 1.0 is unity.</summary>
    /// <param name="track"></param>
    /// <param name="gain"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_gain(IntPtr track, float gain);

    /// <summary>
    /// Track pan, -1 to +1, equal power and normalized to unity at center.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="pan"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_pan(IntPtr track, float pan);

    /// <summary>Tempo ratio, 1.0 normal, range is 0.25 to 4.0.</summary>
    /// <param name="track"></param>
    /// <param name="ratio"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_tempo(IntPtr track, float ratio);

    /// <summary>Pitch shift in semitones, -24 to +24.</summary>
    /// <param name="track"></param>
    /// <param name="semitones"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_pitch(IntPtr track, float semitones);

    /// <summary>
    /// Nails the soundtouch stage on for the whole track life so it goes through the stretcher from the
    /// very first block even at unity, instead of switching in from the zero latency bypass later.
    /// Tempo/pitch capable sources set this once when they bind, otherwise the first tempo change
    /// clicks, comb filters and drifts the track out of sync.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="enabled">nonzero pins it on</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_stretch_always_on(IntPtr track, int enabled);

    /// <summary>Mute, 0.0 off and 1.0 on.</summary>
    /// <param name="track"></param>
    /// <param name="muted"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_mute(IntPtr track, float muted);

    /// <summary>
    /// How many output frames the track emits as silence before it starts pulling its source, so its
    /// entry is delayed sample accurately against the shared clock. 0 clears it.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="frames"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_start_delay_frames(IntPtr track, ulong frames);

    /// <summary>
    /// Per track channel routing: source channel i lands on physical output map[i], and any output
    /// the map doesn't name gets nothing from this track. len 0 clears the routing.
    /// </summary>
    /// <param name="track"></param>
    /// <param name="map">first zero based output channel index</param>
    /// <param name="len">how many source channels the map covers</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_output_channel_map(
        IntPtr track,
        in uint map,
        nuint len);

    /// <summary>
    /// Drops the routing, back to the straight i to i mix.
    /// </summary>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_clear_output_channel_map(IntPtr track);

    #endregion

    #region Track source feed

    /// <summary>
    /// Lock free ring feeding the track, the write side handle comes back in outSource.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="capacitySamples">interleaved f32 samples, sampleRate * channels * latencySeconds</param>
    /// <param name="outSource"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_set_ring_source(
        IntPtr mixer,
        IntPtr track,
        nuint capacitySamples,
        out IntPtr outSource);

    /// <summary>
    /// Pushes interleaved f32 into the feed. Rt safe and never blocks, a full ring simply takes less.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="samples">first sample to push</param>
    /// <param name="sampleCount"></param>
    /// <param name="outWritten">how many samples actually landed there</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_source_write(
        IntPtr source,
        in float samples,
        nuint sampleCount,
        out nuint outWritten);

    /// <summary>
    /// Room left in the ring right now, in samples.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="outFree"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_source_free_samples(
        IntPtr source,
        out nuint outFree);

    /// <summary>
    /// Drops the track's source, it goes silent.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_clear_source(IntPtr mixer, IntPtr track);

    /// <summary>
    /// Kills the write handle and its ring producer. Zero handle is fine.
    /// </summary>
    /// <param name="source"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_track_source_destroy(IntPtr source);

    #endregion

    #region File-backed track source

    /// <summary>
    /// Opens a file and puts a decoding source on the track. Decode and resample happen on a native
    /// prefetch thread and feed the track straight from the audio thread, no managed pump anywhere.
    /// Looping and eof are native too, poll them through the returned handle.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="path">utf8 path</param>
    /// <param name="targetSampleRate">0 keeps the source rate</param>
    /// <param name="targetChannels">0 keeps the source layout</param>
    /// <param name="prefetchFrames">ring capacity in frames, 0 takes the default</param>
    /// <param name="outSource"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ownaudio_v1_track_open_file(
        IntPtr mixer,
        IntPtr track,
        string path,
        uint targetSampleRate,
        uint targetChannels,
        nuint prefetchFrames,
        out IntPtr outSource);

    /// <summary>Seamless looping on the file source.</summary>
    /// <param name="source"></param>
    /// <param name="enabled">nonzero loops, zero stops at eof</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_file_source_set_loop(IntPtr source, byte enabled);

    /// <summary>
    /// 1 when the file ran out and isn't looping, 0 while it still has something.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="outFinished"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_file_source_is_finished(IntPtr source, out byte outFinished);

    /// <summary>Asks for a seek, position is in output frames.</summary>
    /// <param name="source"></param>
    /// <param name="framePosition"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_file_source_seek(IntPtr source, ulong framePosition);

    /// <summary>Kills the file source handle. Zero handle is fine.</summary>
    /// <param name="source"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_file_source_destroy(IntPtr source);

    /// <summary>
    /// Copies an interleaved f32 buffer into native memory and serves the track from there. The copy
    /// happens once on the control thread, never on the audio path, and after that the managed side is
    /// only a remote control. Looping and eof are handled natively.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="samples">first interleaved f32 sample</param>
    /// <param name="sampleCount">frames * channels</param>
    /// <param name="channels"></param>
    /// <param name="loopEnabled"></param>
    /// <param name="outSource"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_open_memory(
        IntPtr mixer,
        IntPtr track,
        in float samples,
        nuint sampleCount,
        uint channels,
        byte loopEnabled,
        out IntPtr outSource);

    /// <summary>Seamless looping on the memory source.</summary>
    /// <param name="source"></param>
    /// <param name="enabled"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_memory_source_set_loop(IntPtr source, byte enabled);

    /// <summary>
    /// 1 when the buffer ran out and isn't looping.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="outFinished"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_memory_source_is_finished(IntPtr source, out byte outFinished);

    /// <summary>Seek on the memory source, output frames.</summary>
    /// <param name="source"></param>
    /// <param name="framePosition"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_memory_source_seek(IntPtr source, ulong framePosition);

    /// <summary>Kills the memory source handle.</summary>
    /// <param name="source"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_memory_source_destroy(IntPtr source);

    /// <summary>
    /// Wires device capture straight into the track's ring, no managed callback in the way, so audio
    /// data never crosses into managed code. Starts paused.
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="deviceName">utf8 name, null takes the default device</param>
    /// <param name="sampleRate"></param>
    /// <param name="channels">capture and track channel count</param>
    /// <param name="bufferFrames">0 lets the engine pick</param>
    /// <param name="outInput"></param>
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

    /// <summary>Starts or resumes the capture feeding the track.</summary>
    /// <param name="input"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_source_play(IntPtr input);

    /// <summary>Pauses the capture.</summary>
    /// <param name="input"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_source_pause(IntPtr input);

    /// <summary>Last capture peaks.</summary>
    /// <param name="input"></param>
    /// <param name="outLeft"></param>
    /// <param name="outRight"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_input_source_get_peaks(
        IntPtr input,
        out float outLeft,
        out float outRight);

    /// <summary>Kills the input source handle and stops the capture with it.</summary>
    /// <param name="input"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_input_source_destroy(IntPtr input);

    #endregion
}
