using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Loudness based level rider on the rust engine: BS.1770 detector, gated programme estimate,
/// tolerance window, smoothed rate limited gain and a true-peak look-ahead limiter.
/// Setters push straight down to the native effect, getters read our copy.
/// </summary>
public sealed class OwnDynamicAmpEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamTargetLoudness = 2;
    private const uint ParamWindow = 3;
    private const uint ParamMaxBoost = 4;
    private const uint ParamMaxCut = 5;
    private const uint ParamRiseRate = 6;
    private const uint ParamFallRate = 7;
    private const uint ParamTolerance = 8;
    private const uint ParamSmoothing = 9;
    private const uint ParamRelativeGate = 10;
    private const uint ParamFreezeThreshold = 11;
    private const uint ParamCeiling = 12;
    private const uint ParamLimiter = 13;
    private const uint ParamLookahead = 14;
    private const uint ParamLimiterRelease = 15;
    private const uint ParamInitialGain = 16;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private float _targetLoudness = -14.0f;
    private float _window = 8.0f;
    private float _maxBoost = 12.0f;
    private float _maxCut = 12.0f;
    private float _riseRate = 1.5f;
    private float _fallRate = 3.0f;
    private float _tolerance = 1.0f;
    private float _smoothing = 300.0f;
    private float _relativeGate = -10.0f;
    private float _freezeThreshold = -55.0f;
    private float _ceiling = -1.0f;
    private bool _limiterEnabled = true;
    private float _lookahead = 5.0f;
    private float _limiterRelease = 150.0f;
    private float _initialGain;

    internal OwnDynamicAmpEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Properties

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.OwnDynamicAmp;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Kept for symmetry, the rider always runs fully wet.</summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>Target programme loudness in LUFS, -40 - -5.</summary>
    public float TargetLoudness
    {
        get => _targetLoudness;
        set { _targetLoudness = value; _setParam(ParamTargetLoudness, value); }
    }

    /// <summary>Loudness memory in seconds, 0.4 - 60.</summary>
    public float Window
    {
        get => _window;
        set { _window = value; _setParam(ParamWindow, value); }
    }

    /// <summary>Maximum boost in dB, 0 - 30.</summary>
    public float MaxBoost
    {
        get => _maxBoost;
        set { _maxBoost = value; _setParam(ParamMaxBoost, value); }
    }

    /// <summary>Maximum cut in dB, 0 - 30.</summary>
    public float MaxCut
    {
        get => _maxCut;
        set { _maxCut = value; _setParam(ParamMaxCut, value); }
    }

    /// <summary>Fastest upward movement in dB/s, 0.1 - 20.</summary>
    public float RiseRate
    {
        get => _riseRate;
        set { _riseRate = value; _setParam(ParamRiseRate, value); }
    }

    /// <summary>Fastest downward movement in dB/s, 0.1 - 40.</summary>
    public float FallRate
    {
        get => _fallRate;
        set { _fallRate = value; _setParam(ParamFallRate, value); }
    }

    /// <summary>Dead band around the target in dB, 0 - 6.</summary>
    public float Tolerance
    {
        get => _tolerance;
        set { _tolerance = value; _setParam(ParamTolerance, value); }
    }

    /// <summary>Gain curve smoothing in ms, 10 - 5000.</summary>
    public float Smoothing
    {
        get => _smoothing;
        set { _smoothing = value; _setParam(ParamSmoothing, value); }
    }

    /// <summary>Relative gate in LU below the programme loudness, -40 - -1.</summary>
    public float RelativeGate
    {
        get => _relativeGate;
        set { _relativeGate = value; _setParam(ParamRelativeGate, value); }
    }

    /// <summary>Absolute freeze threshold in LUFS, -90 - -30.</summary>
    public float FreezeThreshold
    {
        get => _freezeThreshold;
        set { _freezeThreshold = value; _setParam(ParamFreezeThreshold, value); }
    }

    /// <summary>True-peak ceiling in dBTP, -20 - 0.</summary>
    public float Ceiling
    {
        get => _ceiling;
        set { _ceiling = value; _setParam(ParamCeiling, value); }
    }

    /// <summary>True-peak safety limiter on/off.</summary>
    public bool LimiterEnabled
    {
        get => _limiterEnabled;
        set { _limiterEnabled = value; _setParam(ParamLimiter, value ? 1f : 0f); }
    }

    /// <summary>Look-ahead in ms, 1 - 10. This is also the effect's latency.</summary>
    public float Lookahead
    {
        get => _lookahead;
        set { _lookahead = value; _setParam(ParamLookahead, value); }
    }

    /// <summary>Limiter release in ms, 10 - 2000.</summary>
    public float LimiterRelease
    {
        get => _limiterRelease;
        set { _limiterRelease = value; _setParam(ParamLimiterRelease, value); }
    }

    /// <summary>Gain the rider starts from and returns to on reset, dB, -30 - +30.</summary>
    public float InitialGain
    {
        get => _initialGain;
        set { _initialGain = value; _setParam(ParamInitialGain, value); }
    }

    #endregion

    /// <summary>
    /// Drops the native effect handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    private void _setParam(uint paramId, float value)
    {
        if (_disposed) return;
        OwnAudioNative.ownaudio_v1_effect_set_param(_mixerHandle, _handle.DangerousGetHandle(), paramId, value);
    }
}
