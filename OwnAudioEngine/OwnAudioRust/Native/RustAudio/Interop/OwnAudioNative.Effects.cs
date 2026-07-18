using System;
using System.Runtime.InteropServices;

namespace Ownaudio.Native.RustAudio.Interop;

/// <summary>
/// Effect and effect chain P/Invokes, track level and master level both. 0 is ok, anything else is an error code.
/// </summary>
internal static partial class OwnAudioNative
{
    #region Effect lifecycle

    /// <summary>
    /// Hangs a new effect on the track chain, handle in outEffect.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="effectType">numeric id, see Enums.NativeEffectType</param>
    /// <param name="sampleRate">Hz, sizes the internal dsp buffers</param>
    /// <param name="outEffect"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_track_add_effect(
        IntPtr mixer,
        IntPtr track,
        uint effectType,
        float sampleRate,
        out IntPtr outEffect);

    /// <summary>
    /// Pulls the effect out of the chain and destroys the handle too.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="effect"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_effect_remove(
        IntPtr mixer,
        IntPtr track,
        IntPtr effect);

    /// <summary>
    /// Just the handle, chain stays as it is.
    /// </summary>
    /// <param name="effect"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial void ownaudio_v1_effect_destroy(IntPtr effect);

    /// <summary>
    /// Same but on the master chain, so it runs once over the summed mix.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="effectType"></param>
    /// <param name="sampleRate"></param>
    /// <param name="outEffect"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_add_master_effect(
        IntPtr mixer,
        uint effectType,
        float sampleRate,
        out IntPtr outEffect);

    /// <summary>
    /// Master counterpart of effect_remove, no track handle needed here.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="effect"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_mixer_remove_master_effect(
        IntPtr mixer,
        IntPtr effect);

    /// <summary>
    /// Puts a vst3 plugin into the track chain as a native effect. Loading and params stay managed,
    /// the audio thread only forwards each block to processFn with the opaque plugin handle.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="track"></param>
    /// <param name="pluginHandle">opaque instance, has to outlive the effect</param>
    /// <param name="processFn">the host's VST3Plugin_ProcessAudio pointer</param>
    /// <param name="maxChannels"></param>
    /// <param name="maxBlockSize">samples per channel</param>
    /// <param name="latencySamples">plugin latency in frames, for pdc</param>
    /// <param name="outEffect"></param>
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
    /// Master version of the vst insert. Remove it with mixer_remove_master_effect.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="pluginHandle"></param>
    /// <param name="processFn"></param>
    /// <param name="maxChannels"></param>
    /// <param name="maxBlockSize"></param>
    /// <param name="latencySamples"></param>
    /// <param name="outEffect"></param>
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
    /// Sets one param by numeric id. Out of range values get clamped without a word, nonzero means
    /// the effect didn't know the id.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="effect"></param>
    /// <param name="paramId">effect specific</param>
    /// <param name="value"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_effect_set_param(
        IntPtr mixer,
        IntPtr effect,
        uint paramId,
        float value);

    /// <summary>
    /// Reads a param back.
    /// </summary>
    /// <param name="mixer"></param>
    /// <param name="effect"></param>
    /// <param name="paramId"></param>
    /// <param name="outValue"></param>
    [LibraryImport(NativeLibraryLoader.LogicalName)]
    internal static partial int ownaudio_v1_effect_get_param(
        IntPtr mixer,
        IntPtr effect,
        uint paramId,
        out float outValue);

    #endregion
}
