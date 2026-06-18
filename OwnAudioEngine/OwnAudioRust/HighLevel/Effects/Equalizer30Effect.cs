using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// 30-band 1/3-octave parametric equalizer backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// <para>
/// ISO 1/3-octave centre frequencies (Hz):
/// 20, 25, 31.5, 40, 50, 63, 80, 100, 125, 160,
/// 200, 250, 315, 400, 500, 630, 800, 1 k, 1.25 k, 1.6 k,
/// 2 k, 2.5 k, 3.15 k, 4 k, 5 k, 6.3 k, 8 k, 10 k, 12.5 k, 16 k.
/// Gain range per band: −12 dB to +12 dB.
/// </para>
/// </remarks>
public sealed class Equalizer30Effect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled = 0;
    private const uint ParamMix    = 1;
    private const uint ParamBand0  = 2;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix      = 1.0f;
    private readonly float[] _gains = new float[30];

    #endregion

    #region Construction

    internal Equalizer30Effect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Equalizer30;

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

    /// <summary>Gets or sets the gain for band 0 (20 Hz) in dB.</summary>
    public float Band0  { get => _gains[0];  set { _gains[0]  = value; SetParam(ParamBand0 +  0, value); } }
    /// <summary>Gets or sets the gain for band 1 (25 Hz) in dB.</summary>
    public float Band1  { get => _gains[1];  set { _gains[1]  = value; SetParam(ParamBand0 +  1, value); } }
    /// <summary>Gets or sets the gain for band 2 (31.5 Hz) in dB.</summary>
    public float Band2  { get => _gains[2];  set { _gains[2]  = value; SetParam(ParamBand0 +  2, value); } }
    /// <summary>Gets or sets the gain for band 3 (40 Hz) in dB.</summary>
    public float Band3  { get => _gains[3];  set { _gains[3]  = value; SetParam(ParamBand0 +  3, value); } }
    /// <summary>Gets or sets the gain for band 4 (50 Hz) in dB.</summary>
    public float Band4  { get => _gains[4];  set { _gains[4]  = value; SetParam(ParamBand0 +  4, value); } }
    /// <summary>Gets or sets the gain for band 5 (63 Hz) in dB.</summary>
    public float Band5  { get => _gains[5];  set { _gains[5]  = value; SetParam(ParamBand0 +  5, value); } }
    /// <summary>Gets or sets the gain for band 6 (80 Hz) in dB.</summary>
    public float Band6  { get => _gains[6];  set { _gains[6]  = value; SetParam(ParamBand0 +  6, value); } }
    /// <summary>Gets or sets the gain for band 7 (100 Hz) in dB.</summary>
    public float Band7  { get => _gains[7];  set { _gains[7]  = value; SetParam(ParamBand0 +  7, value); } }
    /// <summary>Gets or sets the gain for band 8 (125 Hz) in dB.</summary>
    public float Band8  { get => _gains[8];  set { _gains[8]  = value; SetParam(ParamBand0 +  8, value); } }
    /// <summary>Gets or sets the gain for band 9 (160 Hz) in dB.</summary>
    public float Band9  { get => _gains[9];  set { _gains[9]  = value; SetParam(ParamBand0 +  9, value); } }
    /// <summary>Gets or sets the gain for band 10 (200 Hz) in dB.</summary>
    public float Band10 { get => _gains[10]; set { _gains[10] = value; SetParam(ParamBand0 + 10, value); } }
    /// <summary>Gets or sets the gain for band 11 (250 Hz) in dB.</summary>
    public float Band11 { get => _gains[11]; set { _gains[11] = value; SetParam(ParamBand0 + 11, value); } }
    /// <summary>Gets or sets the gain for band 12 (315 Hz) in dB.</summary>
    public float Band12 { get => _gains[12]; set { _gains[12] = value; SetParam(ParamBand0 + 12, value); } }
    /// <summary>Gets or sets the gain for band 13 (400 Hz) in dB.</summary>
    public float Band13 { get => _gains[13]; set { _gains[13] = value; SetParam(ParamBand0 + 13, value); } }
    /// <summary>Gets or sets the gain for band 14 (500 Hz) in dB.</summary>
    public float Band14 { get => _gains[14]; set { _gains[14] = value; SetParam(ParamBand0 + 14, value); } }
    /// <summary>Gets or sets the gain for band 15 (630 Hz) in dB.</summary>
    public float Band15 { get => _gains[15]; set { _gains[15] = value; SetParam(ParamBand0 + 15, value); } }
    /// <summary>Gets or sets the gain for band 16 (800 Hz) in dB.</summary>
    public float Band16 { get => _gains[16]; set { _gains[16] = value; SetParam(ParamBand0 + 16, value); } }
    /// <summary>Gets or sets the gain for band 17 (1 kHz) in dB.</summary>
    public float Band17 { get => _gains[17]; set { _gains[17] = value; SetParam(ParamBand0 + 17, value); } }
    /// <summary>Gets or sets the gain for band 18 (1.25 kHz) in dB.</summary>
    public float Band18 { get => _gains[18]; set { _gains[18] = value; SetParam(ParamBand0 + 18, value); } }
    /// <summary>Gets or sets the gain for band 19 (1.6 kHz) in dB.</summary>
    public float Band19 { get => _gains[19]; set { _gains[19] = value; SetParam(ParamBand0 + 19, value); } }
    /// <summary>Gets or sets the gain for band 20 (2 kHz) in dB.</summary>
    public float Band20 { get => _gains[20]; set { _gains[20] = value; SetParam(ParamBand0 + 20, value); } }
    /// <summary>Gets or sets the gain for band 21 (2.5 kHz) in dB.</summary>
    public float Band21 { get => _gains[21]; set { _gains[21] = value; SetParam(ParamBand0 + 21, value); } }
    /// <summary>Gets or sets the gain for band 22 (3.15 kHz) in dB.</summary>
    public float Band22 { get => _gains[22]; set { _gains[22] = value; SetParam(ParamBand0 + 22, value); } }
    /// <summary>Gets or sets the gain for band 23 (4 kHz) in dB.</summary>
    public float Band23 { get => _gains[23]; set { _gains[23] = value; SetParam(ParamBand0 + 23, value); } }
    /// <summary>Gets or sets the gain for band 24 (5 kHz) in dB.</summary>
    public float Band24 { get => _gains[24]; set { _gains[24] = value; SetParam(ParamBand0 + 24, value); } }
    /// <summary>Gets or sets the gain for band 25 (6.3 kHz) in dB.</summary>
    public float Band25 { get => _gains[25]; set { _gains[25] = value; SetParam(ParamBand0 + 25, value); } }
    /// <summary>Gets or sets the gain for band 26 (8 kHz) in dB.</summary>
    public float Band26 { get => _gains[26]; set { _gains[26] = value; SetParam(ParamBand0 + 26, value); } }
    /// <summary>Gets or sets the gain for band 27 (10 kHz) in dB.</summary>
    public float Band27 { get => _gains[27]; set { _gains[27] = value; SetParam(ParamBand0 + 27, value); } }
    /// <summary>Gets or sets the gain for band 28 (12.5 kHz) in dB.</summary>
    public float Band28 { get => _gains[28]; set { _gains[28] = value; SetParam(ParamBand0 + 28, value); } }
    /// <summary>Gets or sets the gain for band 29 (16 kHz) in dB.</summary>
    public float Band29 { get => _gains[29]; set { _gains[29] = value; SetParam(ParamBand0 + 29, value); } }

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
