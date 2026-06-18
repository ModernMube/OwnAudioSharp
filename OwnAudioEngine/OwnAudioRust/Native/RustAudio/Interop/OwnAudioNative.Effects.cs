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
