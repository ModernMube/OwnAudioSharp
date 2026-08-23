using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// The shared capture bridge (ownaudio_v1_capture_open) - one input stream fanned out to
/// several tracks. Killing it stops capture and drops the stream; attached tracks keep
/// their ring readers and just underrun into silence.
/// </summary>
public sealed class CaptureHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public CaptureHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_capture_close(handle);
        return true;
    }
}
