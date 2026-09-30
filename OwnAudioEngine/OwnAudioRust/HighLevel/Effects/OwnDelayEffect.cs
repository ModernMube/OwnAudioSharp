using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Tape style stereo delay on the rust engine: in-loop diffusion, filtering, ADAA saturation, ducking, freeze.
/// Setters push straight down to the native effect, getters read our copy.
/// </summary>
public sealed class OwnDelayEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamTimeLeft = 2;
    private const uint ParamTimeRight = 3;
    private const uint ParamFeedback = 4;
    private const uint ParamCrossFeedback = 5;
    private const uint ParamCrossfadeTimeMode = 6;
    private const uint ParamGlide = 7;
    private const uint ParamDrive = 8;
    private const uint ParamLowCut = 9;
    private const uint ParamHighCut = 10;
    private const uint ParamDiffusion = 11;
    private const uint ParamModRate = 12;
    private const uint ParamModDepth = 13;
    private const uint ParamDuckAmount = 14;
    private const uint ParamDuckThreshold = 15;
    private const uint ParamDuckAttack = 16;
    private const uint ParamDuckRelease = 17;
    private const uint ParamWidth = 18;
    private const uint ParamFreeze = 19;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.3f;
    private float _timeLeft = 375.0f;
    private float _timeRight = 500.0f;
    private float _feedback = 0.45f;
    private float _crossFeedback = 0.0f;
    private bool _crossfadeTimeMode;
    private float _glide = 150.0f;
    private float _drive = 0.0f;
    private float _lowCut = 80.0f;
    private float _highCut = 8000.0f;
    private float _diffusion = 0.0f;
    private float _modRate = 0.6f;
    private float _modDepth = 0.0f;
    private float _duckAmount = 0.0f;
    private float _duckThreshold = -30.0f;
    private float _duckAttack = 10.0f;
    private float _duckRelease = 250.0f;
    private float _width = 1.0f;
    private bool _freeze;

    internal OwnDelayEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Properties

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.OwnDelay;

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

    /// <summary>Left delay time in ms, 15 - 4000.</summary>
    public float TimeLeft
    {
        get => _timeLeft;
        set { _timeLeft = value; _setParam(ParamTimeLeft, value); }
    }

    /// <summary>Right delay time in ms, 15 - 4000.</summary>
    public float TimeRight
    {
        get => _timeRight;
        set { _timeRight = value; _setParam(ParamTimeRight, value); }
    }

    /// <summary>Feedback, 0.0 - 1.0.</summary>
    public float Feedback
    {
        get => _feedback;
        set { _feedback = value; _setParam(ParamFeedback, value); }
    }

    /// <summary>Cross feedback, 0.0 - 1.0.</summary>
    public float CrossFeedback
    {
        get => _crossFeedback;
        set { _crossFeedback = value; _setParam(ParamCrossFeedback, value); }
    }

    /// <summary>Crossfade instead of tape glide on time changes.</summary>
    public bool CrossfadeTimeMode
    {
        get => _crossfadeTimeMode;
        set { _crossfadeTimeMode = value; _setParam(ParamCrossfadeTimeMode, value ? 1f : 0f); }
    }

    /// <summary>Glide time constant in ms, 5 - 2000.</summary>
    public float Glide
    {
        get => _glide;
        set { _glide = value; _setParam(ParamGlide, value); }
    }

    /// <summary>Saturation drive in dB, 0 - 24.</summary>
    public float Drive
    {
        get => _drive;
        set { _drive = value; _setParam(ParamDrive, value); }
    }

    /// <summary>In-loop low cut in Hz, 20 - 2000.</summary>
    public float LowCut
    {
        get => _lowCut;
        set { _lowCut = value; _setParam(ParamLowCut, value); }
    }

    /// <summary>In-loop high cut in Hz, 500 - 20000.</summary>
    public float HighCut
    {
        get => _highCut;
        set { _highCut = value; _setParam(ParamHighCut, value); }
    }

    /// <summary>Diffusion, 0.0 - 1.0.</summary>
    public float Diffusion
    {
        get => _diffusion;
        set { _diffusion = value; _setParam(ParamDiffusion, value); }
    }

    /// <summary>Wow rate in Hz, 0.05 - 10.</summary>
    public float ModRate
    {
        get => _modRate;
        set { _modRate = value; _setParam(ParamModRate, value); }
    }

    /// <summary>Wow depth in ms, 0 - 5.</summary>
    public float ModDepth
    {
        get => _modDepth;
        set { _modDepth = value; _setParam(ParamModDepth, value); }
    }

    /// <summary>Ducking amount, 0.0 - 1.0.</summary>
    public float DuckAmount
    {
        get => _duckAmount;
        set { _duckAmount = value; _setParam(ParamDuckAmount, value); }
    }

    /// <summary>Ducking threshold in dBFS, -60 - 0.</summary>
    public float DuckThreshold
    {
        get => _duckThreshold;
        set { _duckThreshold = value; _setParam(ParamDuckThreshold, value); }
    }

    /// <summary>Ducker attack in ms, 0.5 - 200.</summary>
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

    /// <summary>Wet stereo width, 0.0 - 2.0.</summary>
    public float Width
    {
        get => _width;
        set { _width = value; _setParam(ParamWidth, value); }
    }

    /// <summary>Holds the loop forever.</summary>
    public bool Freeze
    {
        get => _freeze;
        set { _freeze = value; _setParam(ParamFreeze, value ? 1f : 0f); }
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
