using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// The shared capture bridge (ownaudio_v1_capture_open) - one input stream fanned out to
/// several tracks. Killing it stops capture and drops the stream; attached tracks keep
/// their ring readers and just underrun into silence.
/// </summary>
public sealed class CaptureHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr)
    {
        OwnAudioNative.ownaudio_v1_capture_close(ptr);
    }
}
