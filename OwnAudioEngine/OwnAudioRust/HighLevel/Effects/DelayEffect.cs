using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Stereo delay / echo with ping-pong and damping, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class DelayEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled  = 0;
    private const uint ParamMix      = 1;
    private const uint ParamTimeMs   = 2;
    private const uint ParamFeedback = 3;
    private const uint ParamDamping  = 4;
    private const uint ParamPingPong = 5;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled = true;
    private float _mix       = 0.30f;
    private float _timeMs    = 375.0f;
    private float _feedback  = 0.35f;
    private float _damping   = 0.25f;
    private bool  _pingPong;

    #endregion

    #region Construction

    internal DelayEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Delay;

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

    /// <summary>Gets or sets the delay time in milliseconds (1–5000).</summary>
    public float TimeMs
    {
        get => _timeMs;
        set { _timeMs = value; SetParam(ParamTimeMs, value); }
    }

    /// <summary>Gets or sets the feedback / repeat amount (0.0–1.0).</summary>
    public float Feedback
    {
        get => _feedback;
        set { _feedback = value; SetParam(ParamFeedback, value); }
    }

    /// <summary>Gets or sets the feedback damping (0.0–1.0; higher = darker repeats).</summary>
    public float Damping
    {
        get => _damping;
        set { _damping = value; SetParam(ParamDamping, value); }
    }

    /// <summary>Gets or sets the ping-pong cross-feed mode.</summary>
    public bool PingPong
    {
        get => _pingPong;
        set { _pingPong = value; SetParam(ParamPingPong, value ? 1f : 0f); }
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
