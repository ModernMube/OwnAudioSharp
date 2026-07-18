using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Harmonic enhancer, high-pass then saturation on the rust side.
/// </summary>
public sealed class EnhancerEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamGain = 2;
    private const uint ParamCutoff = 3;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.2f;
    private float _gain = 2.5f;
    private float _cutoff = 4000.0f;

    internal EnhancerEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Enhancer;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Dry/wet, 0.0 - 1.0. A little goes a long way here.
    /// </summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>Gain before the saturator, 0.1 - 10.0.</summary>
    public float Gain
    {
        get => _gain;
        set { _gain = value; _setParam(ParamGain, value); }
    }

    /// <summary>
    /// High-pass corner in Hz, 100 - 20000. Only what is above gets excited.
    /// </summary>
    public float CutoffFrequency
    {
        get => _cutoff;
        set { _cutoff = value; _setParam(ParamCutoff, value); }
    }

    #endregion

    private void _setParam(uint paramId, float value)
    {
        if(_disposed) return;
        OwnAudioNative.ownaudio_v1_effect_set_param(_mixerHandle, _handle.DangerousGetHandle(), paramId, value);
    }

    /// <summary>
    /// Drops the native effect handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }
}
