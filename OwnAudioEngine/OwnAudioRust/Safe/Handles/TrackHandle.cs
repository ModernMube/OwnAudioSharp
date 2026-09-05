using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Track pointer from ownaudio_v1_track_create. Freeing it drops the native wrapper but
/// leaves the track sitting in the mixer — ownaudio_v1_track_remove comes first.
/// </summary>
public sealed class TrackHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_track_destroy(ptr);
}
