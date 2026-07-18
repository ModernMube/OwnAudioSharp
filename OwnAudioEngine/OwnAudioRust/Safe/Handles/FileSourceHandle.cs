using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Control block for a file source (ownaudio_v1_track_open_file). Destroying it frees the
/// control block only — the decoder itself keeps living on the audio thread until the
/// track's source is cleared or the track goes away, and only then gets retired off the RT path.
/// </summary>
public sealed class FileSourceHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public FileSourceHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_file_source_destroy(handle);
        return true;
    }
}
