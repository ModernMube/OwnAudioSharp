using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Capture stream handle from ownaudio_v1_open_input_stream.
/// Pause it (or never start it) before this goes away.
/// </summary>
public sealed class AudioInputStreamHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_input_stream_destroy(ptr);
}
