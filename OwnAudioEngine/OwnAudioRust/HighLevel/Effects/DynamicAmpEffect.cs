using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Adaptive dynamic amplifier backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// <para>
/// Uses dual-window IIR RMS detection (fast + slow), hysteresis noise gate,
/// and attack/release gain smoothing with a per-block gain-change rate limit.
/// This mirrors the C# <c>DynamicAmpEffect</c> algorithm ported to Rust.
/// </para>
/// </remarks>
public sealed class DynamicAmpEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled            = 0;
    private const uint ParamMix               = 1;
    private const uint ParamTargetRmsDb       = 2;
    private const uint ParamAttackTime        = 3;
    private const uint ParamReleaseTime       = 4;
    private const uint ParamNoiseGateDb       = 5;
    private const uint ParamMaxGain           = 6;
    private const uint ParamMaxGainReductionDb = 7;
    private const uint ParamRmsWindowSeconds  = 8;
    private const uint ParamMaxGainChangeDbS  = 9;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled            = true;
    private float _mix                  = 1.0f;
    private float _targetRmsDb          = -12.0f;
    private float _attackTime           = 0.30f;
    private float _releaseTime          = 1.50f;
    private float _noiseGateDb          = -50.0f;
    private float _maxGain              = 6.0f;
    private float _maxGainReductionDb   = 12.0f;
    private float _rmsWindowSeconds     = 0.5f;
    private float _maxGainChangeDbS     = 12.0f;

    #endregion

    #region Construction

    internal DynamicAmpEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.DynamicAmp;

    /// <summary>Gets or sets whether the effect is active.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Gets or sets the dry/wet mix (always 1.0 for DynamicAmp).</summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; SetParam(ParamMix, value); }
    }

    /// <summary>Gets or sets the target RMS level in dB (−60 to −3).</summary>
    public float TargetRmsDb
    {
        get => _targetRmsDb;
        set { _targetRmsDb = value; SetParam(ParamTargetRmsDb, value); }
    }

    /// <summary>Gets or sets the attack time in seconds (min 0.05).</summary>
    public float AttackTime
    {
        get => _attackTime;
        set { _attackTime = value; SetParam(ParamAttackTime, value); }
    }

    /// <summary>Gets or sets the release time in seconds (min 0.2).</summary>
    public float ReleaseTime
    {
        get => _releaseTime;
        set { _releaseTime = value; SetParam(ParamReleaseTime, value); }
    }

    /// <summary>Gets or sets the noise gate threshold in dB (−80 to −30).</summary>
    public float NoiseGateDb
    {
        get => _noiseGateDb;
        set { _noiseGateDb = value; SetParam(ParamNoiseGateDb, value); }
    }

    /// <summary>Gets or sets the maximum linear gain multiplier (1.0–20.0).</summary>
    public float MaxGain
    {
        get => _maxGain;
        set { _maxGain = value; SetParam(ParamMaxGain, value); }
    }

    /// <summary>Gets or sets the maximum gain reduction in dB (3–40).</summary>
    public float MaxGainReductionDb
    {
        get => _maxGainReductionDb;
        set { _maxGainReductionDb = value; SetParam(ParamMaxGainReductionDb, value); }
    }

    /// <summary>Gets or sets the RMS averaging window in seconds (min 0.01).</summary>
    public float RmsWindowSeconds
    {
        get => _rmsWindowSeconds;
        set { _rmsWindowSeconds = value; SetParam(ParamRmsWindowSeconds, value); }
    }

    /// <summary>Gets or sets the maximum gain change rate in dB per second (min 1.0).</summary>
    public float MaxGainChangeDbPerSecond
    {
        get => _maxGainChangeDbS;
        set { _maxGainChangeDbS = value; SetParam(ParamMaxGainChangeDbS, value); }
    }

    #endregion

    #region Private helpers

    private void SetParam(uint paramId, float value)
    {
        if (_disposed) return;
        OwnAudioNative.ownaudio_v1_effect_set_param(_mixerHandle, _handle.DangerousGetHandle(), paramId, value);
    }

    #endregion

    #region IDisposable

    /// <summary>Releases the native effect handle.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    #endregion
}
