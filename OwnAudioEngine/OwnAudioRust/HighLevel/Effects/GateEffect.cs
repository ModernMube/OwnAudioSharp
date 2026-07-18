using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Noise gate running in the rust engine.
/// </summary>
public sealed class GateEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamThreshold = 2;
    private const uint ParamAttack = 3;
    private const uint ParamRelease = 4;
    private const uint ParamHold = 5;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private float _thresholdDb = -40.0f;
    private float _attackMs = 1.0f;
    private float _releaseMs = 100.0f;
    private float _holdMs = 50.0f;

    internal GateEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Gate;

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

    /// <summary>
    /// Anything under this gets shut, -80 - 0 dB.
    /// </summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set { _thresholdDb = value; _setParam(ParamThreshold, value); }
    }

    /// <summary>Open time in ms, 0.1 - 100.</summary>
    public float AttackMs
    {
        get => _attackMs;
        set { _attackMs = value; _setParam(ParamAttack, value); }
    }

    /// <summary>Close time in ms, 10 - 2000.</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; _setParam(ParamRelease, value); }
    }

    /// <summary>
    /// How long we keep it open after the signal drops, 0 - 500 ms.
    /// </summary>
    public float HoldMs
    {
        get => _holdMs;
        set { _holdMs = value; _setParam(ParamHold, value); }
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
