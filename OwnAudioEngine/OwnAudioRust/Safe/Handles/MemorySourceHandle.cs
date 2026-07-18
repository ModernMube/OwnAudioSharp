using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Control block for an in-memory source (ownaudio_v1_track_open_memory). Only the control
/// block dies here — the serving source and its interleaved buffer stick around on the
/// audio thread until the track drops it.
/// </summary>
public sealed class MemorySourceHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public MemorySourceHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_memory_source_destroy(handle);
        return true;
    }
}
