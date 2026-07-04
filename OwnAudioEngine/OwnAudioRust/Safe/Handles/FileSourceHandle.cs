using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native file-source control handle returned by
/// <c>ownaudio_v1_track_open_file</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_file_source_destroy</c> even if
/// <c>Dispose</c> is not called. Destroying the handle only releases the control
/// block; the decoding source itself lives on the audio thread until the track's
/// source is cleared or the track is removed, at which point the source and its
/// native prefetch thread are retired off the real-time path.
/// </para>
/// </remarks>
public sealed class FileSourceHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid file-source handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public FileSourceHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_file_source_destroy(handle);
        return true;
    }

    #endregion
}
