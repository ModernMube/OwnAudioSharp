using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// The master-bus counterpart of <see cref="TrackEffectChain"/>: effects sitting on the
/// fully summed mix, after every track is rendered. They belong to no track. Not
/// thread-safe, serialize it yourself.
/// </summary>
public sealed class MasterEffectChain
{
    #region Fields

    private readonly IntPtr _mixerHandle;
    private readonly List<object> _effects = new();
    private readonly List<EffectHandle> _handles = new();
    private readonly IReadOnlyList<object> _effectsView;

    #endregion

    #region Construction

    internal MasterEffectChain(IntPtr mixerHandle)
    {
        _mixerHandle = mixerHandle;
        _effectsView = _effects.AsReadOnly();
    }

    #endregion

    #region Public API

    /// <summary>
    /// Read-only view of the master chain, in order.
    /// </summary>
    public IReadOnlyList<object> Effects => _effectsView;

    /// <summary>
    /// Appends a new master effect. sampleRate sizes the DSP buffers.
    /// </summary>
    public object Add(EffectType effectType, float sampleRate)
    {
        int code = OwnAudioNative.ownaudio_v1_mixer_add_master_effect(
            _mixerHandle,
            (uint)effectType,
            sampleRate,
            out IntPtr rawEffect);

        ErrorCodeMapper.ThrowIfError(code, nameof(Add));

        var handle = new EffectHandle();
        Marshal.InitHandle(handle, rawEffect);

        object effect = _createWrapper(effectType, handle);
        _effects.Add(effect);
        _handles.Add(handle);
        return effect;
    }

    /// <summary>
    /// Appends an external VST3 plugin onto the master bus. The audio thread just calls
    /// processFn with the opaque pluginHandle, which has to outlive the effect.
    /// latencySamples goes into the delay compensation.
    /// </summary>
    /// <param name="maxChannels">Widest channel count the master bus will show up with.</param>
    /// <param name="maxBlockSize">Biggest block in samples per channel.</param>
    /// <returns>An opaque token for Remove.</returns>
    public object AddVst(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels, uint maxBlockSize, uint latencySamples)
    {
        int code = OwnAudioNative.ownaudio_v1_mixer_add_master_vst_effect(
            _mixerHandle,
            pluginHandle,
            processFn,
            maxChannels,
            maxBlockSize,
            latencySamples,
            out IntPtr rawEffect);

        ErrorCodeMapper.ThrowIfError(code, nameof(AddVst));

        var handle = new EffectHandle();
        Marshal.InitHandle(handle, rawEffect);

        object token = new NativeVstEffect();
        _effects.Add(token);
        _handles.Add(handle);
        return token;
    }

    /// <summary>
    /// Same as Add, type inferred from the wrapper so you get it back typed.
    /// </summary>
    public T Add<T>(float sampleRate) where T : class
    {
        if (!EffectTypeByWrapper.TryGetValue(typeof(T), out EffectType effectType))
            throw new ArgumentException($"Unknown effect wrapper type: {typeof(T).Name}", nameof(T));

        return (T)Add(effectType, sampleRate);
    }

