using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Flanger with a short modulated delay and feedback, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class FlangerEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled  = 0;
    private const uint ParamMix      = 1;
    private const uint ParamRate     = 2;
    private const uint ParamDepth    = 3;
    private const uint ParamFeedback = 4;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled = true;
    private float _mix       = 0.5f;
    private float _rate      = 0.5f;
    private float _depth     = 0.8f;
    private float _feedback  = 0.6f;

    #endregion

    #region Construction

    internal FlangerEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Flanger;

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

    /// <summary>Gets or sets the LFO modulation rate in Hz (0.1–5.0).</summary>
    public float Rate
    {
        get => _rate;
        set { _rate = value; SetParam(ParamRate, value); }
    }

    /// <summary>Gets or sets the modulation depth (0.0–1.0).</summary>
    public float Depth
    {
        get => _depth;
        set { _depth = value; SetParam(ParamDepth, value); }
    }

    /// <summary>Gets or sets the feedback amount (0.0–0.95).</summary>
    public float Feedback
    {
        get => _feedback;
        set { _feedback = value; SetParam(ParamFeedback, value); }
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
