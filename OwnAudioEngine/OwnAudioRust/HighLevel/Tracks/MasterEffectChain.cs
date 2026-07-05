using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Ownaudio.Audio.Effects;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Tracks;

/// <summary>
/// Manages the ordered list of native Rust-backed effects applied to the mixer's
/// <b>master</b> bus — the fully summed mix, after every track has been rendered.
/// </summary>
/// <remarks>
/// <para>
/// The master counterpart of <see cref="TrackEffectChain"/>: effects are processed in the order
/// they were added, and add/remove operations are forwarded immediately to the native mixer through
/// the master effect FFI (the effects are not owned by any track).
/// </para>
/// <para>
/// This class is not thread-safe; access must be serialized by the caller.
/// </para>
/// </remarks>
public sealed class MasterEffectChain
{
    #region Fields

    private readonly IntPtr _mixerHandle;
    private readonly List<object> _effects = new();
    private readonly List<EffectHandle> _handles = new();

    #endregion

    #region Construction

    internal MasterEffectChain(IntPtr mixerHandle)
    {
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Gets a read-only view of the current master effects in chain order.
    /// </summary>
    public IReadOnlyList<object> Effects => _effects.AsReadOnly();

    /// <summary>
    /// Adds a new effect of the given type to the end of the master chain.
    /// </summary>
    /// <param name="effectType">Type of effect to create.</param>
    /// <param name="sampleRate">Sample rate in Hz; used to size DSP buffers.</param>
    /// <returns>The newly created effect instance.</returns>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">
    /// Thrown when the native call fails.
    /// </exception>
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

        object effect = CreateWrapper(effectType, handle);
        _effects.Add(effect);
        _handles.Add(handle);
        return effect;
    }

    /// <summary>
    /// Adds an external VST3 plugin to the end of the master chain as a native effect. The plugin is
    /// created and controlled by the managed control plane; the audio thread only forwards each block
    /// to <paramref name="processFn"/> with the opaque <paramref name="pluginHandle"/>.
    /// </summary>
    /// <param name="pluginHandle">Opaque plugin instance handle; must outlive the effect.</param>
    /// <param name="processFn">The host's <c>VST3Plugin_ProcessAudio</c> function pointer.</param>
    /// <param name="maxChannels">Largest channel count the master bus will present.</param>
    /// <param name="maxBlockSize">Largest block size in samples per channel.</param>
    /// <returns>An opaque token identifying the native effect (for <see cref="Remove(object)"/>).</returns>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">Thrown when the native call fails.</exception>
    public object AddVst(IntPtr pluginHandle, IntPtr processFn, ushort maxChannels, uint maxBlockSize)
    {
        int code = OwnAudioNative.ownaudio_v1_mixer_add_master_vst_effect(
            _mixerHandle,
            pluginHandle,
            processFn,
            maxChannels,
            maxBlockSize,
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
    /// Adds a new effect to the end of the master chain, inferring its type from the requested
    /// wrapper type and returning the strongly-typed instance.
    /// </summary>
    /// <typeparam name="T">The effect wrapper type (for example <see cref="ReverbEffect"/>).</typeparam>
    /// <param name="sampleRate">Sample rate in Hz; used to size DSP buffers.</param>
    /// <returns>The newly created, strongly-typed effect instance.</returns>
    /// <exception cref="ArgumentException">Thrown when <typeparamref name="T"/> is not a known wrapper.</exception>
    /// <exception cref="Ownaudio.Safe.Exceptions.OwnAudioException">Thrown when the native call fails.</exception>
    public T Add<T>(float sampleRate) where T : class
    {
        if (!EffectTypeByWrapper.TryGetValue(typeof(T), out EffectType effectType))
        {
            throw new ArgumentException($"Unknown effect wrapper type: {typeof(T).Name}", nameof(T));
        }

        return (T)Add(effectType, sampleRate);
    }

    /// <summary>
    /// Sets a native parameter (by numeric id) on a master effect previously returned by
    /// <see cref="Add(EffectType,float)"/>. Used to mirror a managed effect's parameters onto its
    /// paired native effect on the master bus.
    /// </summary>
    /// <param name="effect">The effect instance returned by <c>Add</c>.</param>
    /// <param name="paramId">Native parameter identifier (effect-specific).</param>
    /// <param name="value">New parameter value; clamped silently to the valid range natively.</param>
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
    /// Reads a native parameter (by numeric id) back from a master effect. Returns the control-side
    /// shadow value (what was last set). Mainly useful for verification.
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
    /// Removes a specific master effect instance (returned by <see cref="Add(EffectType,float)"/>)
    /// from the native chain. No-op when the effect is not in this chain.
    /// </summary>
    /// <param name="effect">The effect instance to remove.</param>
    public void Remove(object effect)
    {
        int index = _effects.IndexOf(effect);
        if (index < 0)
        {
            return;
        }

        RemoveHandleAt(index);
        _effects.RemoveAt(index);
    }

    /// <summary>
    /// Removes the master effect at the given index from the native chain and invalidates its handle.
    /// </summary>
    /// <param name="index">Zero-based index of the effect to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="index"/> is out of range.</exception>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _effects.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        RemoveHandleAt(index);
        _effects.RemoveAt(index);
    }

    /// <summary>
    /// Removes all master effects from the native chain.
    /// </summary>
    public void Clear()
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            RemoveHandleAt(i);
        }

        _effects.Clear();
    }

    #endregion

    #region Private helpers

    /// <summary>
    /// Removes the native master effect at <paramref name="index"/> and invalidates the managed
    /// handle so it is not destroyed a second time (the native remove already freed it).
    /// </summary>
    private void RemoveHandleAt(int index)
    {
        EffectHandle handle = _handles[index];
        int code = OwnAudioNative.ownaudio_v1_mixer_remove_master_effect(
            _mixerHandle,
            handle.DangerousGetHandle());
        ErrorCodeMapper.ThrowIfError(code, nameof(RemoveAt));

        // The native remove freed the effect box; stop the SafeHandle from destroying it again.
        handle.SetHandleAsInvalid();
        _handles.RemoveAt(index);
    }

    /// <summary>
    /// Maps each effect wrapper type to its <see cref="EffectType"/> so the generic
    /// <see cref="Add{T}"/> can resolve the native effect id.
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
            _ => throw new ArgumentOutOfRangeException(nameof(effectType)),
        };
    }

    #endregion
}
