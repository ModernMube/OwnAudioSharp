using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Multi voice chorus, the DSP itself lives in the rust engine.
/// </summary>
public sealed class ChorusEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamRate = 2;
    private const uint ParamDepth = 3;
    private const uint ParamVoices = 4;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.5f;
    private float _rate = 1.0f;
    private float _depth = 0.5f;
    private int _voices = 3;

    internal ChorusEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Chorus;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Dry/wet, 0.0 - 1.0.
    /// </summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>
    /// LFO speed in Hz, 0.1 - 10.0.
    /// </summary>
    public float Rate
    {
        get => _rate;
        set { _rate = value; _setParam(ParamRate, value); }
    }

    /// <summary>Modulation depth, 0.0 - 1.0.</summary>
    public float Depth
    {
        get => _depth;
        set { _depth = value; _setParam(ParamDepth, value); }
    }

    /// <summary>
    /// How many detuned voices we run, 2 - 6.
    /// </summary>
    public int Voices
    {
        get => _voices;
        set { _voices = value; _setParam(ParamVoices, value); }
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
