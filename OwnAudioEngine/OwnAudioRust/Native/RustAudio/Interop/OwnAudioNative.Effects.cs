using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// P/Invoke declarations for the native audio effect and track-effect-chain API.
/// </summary>
internal static partial class OwnAudioNative
{
    #region Effect lifecycle

    /// <summary>
    /// Adds a new effect of the given type to the track's effect chain and
    /// writes the resulting handle to <paramref name="outEffect"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="track">Valid track handle.</param>
    /// <param name="effectType">Numeric effect type identifier (<see cref="Enums.NativeEffectType"/>).</param>
    /// <param name="sampleRate">Sample rate in Hz; used to size internal DSP buffers.</param>
    /// <param name="outEffect">Receives the new effect handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_add_effect(
        IntPtr mixer,
        IntPtr track,
        uint effectType,
        float sampleRate,
        out IntPtr outEffect);

    /// <summary>
    /// Removes the effect from the track chain and destroys the handle.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="track">Valid track handle.</param>
    /// <param name="effect">Effect handle to remove and destroy.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_effect_remove(
        IntPtr mixer,
        IntPtr track,
        IntPtr effect);

    /// <summary>
    /// Destroys an effect handle without removing it from the chain.
    /// </summary>
    /// <param name="effect">Effect handle to destroy.</param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_effect_destroy(IntPtr effect);

    /// <summary>
    /// Adds a new effect of the given type to the mixer's master effect chain (applied once over
    /// the fully summed mix) and writes the resulting handle to <paramref name="outEffect"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="effectType">Numeric effect type identifier.</param>
    /// <param name="sampleRate">Sample rate in Hz; used to size internal DSP buffers.</param>
    /// <param name="outEffect">Receives the new master effect handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_add_master_effect(
        IntPtr mixer,
        uint effectType,
        float sampleRate,
        out IntPtr outEffect);

    /// <summary>
    /// Removes a master effect from the mixer's master chain and destroys the handle. The master
    /// counterpart of <see cref="ownaudio_v1_effect_remove"/> (no track handle is required).
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="effect">Master effect handle to remove and destroy.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_remove_master_effect(
        IntPtr mixer,
        IntPtr effect);

    /// <summary>
    /// Adds an external VST3 plugin to a track's effect chain as a native effect. The plugin is
    /// created, loaded and parameter-controlled on the managed control plane; the audio thread only
    /// forwards each block to <paramref name="processFn"/> with the opaque <paramref name="pluginHandle"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="track">Valid track handle.</param>
    /// <param name="pluginHandle">Opaque plugin instance handle; must outlive the effect.</param>
    /// <param name="processFn">The host's <c>VST3Plugin_ProcessAudio</c> function pointer.</param>
    /// <param name="maxChannels">Largest channel count the chain will present.</param>
    /// <param name="maxBlockSize">Largest block size in samples per channel.</param>
    /// <param name="latencySamples">Plugin processing latency in frames, for delay compensation.</param>
    /// <param name="outEffect">Receives the new effect handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_add_vst_effect(
        IntPtr mixer,
        IntPtr track,
        IntPtr pluginHandle,
        IntPtr processFn,
        ushort maxChannels,
        uint maxBlockSize,
        uint latencySamples,
        out IntPtr outEffect);

    /// <summary>
    /// Adds an external VST3 plugin to the mixer's master effect chain (applied once over the fully
    /// summed mix). The master counterpart of <see cref="ownaudio_v1_track_add_vst_effect"/>; the
    /// returned handle is removed with <see cref="ownaudio_v1_mixer_remove_master_effect"/>.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="pluginHandle">Opaque plugin instance handle; must outlive the effect.</param>
    /// <param name="processFn">The host's <c>VST3Plugin_ProcessAudio</c> function pointer.</param>
    /// <param name="maxChannels">Largest channel count the chain will present.</param>
    /// <param name="maxBlockSize">Largest block size in samples per channel.</param>
    /// <param name="latencySamples">Plugin processing latency in frames, for delay compensation.</param>
    /// <param name="outEffect">Receives the new master effect handle on success.</param>
    /// <returns>Zero on success; non-zero error code otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_add_master_vst_effect(
        IntPtr mixer,
        IntPtr pluginHandle,
        IntPtr processFn,
        ushort maxChannels,
        uint maxBlockSize,
        uint latencySamples,
        out IntPtr outEffect);

    #endregion

    #region Effect parameters

    /// <summary>
    /// Sets a parameter on an effect by numeric identifier.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="effect">Valid effect handle.</param>
    /// <param name="paramId">Numeric parameter identifier (effect-specific).</param>
    /// <param name="value">New parameter value; clamped silently to the valid range.</param>
    /// <returns>Zero when the parameter is recognised; non-zero otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_effect_set_param(
        IntPtr mixer,
        IntPtr effect,
        uint paramId,
        float value);

    /// <summary>
    /// Reads the current value of an effect parameter.
    /// </summary>
    /// <param name="mixer">Valid mixer handle.</param>
    /// <param name="effect">Valid effect handle.</param>
    /// <param name="paramId">Numeric parameter identifier.</param>
    /// <param name="outValue">Receives the current value on success.</param>
    /// <returns>Zero when the parameter is recognised; non-zero otherwise.</returns>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_effect_get_param(
        IntPtr mixer,
        IntPtr effect,
        uint paramId,
        out float outValue);

    #endregion
}
