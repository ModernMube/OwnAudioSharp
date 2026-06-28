using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native track-source write handle returned by
/// <c>ownaudio_v1_track_set_ring_source</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_track_source_destroy</c> even if
/// <c>Dispose</c> is not called.  Destroying the handle drops the ring-buffer
/// producer; the track's reader on the audio thread is undisturbed and simply
/// underruns to silence once the buffered samples are consumed.
/// </para>
/// </remarks>
public sealed class TrackSourceHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid track-source handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public TrackSourceHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_track_source_destroy(handle);
        return true;
    }

    #endregion
}
