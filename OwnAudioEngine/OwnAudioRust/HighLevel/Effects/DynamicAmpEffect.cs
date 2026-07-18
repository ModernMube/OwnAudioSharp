using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Adaptive dynamic amp: dual window IIR RMS detection, hysteresis gate and
/// rate limited gain smoothing. Same algo as the old managed DynamicAmpEffect,
/// just ported to rust.
/// </summary>
public sealed class DynamicAmpEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamTargetRmsDb = 2;
    private const uint ParamAttackTime = 3;
    private const uint ParamReleaseTime = 4;
    private const uint ParamNoiseGateDb = 5;
    private const uint ParamMaxGain = 6;
    private const uint ParamMaxGainReductionDb = 7;
    private const uint ParamRmsWindowSeconds = 8;
    private const uint ParamMaxGainChangeDbS = 9;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private float _targetRmsDb = -12.0f;
    private float _attackTime = 0.30f;
    private float _releaseTime = 1.50f;
    private float _noiseGateDb = -50.0f;
    private float _maxGain = 6.0f;
    private float _maxGainReductionDb = 12.0f;
    private float _rmsWindowSeconds = 0.5f;
    private float _maxGainChangeDbS = 12.0f;

    internal DynamicAmpEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.DynamicAmp;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Dry/wet. For this one it stays at 1.0 in practice.
    /// </summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>
    /// RMS level we aim for, -60 - -3 dB.
    /// </summary>
    public float TargetRmsDb
    {
        get => _targetRmsDb;
        set { _targetRmsDb = value; _setParam(ParamTargetRmsDb, value); }
    }

    /// <summary>Attack in seconds, 0.05 min.</summary>
    public float AttackTime
    {
        get => _attackTime;
        set { _attackTime = value; _setParam(ParamAttackTime, value); }
    }

    /// <summary>Release in seconds, 0.2 min.</summary>
    public float ReleaseTime
    {
        get => _releaseTime;
        set { _releaseTime = value; _setParam(ParamReleaseTime, value); }
    }

    /// <summary>
    /// Gate threshold, -80 - -30 dB. Under this we stop pushing the gain up.
    /// </summary>
    public float NoiseGateDb
    {
        get => _noiseGateDb;
        set { _noiseGateDb = value; _setParam(ParamNoiseGateDb, value); }
    }

    /// <summary>Linear gain ceiling, 1.0 - 20.0.</summary>
    public float MaxGain
    {
        get => _maxGain;
        set { _maxGain = value; _setParam(ParamMaxGain, value); }
    }

    /// <summary>
    /// How far down we are allowed to pull, 3 - 40 dB.
    /// </summary>
    public float MaxGainReductionDb
    {
        get => _maxGainReductionDb;
        set { _maxGainReductionDb = value; _setParam(ParamMaxGainReductionDb, value); }
    }

    /// <summary>
    /// RMS averaging window in seconds, 0.01 min.
    /// </summary>
    public float RmsWindowSeconds
    {
        get => _rmsWindowSeconds;
        set { _rmsWindowSeconds = value; _setParam(ParamRmsWindowSeconds, value); }
    }

    /// <summary>
    /// Rate limit on the gain in dB/sec, 1.0 min. Keeps it from pumping.
    /// </summary>
    public float MaxGainChangeDbPerSecond
    {
        get => _maxGainChangeDbS;
        set { _maxGainChangeDbS = value; _setParam(ParamMaxGainChangeDbS, value); }
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
