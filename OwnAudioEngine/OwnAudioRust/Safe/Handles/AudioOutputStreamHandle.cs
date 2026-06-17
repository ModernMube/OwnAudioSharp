using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// <para>
/// Wraps the opaque native output-stream handle returned by <c>ownaudio_v1_open_output_stream</c>.
/// Guarantees release via <c>ownaudio_v1_output_stream_destroy</c> even if <c>Dispose</c> is not called.
/// </para>
/// <para>
/// The stream must be paused (or never started) before this handle is released.
/// </para>
/// </summary>
public sealed class AudioOutputStreamHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid output-stream handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public AudioOutputStreamHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_output_stream_destroy(handle);
        return true;
    }

    #endregion
}
