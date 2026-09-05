using System;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// The ordered list of native effects hanging off an <see cref="AudioTrack"/>. Processed
/// in insertion order, every add/remove goes straight to the native mixer.
/// </summary>
public sealed class TrackEffectChain : NativeEffectChain
{
    private readonly IntPtr _trackHandle;

    internal TrackEffectChain(IntPtr mixerHandle, IntPtr trackHandle) : base(mixerHandle)
    {
        _trackHandle = trackHandle;
    }

    /// <inheritdoc />
    private protected override int AddNative(EffectType effectType, float sampleRate, out IntPtr rawEffect)
        => OwnAudioNative.ownaudio_v1_track_add_effect(_mixerHandle, _trackHandle, (uint)effectType, sampleRate, out rawEffect);

    /// <inheritdoc />
    private protected override int AddVstNative(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels,
                                                uint maxBlockSize, uint latencySamples, out IntPtr rawEffect)
        => OwnAudioNative.ownaudio_v1_track_add_vst_effect(_mixerHandle, _trackHandle, pluginHandle, processFn,
                                                           maxChannels, maxBlockSize, latencySamples, out rawEffect);

    /// <inheritdoc />
    private protected override int RemoveNative(IntPtr rawEffect)
        => OwnAudioNative.ownaudio_v1_effect_remove(_mixerHandle, _trackHandle, rawEffect);
}
