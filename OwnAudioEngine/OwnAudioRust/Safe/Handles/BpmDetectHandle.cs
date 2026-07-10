using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native BPM detector handle returned by <c>ownaudio_v1_bpm_create</c>.
/// </summary>
/// <remarks>
/// Guarantees release via <c>ownaudio_v1_bpm_destroy</c> even if <c>Dispose</c> is not called.
/// </remarks>
public sealed class BpmDetectHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new, invalid detector handle. The runtime fills in the actual handle value via
    /// P/Invoke <c>out</c> marshaling.
    /// </summary>
    public BpmDetectHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_bpm_destroy(handle);
        return true;
    }
}
