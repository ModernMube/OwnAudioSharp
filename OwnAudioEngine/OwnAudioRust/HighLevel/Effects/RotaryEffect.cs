using System;
using Ownaudio.Native.RustAudio.Interop;
using Ownaudio.Safe.Handles;

namespace Ownaudio.Audio.Effects;

/// <summary>
/// RotaryEffect backed by the native Rust DSP engine.
/// </summary>
public sealed class RotaryEffect : IDisposable
{
    #region Fields

    private readonly EffectHandle _handle;
    private readonly IntPtr _mixerHandle;
    private bool _disposed;
    private bool _isEnabled = true;

    #endregion

    #region Construction

    internal RotaryEffect(EffectHandle handle, IntPtr mixerHandle)
    {
        _handle      = handle;
        _mixerHandle = mixerHandle;
    }

    #endregion

    #region Properties

    /// <summary>Gets the effect type identifier.</summary>
    public EffectType EffectType => EffectType.Rotary;

    /// <summary>Gets or sets whether the effect is active.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set { _isEnabled = value; SetParam(0u, value ? 1f : 0f); }
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
