using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Freeverb style algorithmic reverb on the rust engine. Every setter pushes
/// straight down to the native effect, the getters just read back our copy.
/// </summary>
public sealed class ReverbEffect : IDisposable
{
    private const uint ParamEnabled = 0;
    private const uint ParamMix = 1;
    private const uint ParamRoomSize = 2;
    private const uint ParamDamping = 3;
    private const uint ParamWidth = 4;
    private const uint ParamWetLevel = 5;
    private const uint ParamDryLevel = 6;

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix = 0.5f;
    private float _roomSize = 0.5f;
    private float _damping = 0.5f;
    private float _width = 1.0f;
    private float _wetLevel = 0.33f;
    private float _dryLevel = 0.67f;

    internal ReverbEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle = handle;
        _mixerHandle = mixerHandle;
    }

    #region Propertyes

    /// <summary>
    /// Which native effect this wrapper drives.
    /// </summary>
    public EffectType EffectType => EffectType.Reverb;

    /// <summary>Bypass switch.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; _setParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>
    /// Dry/wet, 0.0 is fully dry and 1.0 fully wet.
    /// </summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; _setParam(ParamMix, value); }
    }

    /// <summary>Room size, 0.0 - 1.0.</summary>
    public float RoomSize
    {
        get => _roomSize;
        set { _roomSize = value; _setParam(ParamRoomSize, value); }
    }

    /// <summary>
    /// Damping in the tail, 0.0 - 1.0. Higher kills the highs faster.
    /// </summary>
    public float Damping
    {
        get => _damping;
        set { _damping = value; _setParam(ParamDamping, value); }
    }

    /// <summary>Stereo width, 0.0 - 2.0.</summary>
    public float Width
    {
        get => _width;
        set { _width = value; _setParam(ParamWidth, value); }
    }

    /// <summary>Wet level, 0.0 - 1.0.</summary>
    public float WetLevel
    {
        get => _wetLevel;
        set { _wetLevel = value; _setParam(ParamWetLevel, value); }
    }

    /// <summary>Dry level, 0.0 - 1.0.</summary>
    public float DryLevel
    {
        get => _dryLevel;
        set { _dryLevel = value; _setParam(ParamDryLevel, value); }
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
