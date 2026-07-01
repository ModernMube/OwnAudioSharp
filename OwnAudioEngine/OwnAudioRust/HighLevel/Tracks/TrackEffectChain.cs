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
        return effect;
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
    }

    /// <summary>
    /// Removes all effects from the chain.
    /// </summary>
    public void Clear()
    {
        _effects.Clear();
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
            _ => throw new ArgumentOutOfRangeException(nameof(effectType)),
        };
    }

    #endregion
}
