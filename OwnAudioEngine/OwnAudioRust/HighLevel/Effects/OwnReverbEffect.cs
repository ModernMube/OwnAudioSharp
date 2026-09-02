using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// The 16 line FDN reverb on the rust engine. Diffusion, per line damping, modulated
/// taps and a sidechain ducker. Setters push straight down, getters read our copy.
/// </summary>
public sealed class OwnReverbEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamPreDelay = 2;
    private const uint ParamDecay = 3;
    private const uint ParamSize = 4;
    private const uint ParamDamping = 5;
    private const uint ParamLowDamping = 6;
    private const uint ParamDiffusion = 7;
    private const uint ParamModRate = 8;
    private const uint ParamModDepth = 9;
    private const uint ParamWidth = 10;
    private const uint ParamEarlyLevel = 11;
    private const uint ParamLateLevel = 12;
    private const uint ParamDuckDepth = 13;
    private const uint ParamDuckAttack = 14;
    private const uint ParamDuckRelease = 15;
    private const uint ParamFreeze = 16;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.3f;
    private float _preDelay = 20.0f;
    private float _decay = 2.5f;
    private float _size = 1.0f;
    private float _damping = 0.5f;
    private float _lowDamping = 0.15f;
    private float _diffusion = 0.7f;
    private float _modRate = 0.8f;
    private float _modDepth = 0.4f;
    private float _width = 1.2f;
    private float _earlyLevel = 0.35f;
    private float _lateLevel = 1.0f;
    private float _duckDepth;
    private float _duckAttack = 12.0f;
    private float _duckRelease = 250.0f;
    private bool _freeze;

    internal OwnReverbEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.OwnReverb;

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

    /// <summary>Pre-delay in ms, 0 - 250.</summary>
    public float PreDelay
    {
        get => _preDelay;
        set { _preDelay = value; _setParam(ParamPreDelay, value); }
    }

    /// <summary>
    /// RT60 tail length in seconds, 0.1 - 20. This is the undamped figure, the two
    /// damping controls shorten what you actually hear on top of it.
    /// </summary>
    public float Decay
    {
        get => _decay;
        set { _decay = value; _setParam(ParamDecay, value); }
    }

    /// <summary>
    /// Room size, 0.25 - 2.0. Scales every delay line, so don't automate it while
    /// a tail is ringing - the read taps jump and it clicks.
    /// </summary>
    public float Size
    {
        get => _size;
        set { _size = value; _setParam(ParamSize, value); }
    }

    /// <summary>High damping, 0.0 - 1.0. Higher kills the air faster.</summary>
    public float Damping
    {
        get => _damping;
        set { _damping = value; _setParam(ParamDamping, value); }
    }

    /// <summary>Low damping, 0.0 - 1.0. Thins the bottom out of the tail.</summary>
    public float LowDamping
    {
        get => _lowDamping;
        set { _lowDamping = value; _setParam(ParamLowDamping, value); }
    }

    /// <summary>Input diffusion, 0.0 - 1.0.</summary>
    public float Diffusion
    {
        get => _diffusion;
        set { _diffusion = value; _setParam(ParamDiffusion, value); }
    }

    /// <summary>Tail modulation rate in Hz, 0.05 - 5.0.</summary>
    public float ModRate
    {
        get => _modRate;
        set { _modRate = value; _setParam(ParamModRate, value); }
    }

    /// <summary>Modulation depth, 0.0 - 1.0 (up to about 3 ms).</summary>
    public float ModDepth
    {
        get => _modDepth;
        set { _modDepth = value; _setParam(ParamModDepth, value); }
    }

    /// <summary>Stereo width of the wet signal, 0.0 - 2.0.</summary>
    public float Width
    {
        get => _width;
        set { _width = value; _setParam(ParamWidth, value); }
    }

    /// <summary>Early reflection level, 0.0 - 1.0.</summary>
    public float EarlyLevel
    {
        get => _earlyLevel;
        set { _earlyLevel = value; _setParam(ParamEarlyLevel, value); }
    }

    /// <summary>Late tail level, 0.0 - 1.0.</summary>
    public float LateLevel
    {
        get => _lateLevel;
        set { _lateLevel = value; _setParam(ParamLateLevel, value); }
    }

    /// <summary>
    /// How hard the dry signal ducks the wet, 0.0 is off.
    /// </summary>
    public float DuckDepth
    {
        get => _duckDepth;
        set { _duckDepth = value; _setParam(ParamDuckDepth, value); }
    }

    /// <summary>Ducker attack in ms, 1 - 200.</summary>
    public float DuckAttack
    {
        get => _duckAttack;
        set { _duckAttack = value; _setParam(ParamDuckAttack, value); }
    }

    /// <summary>Ducker release in ms, 10 - 2000.</summary>
    public float DuckRelease
    {
        get => _duckRelease;
        set { _duckRelease = value; _setParam(ParamDuckRelease, value); }
    }

    /// <summary>
    /// Holds the tail forever and mutes the input into the tank.
    /// </summary>
    public bool Freeze
    {
        get => _freeze;
        set { _freeze = value; _setParam(ParamFreeze, value ? 1f : 0f); }
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
