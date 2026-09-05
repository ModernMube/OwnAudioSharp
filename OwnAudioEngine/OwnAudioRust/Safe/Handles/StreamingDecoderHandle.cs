using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Decoder pointer from ownaudio_v1_decoder_open. Releasing stops and joins the native
/// prefetch thread, so it can block for a tick.
/// </summary>
public sealed class StreamingDecoderHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_decoder_destroy(ptr);
}
