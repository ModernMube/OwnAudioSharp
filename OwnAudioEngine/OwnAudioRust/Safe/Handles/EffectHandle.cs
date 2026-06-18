using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native effect handle returned by <c>ownaudio_v1_track_add_effect</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_effect_destroy</c> even if <c>Dispose</c> is not called.
/// The handle releases the effect wrapper memory but does not remove the effect from the
/// track chain; call <c>ownaudio_v1_effect_remove</c> before disposing.
/// </para>
/// </remarks>
public sealed class EffectHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid effect handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public EffectHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_effect_destroy(handle);
        return true;
    }

    #endregion
}
