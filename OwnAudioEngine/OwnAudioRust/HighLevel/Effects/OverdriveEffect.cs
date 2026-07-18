using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Asymmetric tube style overdrive, rust side DSP.
/// </summary>
public sealed class OverdriveEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamGain = 2;
    private const uint ParamTone = 3;
    private const uint ParamOutputLevel = 4;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private float _gain = 2.0f;
    private float _tone = 0.5f;
    private float _outputLevel = 0.7f;

    internal OverdriveEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Overdrive;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Dry/wet, 0.0 - 1.0.</summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>Gain into the saturator, 1.0 - 5.0.</summary>
    public float Gain
    {
        get => _gain;
        set { _gain = value; _setParam(ParamGain, value); }
    }

    /// <summary>
    /// Tone tilt, 0.0 - 1.0. Dark to bright.
    /// </summary>
    public float Tone
    {
        get => _tone;
        set { _tone = value; _setParam(ParamTone, value); }
    }

    /// <summary>Output level, 0.1 - 1.0.</summary>
    public float OutputLevel
    {
        get => _outputLevel;
        set { _outputLevel = value; _setParam(ParamOutputLevel, value); }
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
