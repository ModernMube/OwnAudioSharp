using System;
using System.Runtime.InteropServices;
using Ownaudio.Native.RustAudio.Interop;

namespace Ownaudio.Safe.Handles;

/// <summary>
/// Mixer pointer from ownaudio_v1_mixer_create. Every track and effect handle that came out
/// of this mixer has to be disposed first, otherwise you're freeing under their feet.
/// </summary>
public sealed class MixerHandle : SafeHandle
{
    /// <summary>
    /// Invalid until P/Invoke fills it in.
    /// </summary>
    public MixerHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        OwnAudioNative.ownaudio_v1_mixer_destroy(handle);
        return true;
    }
}
