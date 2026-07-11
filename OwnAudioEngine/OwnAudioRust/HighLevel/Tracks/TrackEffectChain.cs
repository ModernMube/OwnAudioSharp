using System;
using System.Collections.Generic;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Manages the ordered list of native Rust-backed effects attached to an <see cref="AudioTrack"/>.
/// </summary>
/// <remarks>
/// <para>
/// Effects are processed in the order they were added (chain topology).
/// All add/remove operations are forwarded immediately to the native mixer.
/// </para>
/// <para>
/// This class is not thread-safe; access must be serialized by the caller.
/// </para>
/// </remarks>
public sealed class TrackEffectChain
{
    #region Fields

    private readonly IntPtr _mixerHandle;
    private readonly IntPtr _trackHandle;
    private readonly List<object> _effects = new();
    private readonly List<EffectHandle> _handles = new();

    #endregion

    #region Construction

    internal TrackEffectChain(IntPtr mixerHandle, IntPtr trackHandle)
    {
        _mixerHandle = mixerHandle;
        _trackHandle = trackHandle;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Gets a read-only view of the current effects in chain order.
    /// </summary>
    public IReadOnlyList<object> Effects => _effects.AsReadOnly();

    /// <summary>
    /// Adds a new effect of the given type to the end of the chain.
    /// </summary>
    /// <param name="effectType">Type of effect to create.</param>
    /// <param name="sampleRate">Sample rate in Hz; used to size DSP buffers.</param>
    /// <returns>The newly created effect instance.</returns>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native call fails.
    /// </exception>
    public object Add(EffectType effectType, float sampleRate)
    {
        int code = OwnAudioNative.ownaudio_v1_track_add_effect(
            _mixerHandle,
            _trackHandle,
            (uint)effectType,
            sampleRate,
            out IntPtr rawEffect);

        ErrorCodeMapper.ThrowIfError(code, nameof(Add));

        var handle = new EffectHandle();
        System.Runtime.InteropServices.Marshal.InitHandle(handle, rawEffect);

        object effect = CreateWrapper(effectType, handle);
        _effects.Add(effect);
        _handles.Add(handle);
        return effect;
    }

    /// <summary>
    /// Adds an external VST3 plugin to the end of the track chain as a native effect. The plugin is
    /// created and controlled by the managed control plane; the audio thread only forwards each block
    /// to <paramref name="processFn"/> with the opaque <paramref name="pluginHandle"/>.
    /// </summary>
    /// <param name="pluginHandle">Opaque plugin instance handle; must outlive the effect.</param>
    /// <param name="processFn">The host's <c>VST3Plugin_ProcessAudio</c> function pointer.</param>
    /// <param name="maxChannels">Largest channel count the track will present.</param>
    /// <param name="maxBlockSize">Largest block size in samples per channel.</param>
    /// <param name="latencySamples">Plugin processing latency in frames, for delay compensation.</param>
    /// <returns>An opaque token identifying the native effect (for <see cref="Remove(object)"/>).</returns>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">Thrown when the native call fails.</exception>
    public object AddVst(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels, uint maxBlockSize, uint latencySamples)
    {
        int code = OwnAudioNative.ownaudio_v1_track_add_vst_effect(
            _mixerHandle,
            _trackHandle,
            pluginHandle,
            processFn,
            maxChannels,
            maxBlockSize,
            latencySamples,
            out IntPtr rawEffect);

        ErrorCodeMapper.ThrowIfError(code, nameof(AddVst));

        var handle = new EffectHandle();
        System.Runtime.InteropServices.Marshal.InitHandle(handle, rawEffect);

        object token = new NativeVstEffect();
        _effects.Add(token);
        _handles.Add(handle);
        return token;
    }

    /// <summary>
    /// Adds a new effect to the end of the chain, inferring its type from the
    /// requested wrapper type and returning the strongly-typed instance.
    /// </summary>
    /// <typeparam name="T">
    /// The effect wrapper type (for example <see cref="Ownaudio.Audio.Effects.ChorusEffect"/>),
    /// so the caller can set its parameters without a cast.
    /// </typeparam>
    /// <param name="sampleRate">Sample rate in Hz; used to size DSP buffers.</param>
    /// <returns>The newly created, strongly-typed effect instance.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <typeparamref name="T"/> is not a known effect wrapper type.
    /// </exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native call fails.
    /// </exception>
    public T Add<T>(float sampleRate) where T : class
    {
        if (!EffectTypeByWrapper.TryGetValue(typeof(T), out EffectType effectType))
        {
            throw new ArgumentException($"Unknown effect wrapper type: {typeof(T).Name}", nameof(T));
        }

        return (T)Add(effectType, sampleRate);
    }

    /// <summary>
    /// Removes and disposes the effect at the given index.
    /// </summary>
    /// <param name="index">Zero-based index of the effect to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="index"/> is out of range.
    /// </exception>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _effects.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _effects.RemoveAt(index);
        _handles.RemoveAt(index);
    }

