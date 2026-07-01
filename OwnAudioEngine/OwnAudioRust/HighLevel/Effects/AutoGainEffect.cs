using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// RMS-based automatic gain control, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.  AutoGain has no wet/dry mix; it
/// always runs fully wet.
/// </remarks>
public sealed class AutoGainEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled       = 0;
    private const uint ParamTargetLevel   = 2;
    private const uint ParamAttack        = 3;
    private const uint ParamRelease       = 4;
    private const uint ParamMaxGain       = 5;
    private const uint ParamMinGain       = 6;
    private const uint ParamGateThreshold = 7;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled      = true;
    private float _targetLevel    = 0.25f;
    private float _attack         = 0.99f;
    private float _release        = 0.999f;
    private float _maxGain        = 4.0f;
    private float _minGain        = 0.25f;
    private float _gateThreshold  = 0.001f;

    #endregion

    #region Construction

    internal AutoGainEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.AutoGain;

    /// <summary>Gets or sets whether the effect is active.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Gets or sets the target RMS level (0.01–1.0).</summary>
    public float TargetLevel
    {
        get => _targetLevel;
        set { _targetLevel = value; SetParam(ParamTargetLevel, value); }
    }

    /// <summary>Gets or sets the attack coefficient (0.9–0.999; higher = slower attack).</summary>
    public float AttackCoefficient
    {
        get => _attack;
        set { _attack = value; SetParam(ParamAttack, value); }
    }

    /// <summary>Gets or sets the release coefficient (0.9–0.9999; higher = slower release).</summary>
    public float ReleaseCoefficient
    {
        get => _release;
        set { _release = value; SetParam(ParamRelease, value); }
    }

    /// <summary>Gets or sets the maximum gain multiplier (1.0–10.0).</summary>
    public float MaxGain
    {
        get => _maxGain;
        set { _maxGain = value; SetParam(ParamMaxGain, value); }
    }

    /// <summary>Gets or sets the minimum gain multiplier (0.1–1.0).</summary>
    public float MinGain
    {
        get => _minGain;
        set { _minGain = value; SetParam(ParamMinGain, value); }
    }

    /// <summary>Gets or sets the noise gate threshold (0.0001–0.01).</summary>
    public float GateThreshold
    {
        get => _gateThreshold;
        set { _gateThreshold = value; SetParam(ParamGateThreshold, value); }
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
