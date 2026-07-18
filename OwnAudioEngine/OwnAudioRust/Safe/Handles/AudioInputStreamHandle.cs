using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Capture stream handle from ownaudio_v1_open_input_stream.
/// Pause it (or never start it) before this goes away.
/// </summary>
public sealed class AudioInputStreamHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke writes the real pointer here.
    /// </summary>
    public AudioInputStreamHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_input_stream_destroy(handle);
        return true;
    }
}
