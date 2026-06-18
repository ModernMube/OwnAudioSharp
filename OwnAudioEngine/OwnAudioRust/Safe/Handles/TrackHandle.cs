using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native track handle returned by <c>ownaudio_v1_track_create</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_track_destroy</c> even if <c>Dispose</c> is not called.
/// The handle releases the memory of the track wrapper on the native side but does not
/// remove the track from the mixer; call <c>ownaudio_v1_track_remove</c> first.
/// </para>
/// </remarks>
public sealed class TrackHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid track handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public TrackHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_track_destroy(handle);
        return true;
    }

    #endregion
}
