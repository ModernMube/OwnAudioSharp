using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Everything a native effect chain does regardless of where it hangs. Only the three
/// native calls differ between the track and the master bus, so those are the abstract
/// bits. Not thread-safe, serialize it yourself.
/// </summary>
public abstract class NativeEffectChain
{
    #region Fields

    /// <summary>
    /// The mixer every effect call is addressed to.
    /// </summary>
    private protected readonly IntPtr _mixerHandle;

    private readonly List<object> _effects = new();
    private readonly List<EffectHandle> _handles = new();
    private readonly IReadOnlyList<object> _effectsView;

    #endregion

    #region Construction

    private protected NativeEffectChain(IntPtr mixerHandle)
    {
        _mixerHandle = mixerHandle;
        _effectsView = _effects.AsReadOnly();
    }

    #endregion

    #region Native hooks

    /// <summary>
    /// Adds an effect of the given type on the native side and hands back its pointer.
    /// </summary>
    private protected abstract int AddNative(EffectType effectType, float sampleRate, out IntPtr rawEffect);

    /// <summary>
    /// Same for a VST3 plugin: the audio thread only gets processFn plus the opaque
    /// pluginHandle, which has to outlive the effect.
    /// </summary>
    private protected abstract int AddVstNative(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels,
                                                uint maxBlockSize, uint latencySamples, out IntPtr rawEffect);

    /// <summary>
    /// Unhooks one effect pointer from the native chain.
    /// </summary>
    private protected abstract int RemoveNative(IntPtr rawEffect);

    #endregion

    #region Public API

    /// <summary>
    /// Read-only view of the chain, in order. Same instance every time, it just wraps
    /// the live list.
    /// </summary>
    public IReadOnlyList<object> Effects => _effectsView;

    /// <summary>
    /// Appends a new effect of the given type. sampleRate sizes the DSP buffers.
    /// </summary>
    /// <returns>The freshly built wrapper.</returns>
    public object Add(EffectType effectType, float sampleRate)
    {
        int code = AddNative(effectType, sampleRate, out IntPtr rawEffect);
        ErrorCodeMapper.ThrowIfError(code, nameof(Add));

        var handle = new EffectHandle();
        Marshal.InitHandle(handle, rawEffect);

        object effect = _createWrapper(effectType, handle);
        _effects.Add(effect);
        _handles.Add(handle);
        return effect;
    }

    /// <summary>
    /// Appends an external VST3 plugin as a native effect. latencySamples feeds the delay
    /// compensation.
    /// </summary>
    /// <param name="maxChannels">Widest channel count this chain will show up with.</param>
    /// <param name="maxBlockSize">Biggest block in samples per channel.</param>
    /// <returns>An opaque token for Remove.</returns>
    public object AddVst(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels, uint maxBlockSize, uint latencySamples)
    {
        int code = AddVstNative(pluginHandle, processFn, maxChannels, maxBlockSize, latencySamples, out IntPtr rawEffect);
        ErrorCodeMapper.ThrowIfError(code, nameof(AddVst));

        var handle = new EffectHandle();
        Marshal.InitHandle(handle, rawEffect);

        object token = new NativeVstEffect();
        _effects.Add(token);
        _handles.Add(handle);
        return token;
    }

    /// <summary>
    /// Same as Add, but figures out the type from the wrapper you asked for so you get
    /// it back without a cast.
    /// </summary>
    public T Add<T>(float sampleRate) where T : class
    {
        if (!EffectTypeByWrapper.TryGetValue(typeof(T), out EffectType effectType))
            throw new ArgumentException($"Unknown effect wrapper type: {typeof(T).Name}", nameof(T));

        return (T)Add(effectType, sampleRate);
    }

    /// <summary>
    /// Sets a native parameter by numeric id — this is how a managed effect mirrors
    /// itself onto its native twin. Out-of-range values get clamped down there.
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
    /// Clears the effect's internal state on the native side, params left alone.
    /// </summary>
    /// <returns>false when we don't know that effect.</returns>
    public bool Reset(object effect)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0) { return false; }

        int code = OwnAudioNative.ownaudio_v1_effect_reset(
            _mixerHandle,
            _handles[index].DangerousGetHandle());
        return code == 0;
    }

    /// <summary>
    /// Reads a native parameter back — the control-side shadow value, mostly for checks.
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
    /// Pulls one specific effect instance out of the native chain. No-op when it isn't
    /// ours.
    /// </summary>
    public void Remove(object effect)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0) { return; }

        _removeHandleAt(index, nameof(Remove));
        _effects.RemoveAt(index);
    }

    /// <summary>
    /// Drops the effect at the given index.
    /// </summary>
    /// <param name="index"></param>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _effects.Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        _removeHandleAt(index, nameof(RemoveAt));
        _effects.RemoveAt(index);
    }

    /// <summary>
    /// Empties the chain, native side included.
    /// </summary>
    public void Clear()
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            _removeHandleAt(i, nameof(Clear));
        }

        _effects.Clear();
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Native remove already freed the effect box, so we invalidate the SafeHandle
    /// afterwards — otherwise it would destroy it a second time. op only shapes the
    /// error message.
    /// </summary>
    private void _removeHandleAt(int index, string op)
    {
        EffectHandle handle = _handles[index];
        int code = RemoveNative(handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, op);

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
            [typeof(OwnReverbEffect)]   = EffectType.OwnReverb,
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
            EffectType.OwnReverb   => new OwnReverbEffect(handle, _mixerHandle),
            _ => throw new ArgumentOutOfRangeException(nameof(effectType)),
        };
    }

    #endregion
}
