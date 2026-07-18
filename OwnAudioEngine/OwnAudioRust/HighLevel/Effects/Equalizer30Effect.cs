using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// 30 band 1/3 octave EQ on the rust engine. ISO centres from 20 Hz up to 16 kHz,
/// every band takes -12 - +12 dB.
/// </summary>
public sealed class Equalizer30Effect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamBand0 = 2;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private readonly float[] _gains = new float[30];

    internal Equalizer30Effect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Equalizer30;

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

    /// <summary>20 Hz, dB.</summary>
    public float Band0 { get => _gains[0]; set { _gains[0] = value; _setParam(ParamBand0 + 0, value); } }
    /// <summary>25 Hz, dB.</summary>
    public float Band1 { get => _gains[1]; set { _gains[1] = value; _setParam(ParamBand0 + 1, value); } }
    /// <summary>31.5 Hz, dB.</summary>
    public float Band2 { get => _gains[2]; set { _gains[2] = value; _setParam(ParamBand0 + 2, value); } }
    /// <summary>40 Hz, dB.</summary>
    public float Band3 { get => _gains[3]; set { _gains[3] = value; _setParam(ParamBand0 + 3, value); } }
    /// <summary>50 Hz, dB.</summary>
    public float Band4 { get => _gains[4]; set { _gains[4] = value; _setParam(ParamBand0 + 4, value); } }
    /// <summary>63 Hz, dB.</summary>
    public float Band5 { get => _gains[5]; set { _gains[5] = value; _setParam(ParamBand0 + 5, value); } }
    /// <summary>80 Hz, dB.</summary>
    public float Band6 { get => _gains[6]; set { _gains[6] = value; _setParam(ParamBand0 + 6, value); } }
    /// <summary>100 Hz, dB.</summary>
    public float Band7 { get => _gains[7]; set { _gains[7] = value; _setParam(ParamBand0 + 7, value); } }
    /// <summary>125 Hz, dB.</summary>
    public float Band8 { get => _gains[8]; set { _gains[8] = value; _setParam(ParamBand0 + 8, value); } }
    /// <summary>160 Hz, dB.</summary>
    public float Band9 { get => _gains[9]; set { _gains[9] = value; _setParam(ParamBand0 + 9, value); } }
    /// <summary>200 Hz, dB.</summary>
    public float Band10 { get => _gains[10]; set { _gains[10] = value; _setParam(ParamBand0 + 10, value); } }
    /// <summary>250 Hz, dB.</summary>
    public float Band11 { get => _gains[11]; set { _gains[11] = value; _setParam(ParamBand0 + 11, value); } }
    /// <summary>315 Hz, dB.</summary>
    public float Band12 { get => _gains[12]; set { _gains[12] = value; _setParam(ParamBand0 + 12, value); } }
    /// <summary>400 Hz, dB.</summary>
    public float Band13 { get => _gains[13]; set { _gains[13] = value; _setParam(ParamBand0 + 13, value); } }
    /// <summary>500 Hz, dB.</summary>
    public float Band14 { get => _gains[14]; set { _gains[14] = value; _setParam(ParamBand0 + 14, value); } }
    /// <summary>630 Hz, dB.</summary>
    public float Band15 { get => _gains[15]; set { _gains[15] = value; _setParam(ParamBand0 + 15, value); } }
    /// <summary>800 Hz, dB.</summary>
    public float Band16 { get => _gains[16]; set { _gains[16] = value; _setParam(ParamBand0 + 16, value); } }
    /// <summary>1 kHz, dB.</summary>
    public float Band17 { get => _gains[17]; set { _gains[17] = value; _setParam(ParamBand0 + 17, value); } }
    /// <summary>1.25 kHz, dB.</summary>
    public float Band18 { get => _gains[18]; set { _gains[18] = value; _setParam(ParamBand0 + 18, value); } }
    /// <summary>1.6 kHz, dB.</summary>
    public float Band19 { get => _gains[19]; set { _gains[19] = value; _setParam(ParamBand0 + 19, value); } }
    /// <summary>2 kHz, dB.</summary>
    public float Band20 { get => _gains[20]; set { _gains[20] = value; _setParam(ParamBand0 + 20, value); } }
    /// <summary>2.5 kHz, dB.</summary>
    public float Band21 { get => _gains[21]; set { _gains[21] = value; _setParam(ParamBand0 + 21, value); } }
    /// <summary>3.15 kHz, dB.</summary>
    public float Band22 { get => _gains[22]; set { _gains[22] = value; _setParam(ParamBand0 + 22, value); } }
    /// <summary>4 kHz, dB.</summary>
    public float Band23 { get => _gains[23]; set { _gains[23] = value; _setParam(ParamBand0 + 23, value); } }
    /// <summary>5 kHz, dB.</summary>
    public float Band24 { get => _gains[24]; set { _gains[24] = value; _setParam(ParamBand0 + 24, value); } }
    /// <summary>6.3 kHz, dB.</summary>
    public float Band25 { get => _gains[25]; set { _gains[25] = value; _setParam(ParamBand0 + 25, value); } }
    /// <summary>8 kHz, dB.</summary>
    public float Band26 { get => _gains[26]; set { _gains[26] = value; _setParam(ParamBand0 + 26, value); } }
    /// <summary>10 kHz, dB.</summary>
    public float Band27 { get => _gains[27]; set { _gains[27] = value; _setParam(ParamBand0 + 27, value); } }
    /// <summary>12.5 kHz, dB.</summary>
    public float Band28 { get => _gains[28]; set { _gains[28] = value; _setParam(ParamBand0 + 28, value); } }
    /// <summary>16 kHz, dB.</summary>
    public float Band29 { get => _gains[29]; set { _gains[29] = value; _setParam(ParamBand0 + 29, value); } }

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
