using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Render stream handle from ownaudio_v1_open_output_stream. Same deal as the input side:
/// pause first, then let it go.
/// </summary>
public sealed class AudioOutputStreamHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_output_stream_destroy(ptr);
}
