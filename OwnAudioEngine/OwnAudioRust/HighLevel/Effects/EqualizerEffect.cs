using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// 10 band parametric EQ on the rust engine. Centres are 31, 62, 125, 250, 500,
/// 1k, 2k, 4k, 8k, 16k Hz, each band goes -12 - +12 dB.
/// </summary>
public sealed class EqualizerEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamBand0 = 2;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 1.0f;
    private readonly float[] _gains = new float[10];

    internal EqualizerEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Equalizer;

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

    /// <summary>31 Hz, dB.</summary>
    public float Band0 { get => _gains[0]; set { _gains[0] = value; _setParam(ParamBand0 + 0, value); } }

    /// <summary>62 Hz, dB.</summary>
    public float Band1 { get => _gains[1]; set { _gains[1] = value; _setParam(ParamBand0 + 1, value); } }

    /// <summary>125 Hz, dB.</summary>
    public float Band2 { get => _gains[2]; set { _gains[2] = value; _setParam(ParamBand0 + 2, value); } }

    /// <summary>250 Hz, dB.</summary>
    public float Band3 { get => _gains[3]; set { _gains[3] = value; _setParam(ParamBand0 + 3, value); } }

    /// <summary>500 Hz, dB.</summary>
    public float Band4 { get => _gains[4]; set { _gains[4] = value; _setParam(ParamBand0 + 4, value); } }

    /// <summary>1 kHz, dB.</summary>
    public float Band5 { get => _gains[5]; set { _gains[5] = value; _setParam(ParamBand0 + 5, value); } }

    /// <summary>2 kHz, dB.</summary>
    public float Band6 { get => _gains[6]; set { _gains[6] = value; _setParam(ParamBand0 + 6, value); } }

    /// <summary>4 kHz, dB.</summary>
    public float Band7 { get => _gains[7]; set { _gains[7] = value; _setParam(ParamBand0 + 7, value); } }

    /// <summary>8 kHz, dB.</summary>
    public float Band8 { get => _gains[8]; set { _gains[8] = value; _setParam(ParamBand0 + 8, value); } }

    /// <summary>16 kHz, dB.</summary>
    public float Band9 { get => _gains[9]; set { _gains[9] = value; _setParam(ParamBand0 + 9, value); } }

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
