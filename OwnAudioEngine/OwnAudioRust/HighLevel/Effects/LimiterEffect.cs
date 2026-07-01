using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Look-ahead brick-wall limiter, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class LimiterEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled   = 0;
    private const uint ParamThreshold = 2;
    private const uint ParamCeiling   = 3;
    private const uint ParamRelease   = 4;
    private const uint ParamLookahead = 5;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled   = true;
    private float _thresholdDb = -3.0f;
    private float _ceilingDb   = -0.1f;
    private float _releaseMs   = 50.0f;
    private float _lookaheadMs = 5.0f;

    #endregion

    #region Construction

    internal LimiterEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Limiter;

    /// <summary>Gets or sets whether the effect is active.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Gets or sets the threshold in dB (-20–0).</summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set { _thresholdDb = value; SetParam(ParamThreshold, value); }
    }

    /// <summary>Gets or sets the output ceiling in dB (-2–0).</summary>
    public float CeilingDb
    {
        get => _ceilingDb;
        set { _ceilingDb = value; SetParam(ParamCeiling, value); }
    }

    /// <summary>Gets or sets the release time in milliseconds (1–1000).</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; SetParam(ParamRelease, value); }
    }

    /// <summary>Gets or sets the look-ahead time in milliseconds (1–20).</summary>
    public float LookaheadMs
    {
        get => _lookaheadMs;
        set { _lookaheadMs = value; SetParam(ParamLookahead, value); }
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
