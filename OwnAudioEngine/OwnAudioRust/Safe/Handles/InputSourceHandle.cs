using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Control block for a live input source (ownaudio_v1_track_open_input). Killing it stops
/// capture and drops the native stream; the track's ring reader stays on the audio thread
/// and just underruns into silence.
/// </summary>
public sealed class InputSourceHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public InputSourceHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_input_source_destroy(handle);
        return true;
    }
}
