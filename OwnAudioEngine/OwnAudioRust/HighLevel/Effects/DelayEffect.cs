using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Stereo delay / echo with ping-pong and damped repeats, rust side.
/// </summary>
public sealed class DelayEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamTimeMs = 2;
    private const uint ParamFeedback = 3;
    private const uint ParamDamping = 4;
    private const uint ParamPingPong = 5;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.30f;
    private float _timeMs = 375.0f;
    private float _feedback = 0.35f;
    private float _damping = 0.25f;
    private bool _pingPong;

    internal DelayEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Delay;

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
    /// Delay time in ms, 1 - 5000.
    /// </summary>
    public float TimeMs
    {
        get => _timeMs;
        set { _timeMs = value; _setParam(ParamTimeMs, value); }
    }

    /// <summary>
    /// How much comes back around, 0.0 - 1.0.
    /// </summary>
    public float Feedback
    {
        get => _feedback;
        set { _feedback = value; _setParam(ParamFeedback, value); }
    }

    /// <summary>
    /// Damping in the feedback loop, higher gives darker repeats. 0.0 - 1.0.
    /// </summary>
    public float Damping
    {
        get => _damping;
        set { _damping = value; _setParam(ParamDamping, value); }
    }

    /// <summary>
    /// Cross feeds the repeats between L and R.
    /// </summary>
    public bool PingPong
    {
        get => _pingPong;
        set { _pingPong = value; _setParam(ParamPingPong, value ? 1f : 0f); }
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
