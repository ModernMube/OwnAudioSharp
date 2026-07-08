using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native memory-source control handle returned by
/// <c>ownaudio_v1_track_open_memory</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_memory_source_destroy</c> even if
/// <c>Dispose</c> is not called. Destroying the handle only releases the control
/// block; the serving source (and its interleaved buffer) itself lives on the
/// audio thread until the track's source is cleared or the track is removed, at
/// which point it is retired off the real-time path.
/// </para>
/// </remarks>
public sealed class MemorySourceHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid memory-source handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public MemorySourceHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_memory_source_destroy(handle);
        return true;
    }

    #endregion
}
