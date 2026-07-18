using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Soft knee dynamic range compressor sitting on the rust DSP.
/// </summary>
public sealed class CompressorEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamThreshold = 2;
    private const uint ParamRatio = 3;
    private const uint ParamAttack = 4;
    private const uint ParamRelease = 5;
    private const uint ParamMakeup = 6;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _thresholdDb = -6.0f;
    private float _ratio = 4.0f;
    private float _attackMs = 100.0f;
    private float _releaseMs = 200.0f;
    private float _makeupDb;

    internal CompressorEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Compressor;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Where the knee starts, -60 - 0 dB.
    /// </summary>
    public float ThresholdDb
    {
        get => _thresholdDb;
        set { _thresholdDb = value; _setParam(ParamThreshold, value); }
    }

    /// <summary>
    /// Squeeze ratio, 1.0 - 100.0. Anything above ~20 is basically limiting.
    /// </summary>
    public float Ratio
    {
        get => _ratio;
        set { _ratio = value; _setParam(ParamRatio, value); }
    }

    /// <summary>Attack in ms, 0.1 - 1000.</summary>
    public float AttackMs
    {
        get => _attackMs;
        set { _attackMs = value; _setParam(ParamAttack, value); }
    }

    /// <summary>Release in ms, 1 - 2000.</summary>
    public float ReleaseMs
    {
        get => _releaseMs;
        set { _releaseMs = value; _setParam(ParamRelease, value); }
    }

    /// <summary>
    /// Makeup gain we add back after the squeeze, -20 - 20 dB.
    /// </summary>
    public float MakeupDb
    {
        get => _makeupDb;
        set { _makeupDb = value; _setParam(ParamMakeup, value); }
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
