using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// BPM detector pointer from ownaudio_v1_bpm_create, freed by the finalizer if need be.
/// </summary>
public sealed class BpmDetectHandle : SafeHandle
{
    /// <summary>
    /// Invalid until the native side hands us a pointer.
    /// </summary>
    public BpmDetectHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_bpm_destroy(handle);
        return true;
    }
}
