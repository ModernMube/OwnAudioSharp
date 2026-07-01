using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Noise gate, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class GateEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled   = 0;
    private const uint ParamMix       = 1;
    private const uint ParamThreshold = 2;
    private const uint ParamAttack    = 3;
    private const uint ParamRelease   = 4;
    private const uint ParamHold      = 5;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled   = true;
    private float _mix         = 1.0f;
    private float _thresholdDb = -40.0f;
    private float _attackMs    = 1.0f;
    private float _releaseMs   = 100.0f;
    private float _holdMs      = 50.0f;

    #endregion

    #region Construction

    internal GateEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Gate;

    /// <summary>Gets or sets whether the effect is active.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Gets or sets the dry/wet mix (0.0–1.0).</summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; SetParam(ParamMix, value); }
    }

    /// <summary>Gets or sets the threshold in dB (-80–0).</summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set { _thresholdDb = value; SetParam(ParamThreshold, value); }
    }

    /// <summary>Gets or sets the attack time in milliseconds (0.1–100).</summary>
    public float AttackMs
    {
        get => _attackMs;
        set { _attackMs = value; SetParam(ParamAttack, value); }
    }

    /// <summary>Gets or sets the release time in milliseconds (10–2000).</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; SetParam(ParamRelease, value); }
    }

    /// <summary>Gets or sets the hold time in milliseconds (0–500).</summary>
    public float HoldMs
    {
        get => _holdMs;
        set { _holdMs = value; SetParam(ParamHold, value); }
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
