using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Ring-buffer producer side of a track (ownaudio_v1_track_set_ring_source). Dropping it
/// only kills the writer — the reader on the audio thread keeps going and underruns to
/// silence once the buffered samples run out.
/// </summary>
public sealed class TrackSourceHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public TrackSourceHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_track_source_destroy(handle);
        return true;
    }
}
