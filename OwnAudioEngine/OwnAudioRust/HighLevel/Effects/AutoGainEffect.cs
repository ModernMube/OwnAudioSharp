using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// RMS based auto gain riding, running on the rust side.
/// No wet/dry here, it is always fully wet.
/// </summary>
public sealed class AutoGainEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamTargetLevel = 2;
    private const uint ParamAttack = 3;
    private const uint ParamRelease = 4;
    private const uint ParamMaxGain = 5;
    private const uint ParamMinGain = 6;
    private const uint ParamGateThreshold = 7;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _targetLevel = 0.25f;
    private float _attack = 0.99f;
    private float _release = 0.999f;
    private float _maxGain = 4.0f;
    private float _minGain = 0.25f;
    private float _gateThreshold = 0.001f;

    internal AutoGainEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.AutoGain;

    /// <summary>
    /// Bypass switch.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Target RMS we ride the gain towards, 0.01 - 1.0.
    /// </summary>
    public float TargetLevel
    {
        get => _targetLevel;
        set { _targetLevel = value; _setParam(ParamTargetLevel, value); }
    }

    /// <summary>
    /// Attack smoothing coeff, 0.9 - 0.999. Higher is slower.
    /// </summary>
    public float AttackCoefficient
    {
        get => _attack;
        set { _attack = value; _setParam(ParamAttack, value); }
    }

    /// <summary>
    /// Release smoothing coeff, 0.9 - 0.9999. Higher is slower.
    /// </summary>
    public float ReleaseCoefficient
    {
        get => _release;
        set { _release = value; _setParam(ParamRelease, value); }
    }

    /// <summary>Gain ceiling, 1.0 - 10.0.</summary>
    public float MaxGain
    {
        get => _maxGain;
        set { _maxGain = value; _setParam(ParamMaxGain, value); }
    }

    /// <summary>Gain floor, 0.1 - 1.0.</summary>
    public float MinGain
    {
        get => _minGain;
        set { _minGain = value; _setParam(ParamMinGain, value); }
    }

    /// <summary>
    /// Below this the gate holds the gain, 0.0001 - 0.01.
    /// </summary>
    public float GateThreshold
    {
        get => _gateThreshold;
        set { _gateThreshold = value; _setParam(ParamGateThreshold, value); }
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
