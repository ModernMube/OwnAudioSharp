using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native decoder handle returned by <c>ownaudio_v1_decoder_open</c>.
/// </summary>
/// <remarks>
/// Guarantees release via <c>ownaudio_v1_decoder_destroy</c> even if <c>Dispose</c> is not
/// called. Destroying the handle stops and joins the native prefetch thread.
/// </remarks>
public sealed class StreamingDecoderHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid decoder handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public StreamingDecoderHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_decoder_destroy(handle);
        return true;
    }

    #endregion
}
