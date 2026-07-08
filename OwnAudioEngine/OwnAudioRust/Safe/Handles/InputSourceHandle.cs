using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native input-source control handle returned by
/// <c>ownaudio_v1_track_open_input</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_input_source_destroy</c> even if
/// <c>Dispose</c> is not called. Destroying the handle stops capture and releases
/// the native input stream; the track's ring-buffer reader lives on the audio
/// thread until the track's source is cleared or the track is removed, after which
/// it underruns (renders silence).
/// </para>
/// </remarks>
public sealed class InputSourceHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid input-source handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public InputSourceHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_input_source_destroy(handle);
        return true;
    }

    #endregion
}
