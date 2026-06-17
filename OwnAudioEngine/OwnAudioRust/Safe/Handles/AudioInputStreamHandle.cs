using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// <para>
/// Wraps the opaque native input-stream handle returned by <c>ownaudio_v1_open_input_stream</c>.
/// Guarantees release via <c>ownaudio_v1_input_stream_destroy</c> even if <c>Dispose</c> is not called.
/// </para>
/// <para>
/// The stream must be paused (or never started) before this handle is released.
/// </para>
/// </summary>
public sealed class AudioInputStreamHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid input-stream handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public AudioInputStreamHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_input_stream_destroy(handle);
        return true;
    }

    #endregion
}