    /// <summary>
    /// Sets a native parameter by numeric id, so a managed effect can mirror itself onto
    /// its master-bus twin. Values are clamped natively.
    /// </summary>
    /// <returns>false when we don't know that effect.</returns>
    public bool SetParam(object effect, uint paramId, float value)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0) { return false; }

        int code = OwnAudioNative.ownaudio_v1_effect_set_param(
            _mixerHandle,
            _handles[index].DangerousGetHandle(),
            paramId,
            value);
        return code == 0;
    }

    /// <summary>
    /// Reads back what was last set. Mostly for verification.
    /// </summary>
    /// <returns>null when the effect or the param id is unknown.</returns>
    public float? GetParam(object effect, uint paramId)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0) { return null; }

        int code = OwnAudioNative.ownaudio_v1_effect_get_param(
            _mixerHandle,
            _handles[index].DangerousGetHandle(),
            paramId,
            out float value);
        return code == 0 ? value : null;
    }

    /// <summary>
    /// Pulls one specific master effect out of the native chain. No-op when it isn't ours.
    /// </summary>
    public void Remove(object effect)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0) { return; }

        _removeHandleAt(index);
        _effects.RemoveAt(index);
    }

    /// <summary>
    /// Drops the master effect at the given index.
    /// </summary>
    /// <param name="index"></param>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _effects.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        _removeHandleAt(index);
        _effects.RemoveAt(index);
    }

    /// <summary>
    /// Wipes the whole master chain.
    /// </summary>
    public void Clear()
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            _removeHandleAt(i);
        }

        _effects.Clear();
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Native remove already freed the effect box, so we invalidate the SafeHandle
    /// afterwards — otherwise it would destroy it a second time.
    /// </summary>
    private void _removeHandleAt(int index)
    {
        EffectHandle handle = _handles[index];
        int code = OwnAudioNative.ownaudio_v1_mixer_remove_master_effect(
            _mixerHandle,
            handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(RemoveAt));

        handle.SetHandleAsInvalid();
        _handles.RemoveAt(index);
    }

    /// <summary>
    /// Wrapper type → native effect id, so the generic Add can resolve it.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, EffectType> EffectTypeByWrapper =
        new Dictionary<Type, EffectType>
        {
            [typeof(ReverbEffect)]      = EffectType.Reverb,
            [typeof(EqualizerEffect)]   = EffectType.Equalizer,
            [typeof(CompressorEffect)]  = EffectType.Compressor,
            [typeof(LimiterEffect)]     = EffectType.Limiter,
            [typeof(DelayEffect)]       = EffectType.Delay,
            [typeof(ChorusEffect)]      = EffectType.Chorus,
            [typeof(DistortionEffect)]  = EffectType.Distortion,
            [typeof(OverdriveEffect)]   = EffectType.Overdrive,
            [typeof(FlangerEffect)]     = EffectType.Flanger,
            [typeof(PhaserEffect)]      = EffectType.Phaser,
            [typeof(RotaryEffect)]      = EffectType.Rotary,
            [typeof(AutoGainEffect)]    = EffectType.AutoGain,
            [typeof(EnhancerEffect)]    = EffectType.Enhancer,
            [typeof(GateEffect)]        = EffectType.Gate,
            [typeof(PitchShiftEffect)]  = EffectType.PitchShift,
            [typeof(DynamicAmpEffect)]  = EffectType.DynamicAmp,
            [typeof(Equalizer30Effect)] = EffectType.Equalizer30,
        };

    private object _createWrapper(EffectType effectType, EffectHandle handle)
    {
        return effectType switch
        {
            EffectType.Reverb      => new ReverbEffect(handle, _mixerHandle),
            EffectType.Equalizer   => new EqualizerEffect(handle, _mixerHandle),
            EffectType.Compressor  => new CompressorEffect(handle, _mixerHandle),
            EffectType.Limiter     => new LimiterEffect(handle, _mixerHandle),
            EffectType.Delay       => new DelayEffect(handle, _mixerHandle),
            EffectType.Chorus      => new ChorusEffect(handle, _mixerHandle),
            EffectType.Distortion  => new DistortionEffect(handle, _mixerHandle),
            EffectType.Overdrive   => new OverdriveEffect(handle, _mixerHandle),
            EffectType.Flanger     => new FlangerEffect(handle, _mixerHandle),
            EffectType.Phaser      => new PhaserEffect(handle, _mixerHandle),
            EffectType.Rotary      => new RotaryEffect(handle, _mixerHandle),
            EffectType.AutoGain    => new AutoGainEffect(handle, _mixerHandle),
            EffectType.Enhancer    => new EnhancerEffect(handle, _mixerHandle),
            EffectType.Gate        => new GateEffect(handle, _mixerHandle),
            EffectType.PitchShift  => new PitchShiftEffect(handle, _mixerHandle),
            EffectType.DynamicAmp  => new DynamicAmpEffect(handle, _mixerHandle),
            EffectType.Equalizer30 => new Equalizer30Effect(handle, _mixerHandle),
            EffectType.SmartMaster => new NativeSmartMasterEffect(),
            _ => throw new ArgumentOutOfRangeException(nameof(effectType)),
        };
    }

    #endregion
}
