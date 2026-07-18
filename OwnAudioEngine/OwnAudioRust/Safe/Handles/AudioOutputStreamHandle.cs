using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Render stream handle from ownaudio_v1_open_output_stream. Same deal as the input side:
/// pause first, then let it go.
/// </summary>
public sealed class AudioOutputStreamHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke writes the real pointer here.
    /// </summary>
    public AudioOutputStreamHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_output_stream_destroy(handle);
        return true;
    }
}
