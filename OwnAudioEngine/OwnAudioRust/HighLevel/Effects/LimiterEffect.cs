using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Look-ahead brickwall limiter, rust side. No mix param, it is always inline.
/// </summary>
public sealed class LimiterEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamThreshold = 2;
    private const uint ParamCeiling = 3;
    private const uint ParamRelease = 4;
    private const uint ParamLookahead = 5;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _thresholdDb = -3.0f;
    private float _ceilingDb = -0.1f;
    private float _releaseMs = 50.0f;
    private float _lookaheadMs = 5.0f;

    internal LimiterEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Limiter;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Where it starts to grab, -20 - 0 dB.</summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set { _thresholdDb = value; _setParam(ParamThreshold, value); }
    }

    /// <summary>
    /// Hard output ceiling, -2 - 0 dB. Nothing gets past this.
    /// </summary>
    public float CeilingDb
    {
        get => _ceilingDb;
        set { _ceilingDb = value; _setParam(ParamCeiling, value); }
    }

    /// <summary>Release in ms, 1 - 1000.</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; _setParam(ParamRelease, value); }
    }

    /// <summary>
    /// Look-ahead window in ms, 1 - 20. This is the latency it costs.
    /// </summary>
    public float LookaheadMs
    {
        get => _lookaheadMs;
        set { _lookaheadMs = value; _setParam(ParamLookahead, value); }
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
