using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Control block for a group source (ownaudio_v1_track_open_group). Destroying it frees the
/// control block only — the clips keep living on the audio thread until the track's source is
/// cleared or the track goes away, and only then get retired off the RT path.
/// </summary>
public sealed class GroupSourceHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_group_source_destroy(ptr);
}
