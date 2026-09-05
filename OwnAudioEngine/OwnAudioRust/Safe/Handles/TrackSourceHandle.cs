using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Ring-buffer producer side of a track (ownaudio_v1_track_set_ring_source). Dropping it
/// only kills the writer — the reader on the audio thread keeps going and underruns to
/// silence once the buffered samples run out.
/// </summary>
public sealed class TrackSourceHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_track_source_destroy(ptr);
}
