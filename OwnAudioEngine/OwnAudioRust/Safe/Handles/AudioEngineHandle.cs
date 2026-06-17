using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// <para>
/// Wraps the opaque native engine handle returned by <c>ownaudio_v1_engine_create</c>.
/// Guarantees release via <c>ownaudio_v1_engine_destroy</c> even if <c>Dispose</c> is not called.
/// </para>
/// <para>
/// This class is responsible only for storing and releasing the handle.
/// Creating the handle is the responsibility of <see cref="Ownaudio.Safe.AudioEngine"/>.
/// </para>
/// </summary>
public sealed class AudioEngineHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid engine handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public AudioEngineHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_engine_destroy(handle);
        return true;
    }

    #endregion
}
