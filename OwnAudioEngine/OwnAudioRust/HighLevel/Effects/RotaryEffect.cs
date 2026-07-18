using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Rotary speaker / Leslie cabinet sim, rust side DSP.
/// </summary>
public sealed class RotaryEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamHornSpeed = 2;
    private const uint ParamRotorSpeed = 3;
    private const uint ParamIntensity = 4;
    private const uint ParamIsFast = 5;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private float _hornSpeed = 6.0f;
    private float _rotorSpeed = 1.0f;
    private float _intensity = 0.7f;
    private bool _isFast;

    internal RotaryEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Rotary;

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
    /// Treble horn spin in Hz, 2.0 - 15.0.
    /// </summary>
    public float HornSpeed
    {
        get => _hornSpeed;
        set { _hornSpeed = value; _setParam(ParamHornSpeed, value); }
    }

    /// <summary>
    /// Bass rotor spin in Hz, 0.5 - 5.0. Always slower than the horn.
    /// </summary>
    public float RotorSpeed
    {
        get => _rotorSpeed;
        set { _rotorSpeed = value; _setParam(ParamRotorSpeed, value); }
    }

    /// <summary>Effect intensity, 0.0 - 1.0.</summary>
    public float Intensity
    {
        get => _intensity;
        set { _intensity = value; _setParam(ParamIntensity, value); }
    }

    /// <summary>
    /// The chorale/tremolo switch, false is slow.
    /// </summary>
    public bool IsFast
    {
        get => _isFast;
        set { _isFast = value; _setParam(ParamIsFast, value ? 1f : 0f); }
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
