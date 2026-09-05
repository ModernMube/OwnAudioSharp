using System;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// The master-bus counterpart of <see cref="TrackEffectChain"/>: effects sitting on the
/// fully summed mix, after every track is rendered. They belong to no track.
/// </summary>
public sealed class MasterEffectChain : NativeEffectChain
{
    internal MasterEffectChain(IntPtr mixerHandle) : base(mixerHandle) { }

    /// <inheritdoc />
    private protected override int AddNative(EffectType effectType, float sampleRate, out IntPtr rawEffect)
        => OwnAudioNative.ownaudio_v1_mixer_add_master_effect(_mixerHandle, (uint)effectType, sampleRate, out rawEffect);

    /// <inheritdoc />
    private protected override int AddVstNative(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels,
                                                uint maxBlockSize, uint latencySamples, out IntPtr rawEffect)
        => OwnAudioNative.ownaudio_v1_mixer_add_master_vst_effect(_mixerHandle, pluginHandle, processFn,
                                                                  maxChannels, maxBlockSize, latencySamples, out rawEffect);

    /// <inheritdoc />
    private protected override int RemoveNative(IntPtr rawEffect)
        => OwnAudioNative.ownaudio_v1_mixer_remove_master_effect(_mixerHandle, rawEffect);
}
