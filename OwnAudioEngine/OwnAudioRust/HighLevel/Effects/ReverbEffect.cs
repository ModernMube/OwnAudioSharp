using System;
using System.Collections.Generic;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Exceptions;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// Freeverb-based algorithmic reverb backed by the native Rust DSP engine.
/// </summary>
/// <remarks>
/// <para>
/// All parameters are forwarded immediately to the native effect via
/// <c>ownaudio_v1_effect_set_param</c>.  The property getters return cached values
/// to avoid interop overhead on every UI read.
/// </para>
/// </remarks>
public sealed class ReverbEffect : IDisposable
{
    #region Parameter identifiers

    private const uint ParamEnabled  = 0;
    private const uint ParamMix      = 1;
    private const uint ParamRoomSize = 2;
    private const uint ParamDamping  = 3;
    private const uint ParamWidth    = 4;
    private const uint ParamWetLevel = 5;
    private const uint ParamDryLevel = 6;

    #endregion

    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;

    private bool _isEnabled = true;
    private float _mix      = 0.5f;
    private float _roomSize = 0.5f;
    private float _damping  = 0.5f;
    private float _width    = 1.0f;
    private float _wetLevel = 0.33f;
    private float _dryLevel = 0.67f;

    #endregion

    #region Construction

    internal ReverbEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Reverb;

    /// <summary>Gets or sets whether the effect is active (not bypassed).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(ParamEnabled, value ? 1f : 0f); }
    }

    /// <summary>Gets or sets the dry/wet mix (0.0 = fully dry, 1.0 = fully wet).</summary>
    public float Mix
    {
        get => _mix;
        set { _mix = value; SetParam(ParamMix, value); }
    }

    /// <summary>Gets or sets the room size (0.0–1.0).</summary>
    public float RoomSize
    {
        get => _roomSize;
        set { _roomSize = value; SetParam(ParamRoomSize, value); }
    }

    /// <summary>Gets or sets the damping (0.0–1.0).</summary>
    public float Damping
    {
        get => _damping;
        set { _damping = value; SetParam(ParamDamping, value); }
    }

    /// <summary>Gets or sets the stereo width (0.0–2.0).</summary>
    public float Width
    {
        get => _width;
        set { _width = value; SetParam(ParamWidth, value); }
    }

    /// <summary>Gets or sets the wet signal level (0.0–1.0).</summary>
    public float WetLevel
    {
        get => _wetLevel;
        set { _wetLevel = value; SetParam(ParamWetLevel, value); }
    }

    /// <summary>Gets or sets the dry signal level (0.0–1.0).</summary>
    public float DryLevel
    {
        get => _dryLevel;
        set { _dryLevel = value; SetParam(ParamDryLevel, value); }
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

    /// <summary>
    /// Releases the native effect handle.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _handle.Dispose();
    }

    #endregion
}
