using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Flanger, short modulated delay with feedback. Rust side DSP.
/// </summary>
public sealed class FlangerEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamRate = 2;
    private const uint ParamDepth = 3;
    private const uint ParamFeedback = 4;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.5f;
    private float _rate = 0.5f;
    private float _depth = 0.8f;
    private float _feedback = 0.6f;

    internal FlangerEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Flanger;

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

    /// <summary>LFO speed in Hz, 0.1 - 5.0.</summary>
    public float Rate
    {
        get => _rate;
        set { _rate = value; _setParam(ParamRate, value); }
    }

    /// <summary>Sweep depth, 0.0 - 1.0.</summary>
    public float Depth
    {
        get => _depth;
        set { _depth = value; _setParam(ParamDepth, value); }
    }

    /// <summary>
    /// Feedback, 0.0 - 0.95. Push it up for the jet sound.
    /// </summary>
    public float Feedback
    {
        get => _feedback;
        set { _feedback = value; _setParam(ParamFeedback, value); }
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
