using System;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Control block for a live input source (ownaudio_v1_track_open_input). Killing it stops
/// capture and drops the native stream; the track's ring reader stays on the audio thread
/// and just underruns into silence.
/// </summary>
public sealed class InputSourceHandle : NativePtrHandle
{
    /// <inheritdoc/>
    protected override void Destroy(IntPtr ptr) => OwnAudioNative.ownaudio_v1_input_source_destroy(ptr);
}
