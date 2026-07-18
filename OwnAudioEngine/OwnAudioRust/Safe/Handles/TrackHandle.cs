using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Track pointer from ownaudio_v1_track_create. Freeing it drops the native wrapper but
/// leaves the track sitting in the mixer — ownaudio_v1_track_remove comes first.
/// </summary>
public sealed class TrackHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public TrackHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_track_destroy(handle);
        return true;
    }
}
