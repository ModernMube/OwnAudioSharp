using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Real-time semitone pitch shift, backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </remarks>
public sealed class PitchShiftEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled   = 0;
    private const uint ParamMix       = 1;
    private const uint ParamSemitones = 2;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool  _isEnabled = true;
    private float _mix       = 1.0f;
    private float _semitones;

    #endregion

    #region Construction

    internal PitchShiftEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.PitchShift;

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

    /// <summary>Gets or sets the pitch shift in semitones (-12–12).</summary>
    public float Semitones
    {
        get => _semitones;
        set { _semitones = value; SetParam(ParamSemitones, value); }
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
