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

    #endregion
}