    /// <summary>
    /// Removes all effects from the chain.
    /// </summary>
    public void Clear()
    {
        _effects.Clear();
        _handles.Clear();
    }

    /// <summary>
    /// Sets a native parameter (by numeric id) on an effect previously returned by
    /// <see cref="Add(EffectType,float)"/>. Used to mirror a managed effect's parameters onto its
    /// paired native track effect.
    /// </summary>
    /// <param name="effect">The effect instance returned by <c>Add</c>.</param>
    /// <param name="paramId">Native parameter identifier (effect-specific).</param>
    /// <param name="value">New parameter value; clamped silently natively.</param>
    /// <returns><see langword="true"/> when the effect is known and the parameter recognised.</returns>
    public bool SetParam(object effect, uint paramId, float value)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0)
        {
            return false;
        }

        int code = OwnAudioNative.ownaudio_v1_effect_set_param(
            _mixerHandle,
            _handles[index].DangerousGetHandle(),
            paramId,
            value);
        return code == 0;
    }

    /// <summary>
    /// Reads a native parameter (by numeric id) back from an effect (the control-side shadow value).
    /// </summary>
    /// <param name="effect">The effect instance returned by <c>Add</c>.</param>
    /// <param name="paramId">Native parameter identifier.</param>
    /// <returns>The current value, or <see langword="null"/> when the effect or parameter is unknown.</returns>
    public float? GetParam(object effect, uint paramId)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0)
        {
            return null;
        }

        int code = OwnAudioNative.ownaudio_v1_effect_get_param(
            _mixerHandle,
            _handles[index].DangerousGetHandle(),
            paramId,
            out float value);
        return code == 0 ? value : null;
    }

    /// <summary>
    /// Removes a specific effect instance (returned by <see cref="Add(EffectType,float)"/>) from the
    /// native track chain and invalidates its handle. No-op when the effect is not in this chain.
    /// </summary>
    /// <param name="effect">The effect instance to remove.</param>
    public void Remove(object effect)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0)
        {
            return;
        }

        EffectHandle handle = _handles[index];
        int code = OwnAudioNative.ownaudio_v1_effect_remove(
            _mixerHandle,
            _trackHandle,
            handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(Remove));

        // The native remove freed the effect box; stop the SafeHandle from destroying it again.
        handle.SetHandleAsInvalid();
        _effects.RemoveAt(index);
        _handles.RemoveAt(index);
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Maps each effect wrapper type to its <see cref="EffectType"/> so the
    /// generic <see cref="Add{T}"/> can resolve the native effect id.
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

    private object CreateWrapper(EffectType effectType, EffectHandle handle)
    {
        return effectType switch
        {
            EffectType.Reverb     => new ReverbEffect(handle, _mixerHandle),
            EffectType.Equalizer  => new EqualizerEffect(handle, _mixerHandle),
            EffectType.Compressor => new CompressorEffect(handle, _mixerHandle),
            EffectType.Limiter    => new LimiterEffect(handle, _mixerHandle),
            EffectType.Delay      => new DelayEffect(handle, _mixerHandle),
            EffectType.Chorus     => new ChorusEffect(handle, _mixerHandle),
            EffectType.Distortion => new DistortionEffect(handle, _mixerHandle),
            EffectType.Overdrive  => new OverdriveEffect(handle, _mixerHandle),
            EffectType.Flanger    => new FlangerEffect(handle, _mixerHandle),
            EffectType.Phaser     => new PhaserEffect(handle, _mixerHandle),
            EffectType.Rotary     => new RotaryEffect(handle, _mixerHandle),
            EffectType.AutoGain   => new AutoGainEffect(handle, _mixerHandle),
            EffectType.Enhancer   => new EnhancerEffect(handle, _mixerHandle),
            EffectType.Gate       => new GateEffect(handle, _mixerHandle),
            EffectType.PitchShift => new PitchShiftEffect(handle, _mixerHandle),
            EffectType.DynamicAmp  => new DynamicAmpEffect(handle, _mixerHandle),
            EffectType.Equalizer30 => new Equalizer30Effect(handle, _mixerHandle),
            // The SmartMaster composite has no strongly-typed managed wrapper; the managed
            // SmartMasterEffect is the parameter model and mirrors onto this native effect by id.
            EffectType.SmartMaster => new NativeSmartMasterEffect(),
            _ => throw new ArgumentOutOfRangeException(nameof(effectType)),
        };
    }

    #endregion
}
