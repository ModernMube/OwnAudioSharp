using System;
using System.Collections.Generic;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// 10-band parametric equalizer backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// <para>
/// ISO center frequencies: 31 Hz, 62 Hz, 125 Hz, 250 Hz, 500 Hz, 1 kHz,
/// 2 kHz, 4 kHz, 8 kHz, 16 kHz.  Gain range per band: −12 dB to +12 dB.
/// </para>
/// </remarks>
public sealed class EqualizerEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled = 0;
    private const uint ParamMix     = 1;
    private const uint ParamBand0   = 2;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix      = 1.0f;
    private readonly float[] _gains = new float[10];

    #endregion

    #region Construction

    internal EqualizerEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Equalizer;

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

    /// <summary>Gets or sets the gain for band 0 (31 Hz) in dB.</summary>
    public float Band0 { get => _gains[0]; set { _gains[0] = value; SetParam(ParamBand0 + 0, value); } }

    /// <summary>Gets or sets the gain for band 1 (62 Hz) in dB.</summary>
    public float Band1 { get => _gains[1]; set { _gains[1] = value; SetParam(ParamBand0 + 1, value); } }

    /// <summary>Gets or sets the gain for band 2 (125 Hz) in dB.</summary>
    public float Band2 { get => _gains[2]; set { _gains[2] = value; SetParam(ParamBand0 + 2, value); } }

    /// <summary>Gets or sets the gain for band 3 (250 Hz) in dB.</summary>
    public float Band3 { get => _gains[3]; set { _gains[3] = value; SetParam(ParamBand0 + 3, value); } }

    /// <summary>Gets or sets the gain for band 4 (500 Hz) in dB.</summary>
    public float Band4 { get => _gains[4]; set { _gains[4] = value; SetParam(ParamBand0 + 4, value); } }

    /// <summary>Gets or sets the gain for band 5 (1 kHz) in dB.</summary>
    public float Band5 { get => _gains[5]; set { _gains[5] = value; SetParam(ParamBand0 + 5, value); } }

    /// <summary>Gets or sets the gain for band 6 (2 kHz) in dB.</summary>
    public float Band6 { get => _gains[6]; set { _gains[6] = value; SetParam(ParamBand0 + 6, value); } }

    /// <summary>Gets or sets the gain for band 7 (4 kHz) in dB.</summary>
    public float Band7 { get => _gains[7]; set { _gains[7] = value; SetParam(ParamBand0 + 7, value); } }

    /// <summary>Gets or sets the gain for band 8 (8 kHz) in dB.</summary>
    public float Band8 { get => _gains[8]; set { _gains[8] = value; SetParam(ParamBand0 + 8, value); } }

    /// <summary>Gets or sets the gain for band 9 (16 kHz) in dB.</summary>
    public float Band9 { get => _gains[9]; set { _gains[9] = value; SetParam(ParamBand0 + 9, value); } }

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
