using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Rotary / Leslie-cabinet speaker simulator, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class RotaryEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled    = 0;
    private const uint ParamMix        = 1;
    private const uint ParamHornSpeed  = 2;
    private const uint ParamRotorSpeed = 3;
    private const uint ParamIntensity  = 4;
    private const uint ParamIsFast     = 5;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled  = true;
    private float _mix        = 1.0f;
    private float _hornSpeed  = 6.0f;
    private float _rotorSpeed = 1.0f;
    private float _intensity  = 0.7f;
    private bool  _isFast;

    #endregion

    #region Construction

    internal RotaryEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Rotary;

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

    /// <summary>Gets or sets the horn rotation speed in Hz (2.0–15.0).</summary>
    public float HornSpeed
    {
        get => _hornSpeed;
        set { _hornSpeed = value; SetParam(ParamHornSpeed, value); }
    }

    /// <summary>Gets or sets the rotor rotation speed in Hz (0.5–5.0).</summary>
    public float RotorSpeed
    {
        get => _rotorSpeed;
        set { _rotorSpeed = value; SetParam(ParamRotorSpeed, value); }
    }

    /// <summary>Gets or sets the effect intensity (0.0–1.0).</summary>
    public float Intensity
    {
        get => _intensity;
        set { _intensity = value; SetParam(ParamIntensity, value); }
    }

    /// <summary>Gets or sets the fast/slow speed switch.</summary>
    public bool IsFast
    {
        get => _isFast;
        set { _isFast = value; SetParam(ParamIsFast, value ? 1f : 0f); }
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
