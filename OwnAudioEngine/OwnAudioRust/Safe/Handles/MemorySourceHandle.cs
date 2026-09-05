using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Control block for an in-memory source (ownaudio_v1_track_open_memory). Only the control
/// block dies here — the serving source and its interleaved buffer stick around on the
/// audio thread until the track drops it.
/// </summary>
public sealed class MemorySourceHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_memory_source_destroy(ptr);
}
