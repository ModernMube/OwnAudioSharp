using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Wraps the opaque native mixer handle returned by <c>ownaudio_v1_mixer_create</c>.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees release via <c>ownaudio_v1_mixer_destroy</c> even if <c>Dispose</c> is not called.
/// All track and effect handles obtained from this mixer must be disposed before disposing this handle.
/// </para>
/// </remarks>
public sealed class MixerHandle : SafeHandle
{
    #region Construction

    /// <summary>
    /// Initializes a new, invalid mixer handle.
    /// The runtime fills in the actual handle value via P/Invoke <c>out</c> marshaling.
    /// </summary>
    public MixerHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    #endregion

    #region SafeHandle overrides

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_mixer_destroy(handle);
        return true;
    }

    #endregion
}
