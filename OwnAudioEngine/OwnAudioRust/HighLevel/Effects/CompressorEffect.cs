using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Dynamic range compressor with soft knee, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class CompressorEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled   = 0;
    private const uint ParamThreshold = 2;
    private const uint ParamRatio     = 3;
    private const uint ParamAttack    = 4;
    private const uint ParamRelease   = 5;
    private const uint ParamMakeup    = 6;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled   = true;
    private float _thresholdDb = -6.0f;
    private float _ratio       = 4.0f;
    private float _attackMs    = 100.0f;
    private float _releaseMs   = 200.0f;
    private float _makeupDb;

    #endregion

    #region Construction

    internal CompressorEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Compressor;

    /// <summary>Gets or sets whether the effect is active.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Gets or sets the threshold in dB (-60–0).</summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set { _thresholdDb = value; SetParam(ParamThreshold, value); }
    }

    /// <summary>Gets or sets the compression ratio (1.0–100.0).</summary>
    public float Ratio
    {
        get => _ratio;
        set { _ratio = value; SetParam(ParamRatio, value); }
    }

    /// <summary>Gets or sets the attack time in milliseconds (0.1–1000).</summary>
    public float AttackMs
    {
        get => _attackMs;
        set { _attackMs = value; SetParam(ParamAttack, value); }
    }

    /// <summary>Gets or sets the release time in milliseconds (1–2000).</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; SetParam(ParamRelease, value); }
    }

    /// <summary>Gets or sets the makeup gain in dB (-20–20).</summary>
    public float MakeupDb
    {
        get => _makeupDb;
        set { _makeupDb = value; SetParam(ParamMakeup, value); }
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
